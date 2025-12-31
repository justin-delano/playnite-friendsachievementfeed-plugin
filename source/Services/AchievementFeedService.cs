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

        // Progress state (kept here so progress logic is centralized/consistent)
        private volatile bool _selfStartedSeen;
        private volatile bool _selfCompletedSeen;
        private CacheRebuildService.RebuildStage? _lastStage;
        private readonly SemaphoreSlim _settingsSaveGate = new SemaphoreSlim(1, 1);
        private CancellationTokenSource _settingsSaveDebounceCts;
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

            try { _cacheService.CacheChanged += CacheService_CacheChanged; }
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
            if (dispatcher != null && !dispatcher.CheckAccess())
                dispatcher.BeginInvoke(action, DispatcherPriority.Background);
            else
                action();
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

        private void CacheService_CacheChanged(object sender, EventArgs e)
        {
            // Exposed paths for debugging.
            PostToUi(() =>
            {
                try
                {
                    var entries = _cacheService.GetCachedFriendEntries() ?? new List<FeedEntry>();

                    var cacheDir = Path.Combine(_plugin.GetPluginUserDataPath(), "achievement_cache");
                    _settings.ExposedGlobalFeedPath = cacheDir;

                    _settings.ExposedGameFeeds = entries
                        .GroupBy(en => en.PlayniteGameId?.ToString() ?? en.AppId.ToString())
                        .Where(g => !string.IsNullOrWhiteSpace(g.Key))
                        .ToDictionary(
                            g => g.Key,
                            g => Path.Combine(cacheDir, $"{g.Key}.json"),
                            StringComparer.OrdinalIgnoreCase);

                    ScheduleSettingsSave();
                }
                catch (Exception ex)
                {
                    _logger?.Error(ex, ResourceProvider.GetString("LOCFriendsAchFeed_Error_FileOperationFailed"));
                }
            });
        }

        private void ScheduleSettingsSave()
        {
            // Debounce: collapse many cache-changed events into one save
            _settingsSaveDebounceCts?.Cancel();
            _settingsSaveDebounceCts?.Dispose();
            _settingsSaveDebounceCts = new CancellationTokenSource();
            var token = _settingsSaveDebounceCts.Token;

            Task.Run(async () =>
            {
                try
                {
                    // wait for churn to settle
                    await Task.Delay(400, token).ConfigureAwait(false);

                    await _settingsSaveGate.WaitAsync(token).ConfigureAwait(false);
                    try
                    {
                        // Retry in case something external briefly holds the file (AV/indexer)
                        for (var attempt = 0; attempt < 5; attempt++)
                        {
                            token.ThrowIfCancellationRequested();
                            try
                            {
                                _settings.EndEdit(); // calls SavePluginSettings -> config.json
                                return;
                            }
                            catch (IOException)
                            {
                                await Task.Delay(150 * (attempt + 1), token).ConfigureAwait(false);
                            }
                        }
                    }
                    finally
                    {
                        _settingsSaveGate.Release();
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    _logger?.Error(ex, "[FAF] Failed to persist plugin settings.");
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
        // Centralized progress mapping (ONE PLACE)
        // -----------------------------

        private string StageMessage(CacheRebuildService.RebuildStage stage)
        {
            switch (stage)
            {
                case CacheRebuildService.RebuildStage.NotConfigured: return ResourceProvider.GetString("LOCFriendsAchFeed_Error_SteamNotConfigured");
                case CacheRebuildService.RebuildStage.LoadingOwnedGames: return ResourceProvider.GetString("LOCFriendsAchFeed_Targeted_LoadingOwnedGames");
                case CacheRebuildService.RebuildStage.LoadingFriends: return ResourceProvider.GetString("LOCFriendsAchFeed_Targeted_LoadingFriends");
                case CacheRebuildService.RebuildStage.LoadingExistingCache: return "Loading existing cache…";
                case CacheRebuildService.RebuildStage.LoadingSelfOwnedApps: return "Loading your owned games…";
                case CacheRebuildService.RebuildStage.RefreshingSelfAchievements: return "Refreshing your achievements…";
                case CacheRebuildService.RebuildStage.ProcessingFriends: return "Scanning friends…";
                default: return null;
            }
        }

        private (int Cur, int Total) ProgressSteps(CacheRebuildService.RebuildUpdate u, int fallbackCur = 0, int fallbackTotal = 0)
        {
            if (u != null && u.OverallCount > 0)
                return (Math.Max(0, u.OverallIndex), Math.Max(1, u.OverallCount));

            return (Math.Max(0, fallbackCur), Math.Max(0, fallbackTotal));
        }

        private string AppSuffix(CacheRebuildService.RebuildUpdate u)
        {
            if (u == null) return string.Empty;
            if (!string.IsNullOrWhiteSpace(u.CurrentGameName)) return " — " + u.CurrentGameName;
            if (u.CurrentAppId > 0) return " — app " + u.CurrentAppId;
            return string.Empty;
        }

        private void HandleUpdate(CacheRebuildService.RebuildUpdate u)
        {
            if (u == null) return;

            // Stage updates: indeterminate (easy to change globally)
            if (u.Kind == CacheRebuildService.RebuildUpdateKind.Stage)
            {
                _lastStage = u.Stage;

                var msg = StageMessage(u.Stage);
                if (!string.IsNullOrWhiteSpace(msg))
                {
                    // If stage is "RefreshingSelfAchievements", the service may also emit SelfStarted;
                    // we suppress that once we see the stage message to reduce noise.
                    if (u.Stage == CacheRebuildService.RebuildStage.RefreshingSelfAchievements)
                        _selfStartedSeen = true;

                    Report(msg);
                }

                return;
            }

            switch (u.Kind)
            {
                // ---- Self ----
                case CacheRebuildService.RebuildUpdateKind.SelfStarted:
                {
                    if (_selfStartedSeen) return;
                    _selfStartedSeen = true;

                    var total = Math.Max(0, u.SelfAppCount);
                    var msg = total > 0
                        ? $"You — refreshing achievements — 0/{total}"
                        : "You — refreshing achievements…";

                    Report(msg);
                    return;
                }

                case CacheRebuildService.RebuildUpdateKind.SelfProgress:
                {
                    var part = (u.SelfAppCount > 0)
                        ? $"refreshing {Math.Max(0, u.SelfAppIndex)}/{u.SelfAppCount}"
                        : "refreshing…";

                    var msg = "You — " + part + AppSuffix(u);
                    var steps = ProgressSteps(u);
                    Report(msg, steps.Cur, steps.Total);
                    return;
                }

                case CacheRebuildService.RebuildUpdateKind.SelfCompleted:
                {
                    if (_selfCompletedSeen) return;
                    _selfCompletedSeen = true;

                    var steps = ProgressSteps(u);
                    Report("Your achievements cache is up to date.", steps.Cur, steps.Total);
                    return;
                }

                // ---- Friends ----
                case CacheRebuildService.RebuildUpdateKind.FriendStarted:
                {
                    var totalApps = Math.Max(0, u.FriendAppCount);
                    var msg =
                        $"{u.FriendPersonaName} ({u.FriendIndex}/{u.FriendCount})" +
                        $" — candidates: {u.CandidateGames}" +
                        $" — scanning 0/{totalApps}";

                    var steps = ProgressSteps(u, u.FriendIndex, u.FriendCount);
                    Report(msg, steps.Cur, steps.Total);
                    return;
                }

                case CacheRebuildService.RebuildUpdateKind.FriendProgress:
                {
                    var part = (u.FriendAppCount > 0)
                        ? $"scanning {Math.Max(0, u.FriendAppIndex)}/{u.FriendAppCount}"
                        : "scanning…";

                    var msg =
                        $"{u.FriendPersonaName} ({u.FriendIndex}/{u.FriendCount}) — {part}{AppSuffix(u)}";

                    var steps = ProgressSteps(u, u.FriendIndex, u.FriendCount);
                    Report(msg, steps.Cur, steps.Total);
                    return;
                }

                case CacheRebuildService.RebuildUpdateKind.FriendCompleted:
                {
                    var msg =
                        $"{u.FriendPersonaName} ({u.FriendIndex}/{u.FriendCount})" +
                        $" — candidates: {u.CandidateGames}" +
                        $" — new: {u.FriendNewEntries}";

                    var steps = ProgressSteps(u, u.FriendIndex, u.FriendCount);
                    Report(msg, steps.Cur, steps.Total);
                    return;
                }

                default:
                    return;
            }
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
            _selfStartedSeen = false;
            _selfCompletedSeen = false;
            _lastStage = null;
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
                // Unified progress callback for both scan modes
                Action<CacheRebuildService.RebuildUpdate> onUpdate = HandleUpdate;

                var payload = await runner(cts.Token).ConfigureAwait(false);

                var msg = finalMessage?.Invoke(payload) ?? ResourceProvider.GetString("LOCFriendsAchFeed_Error_FailedRebuild");
                _lastStatus = msg;
                Report(msg, 1, 1);
            }
            catch (OperationCanceledException)
            {
                var msg = ResourceProvider.GetString("LOCFriendsAchFeed_Rebuild_Canceled");
                _lastStatus = msg;
                Report(msg, 0, 1, canceled: true);
                _logger?.Debug(ResourceProvider.GetString("LOCFriendsAchFeed_Debug_RebuildCanceledByUser"));
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
                        return "No candidates detected. Cache is up to date.";

                    return $"Scanned candidates (count={s.CandidateGamesTotal}). No new achievements found.";
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
                    if (s.NewEntriesCount > 0) return $"Found {s.NewEntriesCount} new achievements for {gameName}.";
                    return $"Scanned {gameName}. No new achievements found.";
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
                        return "Incremental scan: no recent friend/game candidates found (cache may be empty).";

                    if (s.NewEntriesCount > 0)
                        return $"Incremental scan found {s.NewEntriesCount} new achievements.";

                    return "Incremental scan completed. No new achievements found.";
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

            var raw = GetAllCachedFriendEntries()
                .Where(e => e != null && e.AppId == appId && e.PlayniteGameId == game.Id)
                .OrderByDescending(e => e.FriendUnlockTimeUtc)
                .Take(_settings.MaxFeedItems)
                .ToList();

            return Task.FromResult(_hydrator.HydrateForUi(raw, cancel) ?? new List<FeedEntry>());
        }

        public Task<List<FeedEntry>> BuildGlobalFeedAsync(CancellationToken cancel)
        {
            var raw = GetAllCachedFriendEntries()
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
            return entries != null && entries.Count > 0;
        }

        public DateTime? GetCacheLastUpdated()
            => _cacheService.GetFriendFeedLastUpdatedUtc();
    }
}
