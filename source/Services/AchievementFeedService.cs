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
        private readonly object _rebuildLock = new object();
        private CancellationTokenSource _activeRebuildCts;

        public event EventHandler<ProgressReport> RebuildProgress;

        private ProgressReport _lastRebuildProgress;
        private string _lastRebuildStatus;

        public ProgressReport GetLastRebuildProgress() => _lastRebuildProgress;
        public string GetLastRebuildStatus() => _lastRebuildStatus;

        public bool IsRebuilding
        {
            get
            {
                lock (_rebuildLock)
                {
                    return _activeRebuildCts != null;
                }
            }
        }

        private readonly IPlayniteAPI _api;
        private readonly FriendsAchievementFeedSettings _settings;
        private readonly ILogger _logger;
        private readonly FriendsAchievementFeedPlugin _plugin;
        private readonly CacheService _cacheService;

        // Extracted collaborators
        private readonly SteamDataProvider _steam;
        private readonly FeedEntryFactory _entryFactory;
        private readonly LiveFeedBuilder _liveFeed;
        private readonly CacheRebuildService _rebuildService;
        private readonly FeedEntryViewComposer _viewComposer;

        public CacheService Cache => _cacheService;

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

            _steam = new SteamDataProvider(_api, _logger, _plugin);
            _entryFactory = new FeedEntryFactory();
            _liveFeed = new LiveFeedBuilder(_steam, _entryFactory, _settings, _logger);

            // Updated rebuild service: delta-only (works for first build too)
            _rebuildService = new CacheRebuildService(_steam, _entryFactory, _cacheService, _settings, _logger);

            _viewComposer = new FeedEntryViewComposer(_steam, _settings);

            try
            {
                _cacheService.CacheChanged += CacheService_CacheChanged;
            }
            catch (Exception e)
            {
                _logger?.Error(e, ResourceProvider.GetString("LOCFriendsAchFeed_Error_RefreshFeedAfterCacheChange"));
            }
        }

        private bool IsSteamConfigured()
        {
            // HTML-based scraping uses cookies; only Steam user id is required.
            return !string.IsNullOrWhiteSpace(_settings.SteamUserId);
        }

        private void PostToUi(Action action)
        {
            try
            {
                var dispatcher = _api?.MainView?.UIDispatcher;
                if (dispatcher != null && !dispatcher.CheckAccess())
                {
                    dispatcher.BeginInvoke(action, DispatcherPriority.Background);
                }
                else
                {
                    action();
                }
            }
            catch
            {
                try { action(); } catch { }
            }
        }

        private void OnRebuildProgress(ProgressReport report)
        {
            try
            {
                if (report != null)
                {
                    _lastRebuildProgress = report;
                    if (!string.IsNullOrWhiteSpace(report.Message))
                    {
                        _lastRebuildStatus = report.Message;
                    }
                }

                var handler = RebuildProgress;
                if (handler == null)
                {
                    return;
                }

                // Always post async to UI to avoid background workers blocking on UI work
                PostToUi(() =>
                {
                    try
                    {
                        handler(this, report);
                    }
                    catch (Exception e)
                    {
                        _logger?.Error(e, string.Format(ResourceProvider.GetString("LOCFriendsAchFeed_Error_NotifySubscribers")));
                    }
                });
            }
            catch (Exception e)
            {
                _logger?.Error(e, string.Format(ResourceProvider.GetString("LOCFriendsAchFeed_Error_NotifySubscribers")));
            }
        }

        private void CacheService_CacheChanged(object sender, EventArgs e)
        {
            // Settings objects/UI-bound state: update on UI thread.
            PostToUi(() =>
            {
                try
                {
                    var entries = _cacheService.GetCachedEntries() ?? new List<FeedEntry>();

                    // Expose the cache directory path for global consumption
                    var cacheDir = Path.Combine(_plugin.GetPluginUserDataPath(), "achievement_cache");
                    _settings.ExposedGlobalFeedPath = cacheDir;

                    // Build map of key -> per-game cache file path (if available)
                    var perGamePaths = entries
                        .GroupBy(en => en.PlayniteGameId?.ToString() ?? en.AppId.ToString())
                        .ToDictionary(
                            g => g.Key,
                            g => Path.Combine(cacheDir, $"{g.Key}.json")
                        );

                    _settings.ExposedGameFeeds = perGamePaths;

                    // Persist snapshot off-UI so disk I/O doesn't stall Playnite
                    Task.Run(() =>
                    {
                        try
                        {
                            _settings.EndEdit();
                        }
                        catch (Exception ex)
                        {
                            _logger?.Error(ex, string.Format(ResourceProvider.GetString("LOCFriendsAchFeed_Error_FileOperationFailed")));
                        }
                    });
                }
                catch (Exception ex)
                {
                    _logger?.Error(ex, string.Format(ResourceProvider.GetString("LOCFriendsAchFeed_Error_FileOperationFailed")));
                }
            });
        }

        // --- Steam auth (still public API) ---

        public Task<(bool Success, string Message)> TestSteamAuthAsync()
        {
            return _steam.TestSteamAuthAsync(_settings.SteamUserId);
        }

        // --- Rebuild control (non-blocking) ---

        /// <summary>
        /// Delta-only rebuild. Works for first build too.
        /// </summary>
        public async Task RunRebuildAsync(IProgress<ProgressReport> progress, CancellationToken cancel)
        {
            await _rebuildService.RebuildCacheAsync(progress, cancel).ConfigureAwait(false);
        }

        /// <summary>
        /// Managed delta-only rebuild. No modes.
        /// </summary>
        public async Task StartManagedRebuildAsync()
        {
            CancellationTokenSource cts;

            lock (_rebuildLock)
            {
                if (_activeRebuildCts != null)
                {
                    return;
                }

                _activeRebuildCts = new CancellationTokenSource();
                cts = _activeRebuildCts;
            }

            try
            {
                // IMPORTANT: run rebuild on background thread so any sync WebApi calls never run on UI thread
                await Task.Run(async () =>
                {
                    // Create Progress inside background context so it doesn't capture UI sync context.
                    var progress = new Progress<ProgressReport>(report => OnRebuildProgress(report));
                    await RunRebuildAsync(progress, cts.Token).ConfigureAwait(false);
                }, cts.Token).ConfigureAwait(false);

                OnRebuildProgress(new ProgressReport
                {
                    Message = ResourceProvider.GetString("LOCFriendsAchFeed_Rebuild_Completed"),
                    CurrentStep = 1,
                    TotalSteps = 1
                });
            }
            catch (OperationCanceledException)
            {
                OnRebuildProgress(new ProgressReport
                {
                    Message = ResourceProvider.GetString("LOCFriendsAchFeed_Rebuild_Canceled"),
                    CurrentStep = 0,
                    TotalSteps = 1,
                    IsCanceled = true
                });

                _logger?.Debug(ResourceProvider.GetString("LOCFriendsAchFeed_Debug_RebuildCanceledByUser"));
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, ResourceProvider.GetString("LOCFriendsAchFeed_Error_ManagedRebuild"));

                OnRebuildProgress(new ProgressReport
                {
                    Message = ResourceProvider.GetString("LOCFriendsAchFeed_Error_FailedRebuild"),
                    CurrentStep = 0,
                    TotalSteps = 1
                });
            }
            finally
            {
                lock (_rebuildLock)
                {
                    _activeRebuildCts?.Dispose();
                    _activeRebuildCts = null;
                }
            }
        }

        public void CancelActiveRebuild()
        {
            lock (_rebuildLock)
            {
                try
                {
                    _activeRebuildCts?.Cancel();
                }
                catch (Exception ex)
                {
                    _logger?.Error(ex, ResourceProvider.GetString("LOCFriendsAchFeed_Error_CancelActiveRebuild"));
                }
            }
        }

        // --- Targeted updates + feed builders ---

        public async Task UpdateCacheForAppIdsAsync(IEnumerable<int> appIds)
        {
            if (!IsSteamConfigured() || appIds == null)
            {
                return;
            }

            if (IsRebuilding)
            {
                _logger?.Debug(ResourceProvider.GetString("LOCFriendsAchFeed_Debug_TargetedUpdateSkipped"));
                return;
            }

            try
            {
                try
                {
                    OnRebuildProgress(new ProgressReport
                    {
                        Message = ResourceProvider.GetString("LOCFriendsAchFeed_Targeted_LoadingOwnedGames"),
                        CurrentStep = 0,
                        TotalSteps = 1
                    });
                }
                catch { }

                var yourOwnedGames = await _steam.GetOwnedGameIdsAsync(_settings.SteamUserId).ConfigureAwait(false);

                try
                {
                    OnRebuildProgress(new ProgressReport
                    {
                        Message = ResourceProvider.GetString("LOCFriendsAchFeed_Targeted_BuildingOwnedGames"),
                        CurrentStep = 0,
                        TotalSteps = 1
                    });
                }
                catch { }

                var steamGamesDict = _steam.BuildOwnedSteamGamesDict(yourOwnedGames);

                var games = appIds
                    .Where(id => steamGamesDict.ContainsKey(id))
                    .Select(id => steamGamesDict[id])
                    .ToList();

                try
                {
                    OnRebuildProgress(new ProgressReport
                    {
                        Message = string.Format(ResourceProvider.GetString("LOCFriendsAchFeed_Targeted_BuildingLiveFeed"), games.Count),
                        CurrentStep = 0,
                        TotalSteps = Math.Max(1, games.Count)
                    });
                }
                catch { }

                if (games.Count == 0)
                {
                    return;
                }

                var liveEntries = await _liveFeed.BuildLiveFeedForGamesAsync(
                        games,
                        report => OnRebuildProgress(report),
                        CancellationToken.None)
                    .ConfigureAwait(false);

                if (liveEntries != null && liveEntries.Any())
                {
                    _cacheService.MergeUpdateCache(liveEntries);
                }
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, ResourceProvider.GetString("LOCFriendsAchFeed_Error_TargetedCacheUpdate"));
            }
        }

        public async Task<List<FeedEntry>> BuildGameFeedAsync(Game game, CancellationToken cancel)
        {
            if (!_steam.TryGetSteamAppId(game, out var appId))
            {
                return new List<FeedEntry>();
            }

            if (_cacheService.IsCacheValid())
            {
                var cached = (_cacheService.GetCachedEntries() ?? new List<FeedEntry>())
                    .Where(e => e.AppId == appId && e.PlayniteGameId == game.Id)
                    .OrderByDescending(e => e.UnlockTime)
                    .Take(_settings.MaxFeedItems)
                    .ToList();

                if (cached.Any())
                {
                    return cached;
                }
            }

            if (!IsSteamConfigured())
            {
                return new List<FeedEntry>();
            }

            var liveEntries = await _liveFeed.BuildLiveFeedForGamesAsync(new[] { game }, null, cancel).ConfigureAwait(false);

            return liveEntries
                .Where(e => e.AppId == appId && e.PlayniteGameId == game.Id)
                .OrderByDescending(e => e.UnlockTime)
                .Take(_settings.MaxFeedItems)
                .ToList();
        }

        public async Task<List<FeedEntry>> BuildGlobalFeedAsync(CancellationToken cancel)
        {
            if (_cacheService.IsCacheValid())
            {
                return GetCachedGlobalFeed(_settings.MaxFeedItems);
            }

            if (!IsSteamConfigured())
            {
                return new List<FeedEntry>();
            }

            var candidateGames = _steam.EnumerateSteamGamesInLibrary()
                .OrderByDescending(g => g.LastActivity ?? g.Added ?? DateTime.MinValue)
                .ToList();

            var liveEntries = await _liveFeed.BuildLiveFeedForGamesAsync(candidateGames, null, cancel).ConfigureAwait(false);

            return liveEntries
                .OrderByDescending(e => e.UnlockTime)
                .Take(_settings.MaxFeedItems)
                .ToList();
        }

        // --- Cache inspection APIs ---

        public List<FeedEntry> GetCachedGlobalFeed(int maxItems = 50)
        {
            var allEntries = _cacheService.GetCachedEntries() ?? new List<FeedEntry>();
            return allEntries
                .OrderByDescending(e => e.UnlockTime)
                .Take(maxItems)
                .ToList();
        }

        public List<FeedEntry> GetAllCachedEntries() => _cacheService.GetCachedEntries();

        public bool GameHasFeedEntries(string gameName)
        {
            if (string.IsNullOrWhiteSpace(gameName)) return false;
            var entries = _cacheService.GetCachedEntries();
            if (entries == null) return false;

            return entries.Any(e => string.Equals(e.GameName, gameName, StringComparison.OrdinalIgnoreCase));
        }

        public bool IsCacheValid() => _cacheService.IsCacheValid();
        public DateTime? GetCacheLastUpdated() => _cacheService.GetCacheLastUpdated();

        public async Task<List<FeedEntry>> DecorateForViewAsync(IEnumerable<FeedEntry> rawEntries, CancellationToken cancel)
        {
            if (rawEntries == null)
            {
                return new List<FeedEntry>();
            }

            if (string.IsNullOrWhiteSpace(_settings?.SteamUserId))
            {
                return rawEntries.Where(e => e != null)
                                .OrderByDescending(e => e.UnlockTime)
                                .ToList();
            }

            return await _viewComposer.ComposeAsync(rawEntries, _settings.SteamUserId, cancel)
                                    .ConfigureAwait(false);
        }
    }
}
