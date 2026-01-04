using FriendsAchievementFeed.Models;
using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Common;

namespace FriendsAchievementFeed.Services
{
    public class AchievementFeedService
    {
        private readonly object _runLock = new object();
        private CancellationTokenSource _activeRunCts;

        public event EventHandler<ProgressReport> RebuildProgress;

        private ProgressReport _lastProgress;
        private string _lastStatus;

        public ProgressReport GetLastRebuildProgress() => _lastProgress;
        public string GetLastRebuildStatus() => _lastStatus;

        public bool IsRebuilding
        {
            get { lock (_runLock) return _activeRunCts != null; }
        }

        private readonly IPlayniteAPI _api;
        private readonly FriendsAchievementFeedSettings _settings;
        private readonly ILogger _logger;
        private readonly FriendsAchievementFeedPlugin _plugin;
        private readonly ICacheService _cacheService;

        private readonly ISteamDataProvider _steam;
        private readonly FeedEntryFactory _entryFactory;
        private readonly CacheRebuildService _rebuildService;

        // Hydrates friend-only cached entries to UI FeedEntry (overlay self at UI time)
        private readonly FeedEntryHydrator _hydrator;

        // Progress mapping delegated to helper
        private readonly RebuildProgressMapper _progressMapper;
        private readonly SettingsPersistenceService _settingsPersistence;
        public ICacheService Cache => _cacheService;

        public event EventHandler CacheChanged
        {
            add => _cacheService.CacheChanged += value;
            remove => _cacheService.CacheChanged -= value;
        }

        public AchievementFeedService(
            IPlayniteAPI api,
            FriendsAchievementFeedSettings settings,
            ILogger logger,
            FriendsAchievementFeedPlugin plugin)
        {
            _api = api;
            _settings = settings;
            _logger = logger;
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));

            _cacheService = new CacheService(api, logger, _plugin);

            _steam = new SteamDataProvider(_api, _logger, _plugin, _cacheService);
            _entryFactory = new FeedEntryFactory();
            _hydrator = new FeedEntryHydrator(_cacheService, _entryFactory);

            _rebuildService = new CacheRebuildService(_steam, _entryFactory, _cacheService, _settings, _logger, _api);
            _progressMapper = new RebuildProgressMapper();
            _settingsPersistence = new SettingsPersistenceService(_settings, _plugin, _cacheService, _logger, PostToUi);

            try { _cacheService.CacheChanged += _settingsPersistence.OnCacheChanged; }
            catch (Exception e)
            {
                _logger?.Error(e, ResourceProvider.GetString("LOCFriendsAchFeed_Error_RefreshFeedAfterCacheChange"));
            }
        }

        // -----------------------------
        // UI helpers
        // -----------------------------

        private void PostToUi(Action action)
        {
            var dispatcher = _api?.MainView?.UIDispatcher;
            dispatcher.InvokeIfNeeded(action, DispatcherPriority.Background);
        }

        private void Report(string message, int current = 0, int total = 0, bool canceled = false)
        {
            var report = new ProgressReport
            {
                Message = message,
                CurrentStep = current,
                TotalSteps = total,
                IsCanceled = canceled
            };

            _lastProgress = report;
            if (!string.IsNullOrWhiteSpace(message))
                _lastStatus = message;

            var handler = RebuildProgress;
            if (handler == null) return;

            PostToUi(() =>
            {
                try { handler(this, report); }
                catch (Exception e)
                {
                    _logger?.Error(e, ResourceProvider.GetString("LOCFriendsAchFeed_Error_NotifySubscribers"));
                }
            });
        }

        // -----------------------------
        // Steam auth (public API)
        // -----------------------------

        public Task<(bool Success, string Message)> TestSteamAuthAsync()
            => _steam.TestSteamAuthAsync(_settings.SteamUserId);

        public Task<List<Models.SteamFriend>> GetFriendsAsync()
            => _steam.GetFriendsAsync(_settings.SteamUserId, _settings.SteamApiKey, cancel: CancellationToken.None);

        // -----------------------------
        // Scan option builders
        // -----------------------------

        private static CacheScanOptions RebuildOptions(CacheRebuildOptions options)
        {
            options ??= new CacheRebuildOptions();
            return new CacheScanOptions
            {
                IncludeSelf = true,
                IncludeFriends = true,
                IncludeUnownedFriendIds = options.FamilySharingFriendIDs,
            };
        }

        private static CacheScanOptions SingleGameOptions(Guid playniteGameId)
        {
            return new CacheScanOptions
            {
                IncludeSelf = false,
                IncludeFriends = true,
                PlayniteGameIds = new[] { playniteGameId },
                ExplicitAppsAllowUnownedDiscovery = true
            };
        }

        // Quick incremental scan: up to N most-recent friends (by cached unlock activity),
        // up to M most-recent games per friend (by cached unlock activity), bounded to N*M pairs.
        private static CacheScanOptions IncrementalOptions(int recentFriends, int recentGamesPerFriend)
        {
            return new CacheScanOptions
            {
                IncludeSelf = true,
                IncludeFriends = true,

                QuickScanRecentPairs = true,
                QuickScanRecentFriendsCount = Math.Max(0, recentFriends),
                QuickScanRecentGamesPerFriend = Math.Max(0, recentGamesPerFriend),

                // Keep consistent with other entrypoints.
                ExplicitAppsAllowUnownedDiscovery = true
            };
        }

        // -----------------------------
        // Centralized progress mapping (via helper)
        // -----------------------------

        private void HandleUpdate(CacheRebuildService.RebuildUpdate update)
        {
            var mapped = _progressMapper.Map(update);
            if (mapped == null)
            {
                return;
            }

            Report(mapped.Message, mapped.CurrentStep, mapped.TotalSteps, mapped.IsCanceled);
        }

        // -----------------------------
        // Managed scan runner (single implementation)
        // -----------------------------

        private bool TryBeginRun(out CancellationTokenSource cts)
        {
            lock (_runLock)
            {
                if (_activeRunCts != null)
                {
                    cts = null;
                    Report(_lastStatus ?? ResourceProvider.GetString("LOCFriendsAchFeed_Status_UpdatingCache"), 0, 1);
                    return false;
                }

                _activeRunCts = new CancellationTokenSource();
                cts = _activeRunCts;
                return true;
            }
        }

        private void EndRun()
        {
            lock (_runLock)
            {
                _activeRunCts?.Dispose();
                _activeRunCts = null;
            }
        }

        private void ResetRunState()
        {
            _progressMapper.Reset();
        }

        private async Task RunManagedAsync(
            Func<CancellationToken, Task<CacheRebuildService.RebuildPayload>> runner,
            Func<CacheRebuildService.RebuildPayload, string> finalMessage,
            string errorLogMessage)
        {
            if (!TryBeginRun(out var cts))
                return;

            ResetRunState();

            try
            {
                try
                {
                    var refreshed = await _steam.RefreshCookiesAsync(cts.Token).ConfigureAwait(false);
                    if (!refreshed)
                    {
                        _logger?.Debug("[FAF] Steam cookie refresh before scan returned no cookies.");
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger?.Debug(ex, "[FAF] Steam cookie refresh before scan failed; continuing.");
                }

                // Unified progress callback for both scan modes
                Action<CacheRebuildService.RebuildUpdate> onUpdate = HandleUpdate;

                var payload = await runner(cts.Token).ConfigureAwait(false);

                var msg = finalMessage?.Invoke(payload) ?? ResourceHelper.ErrorFailedRebuild;
                _lastStatus = msg;
                Report(msg, 1, 1);
            }
            catch (OperationCanceledException)
            {
                var msg = ResourceHelper.RebuildCanceled;
                _lastStatus = msg;
                Report(msg, 0, 1, canceled: true);
                _logger?.Debug(ResourceHelper.DebugRebuildCanceled);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, errorLogMessage);

                var msg = ResourceProvider.GetString("LOCFriendsAchFeed_Error_FailedRebuild");
                _lastStatus = msg;
                Report(msg, 0, 1);
            }
            finally
            {
                EndRun();
            }
        }

        // -----------------------------
        // Public managed entrypoints
        // -----------------------------

        public Task StartManagedRebuildAsync(CacheRebuildOptions options)
        {
            return RunManagedAsync(
                runner: ct => _rebuildService.ScanAsync(RebuildOptions(options), HandleUpdate, ct),
                finalMessage: payload =>
                {
                    var s = payload?.Summary;
                    if (s == null) return ResourceProvider.GetString("LOCFriendsAchFeed_Error_FailedRebuild");

                    if (s.NewEntriesCount > 0)
                        return string.Format(ResourceProvider.GetString("LOCFriendsAchFeed_Rebuild_FoundNewEntries"), s.NewEntriesCount);

                    if (s.NoCandidatesDetected)
                        return ResourceProvider.GetString("LOCFriendsAchFeed_Rebuild_Final_NoCandidates");

                    return string.Format(ResourceProvider.GetString("LOCFriendsAchFeed_Rebuild_Final_NoNew_WithCount"), s.CandidateGamesTotal);
                },
                errorLogMessage: ResourceProvider.GetString("LOCFriendsAchFeed_Error_ManagedRebuild"));
        }

        public Task StartManagedSingleGameScanAsync(Guid playniteGameId)
        {
            var gameName = _api?.Database?.Games?.Get(playniteGameId)?.Name ?? playniteGameId.ToString();

            return RunManagedAsync(
                runner: ct => _rebuildService.ScanAsync(SingleGameOptions(playniteGameId), HandleUpdate, ct),
                finalMessage: payload =>
                {
                    var s = payload?.Summary;
                    if (s == null) return ResourceProvider.GetString("LOCFriendsAchFeed_Error_FailedRebuild");
                    if (s.NewEntriesCount > 0)
                        return string.Format(ResourceProvider.GetString("LOCFriendsAchFeed_Rebuild_Final_SingleGame_Found"), s.NewEntriesCount, gameName);
                    return string.Format(ResourceProvider.GetString("LOCFriendsAchFeed_Rebuild_Final_SingleGame_None"), gameName);
                },
                errorLogMessage: "[FAF] Managed single-game scan failed.");
        }

        /// <summary>
        /// Incremental scan:
        /// - choose up to recentFriends most recent friends (by cached friend unlock activity)
        /// - choose up to recentGamesPerFriend most recent games per friend (by cached friend unlock activity)
        /// - scan ONLY those friend/game pairs and self for affected games
        /// </summary>
        public Task StartManagedIncrementalScanAsync(int recentFriends = 5, int recentGamesPerFriend = 5)
        {
            return RunManagedAsync(
                runner: ct => _rebuildService.ScanAsync(IncrementalOptions(recentFriends, recentGamesPerFriend), HandleUpdate, ct),
                finalMessage: payload =>
                {
                    var s = payload?.Summary;
                    if (s == null) return ResourceProvider.GetString("LOCFriendsAchFeed_Error_FailedRebuild");

                    if (s.NoCandidatesDetected)
                        return ResourceProvider.GetString("LOCFriendsAchFeed_Rebuild_Final_Incremental_NoCandidates");

                    if (s.NewEntriesCount > 0)
                        return string.Format(ResourceProvider.GetString("LOCFriendsAchFeed_Rebuild_Final_Incremental_Found"), s.NewEntriesCount);

                    return ResourceProvider.GetString("LOCFriendsAchFeed_Rebuild_Final_Incremental_None");
                },
                errorLogMessage: "[FAF] Managed incremental scan failed.");
        }

        public void CancelActiveRebuild()
        {
            lock (_runLock)
            {
                try { _activeRunCts?.Cancel(); }
                catch (Exception ex)
                {
                    _logger?.Error(ex, ResourceProvider.GetString("LOCFriendsAchFeed_Error_CancelActiveRebuild"));
                }
            }
        }

        // -----------------------------
        // Cache read APIs
        // -----------------------------

        // Friend-only raw
        public List<FeedEntry> GetAllCachedFriendEntries()
            => _cacheService.GetCachedFriendEntries() ?? new List<FeedEntry>();

        // Convenience: hydrated FeedEntry for callers that still want it
        public List<FeedEntry> GetAllCachedEntriesHydrated()
            => _hydrator.HydrateForUi(GetAllCachedFriendEntries(), CancellationToken.None) ?? new List<FeedEntry>();

        public Task<List<FeedEntry>> BuildGameFeedAsync(Game game, CancellationToken cancel)
        {
            if (game == null || !_steam.TryGetSteamAppId(game, out var appId))
                return Task.FromResult(new List<FeedEntry>());

            var allEntries = GetAllCachedFriendEntries();
            var raw = allEntries
                .Where(e => e != null && e.AppId == appId && e.PlayniteGameId == game.Id)
                .OrderByDescending(e => e.FriendUnlockTimeUtc)
                .Take(_settings.MaxFeedItems)
                .ToList();

            return Task.FromResult(_hydrator.HydrateForUi(raw, cancel) ?? new List<FeedEntry>());
        }

        public Task<List<FeedEntry>> BuildGlobalFeedAsync(CancellationToken cancel)
        {
            var allEntries = GetAllCachedFriendEntries();
            var raw = allEntries
                .OrderByDescending(e => e.FriendUnlockTimeUtc)
                .Take(_settings.MaxFeedItems)
                .ToList();

            return Task.FromResult(_hydrator.HydrateForUi(raw, cancel) ?? new List<FeedEntry>());
        }

        public bool GameHasFeedEntries(string gameName)
        {
            if (string.IsNullOrWhiteSpace(gameName)) return false;
            var entries = _cacheService.GetCachedFriendEntries();
            return entries != null && entries.Any(e => string.Equals(e.GameName, gameName, StringComparison.OrdinalIgnoreCase));
        }

        public bool IsCacheValid()
        {
            var entries = _cacheService.GetCachedFriendEntries();
            return entries?.Any() == true;
        }

        public DateTime? GetCacheLastUpdated()
            => _cacheService.GetFriendFeedLastUpdatedUtc();
    }
}
