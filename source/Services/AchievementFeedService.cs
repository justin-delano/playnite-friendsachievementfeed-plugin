using FriendsAchievementFeed.Models;
using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Common.SteamKitModels;

namespace FriendsAchievementFeed.Services
{
    public enum CacheRebuildMode
    {
        Full,
        Incremental
    }

    public class AchievementFeedService
    {
        private readonly object _rebuildLock = new object();
        private CancellationTokenSource _activeRebuildCts;

        /// <summary>
        /// Raised when a managed rebuild reports progress. Subscribers should handle quickly.
        /// </summary>
        public event EventHandler<ProgressReport> RebuildProgress;

        private ProgressReport _lastRebuildProgress;
        private string _lastRebuildStatus;

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

                RebuildProgress?.Invoke(this, report);
            }
            catch (Exception e)
            {
                _logger?.Error(e, "Error while notifying RebuildProgress subscribers.");
            }
        }

        /// <summary>
        /// Returns the last progress report emitted by a managed rebuild (or null).
        /// </summary>
        public ProgressReport GetLastRebuildProgress()
        {
            return _lastRebuildProgress;
        }

        /// <summary>
        /// Returns the last non-empty rebuild status message, or null.
        /// </summary>
        public string GetLastRebuildStatus()
        {
            return _lastRebuildStatus;
        }

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
        private readonly CacheService _cacheService;
        public event EventHandler CacheChanged
        {
            add    => _cacheService.CacheChanged += value;
            remove => _cacheService.CacheChanged -= value;
        }
        // In-memory cache of owned games per Steam user id (for this process lifetime)
        private readonly Dictionary<string, HashSet<int>> _ownedGamesCache =
            new Dictionary<string, HashSet<int>>();
        // In-memory cache of your (local user) unlocked achievement API names per appId
        private readonly System.Collections.Concurrent.ConcurrentDictionary<int, HashSet<string>> _yourAchievementsCache =
            new System.Collections.Concurrent.ConcurrentDictionary<int, HashSet<string>>();
        // In-memory cache of your (local user) achievement unlock times per appId: apiName -> unlockTime (null if not unlocked)
        private readonly System.Collections.Concurrent.ConcurrentDictionary<int, Dictionary<string, DateTime?>> _yourAchievementsTimesCache =
            new System.Collections.Concurrent.ConcurrentDictionary<int, Dictionary<string, DateTime?>>();

        public CacheService Cache => _cacheService;

        public AchievementFeedService(IPlayniteAPI api, FriendsAchievementFeedSettings settings, ILogger logger)
        {
            _api = api;
            _settings = settings;
            _logger = logger;
            _cacheService = new CacheService(api, logger);
        }

        /// <summary>
        /// Update cache entries for the specified appIds by performing a live fetch
        /// for those games and merging results into the existing cache.
        /// This is intended for targeted updates when a single game in the library changes.
        /// </summary>
        public async Task UpdateCacheForAppIdsAsync(IEnumerable<int> appIds)
        {
            if (!IsSteamConfigured())
            {
                return;
            }

            if (appIds == null)
            {
                return;
            }

            // Avoid clobbering an active full/incremental rebuild
            if (IsRebuilding)
            {
                _logger?.Debug("Skipping targeted cache update because a rebuild is already running.");
                return;
            }

            try
            {
                var yourOwnedGames = GetOwnedGameIdsCached(_settings.SteamUserId);
                var steamGamesDict = BuildOwnedSteamGamesDict(yourOwnedGames);

                var games = appIds
                    .Where(id => steamGamesDict.ContainsKey(id))
                    .Select(id => steamGamesDict[id])
                    .ToList();

                if (games.Count == 0)
                {
                    return;
                }

                var liveEntries = await BuildLiveFeedForGamesAsync(games, CancellationToken.None).ConfigureAwait(false);

                if (liveEntries != null && liveEntries.Any())
                {
                    _cacheService.MergeUpdateCache(liveEntries);
                }
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Error while performing targeted cache update for appIds.");
            }
        }

        #region Basic helpers

        private bool IsSteamConfigured()
        {
            return !string.IsNullOrWhiteSpace(_settings.SteamUserId) &&
                   !string.IsNullOrWhiteSpace(_settings.SteamApiKey);
        }

        private static bool TryGetSteamAppId(Game game, out int appId)
        {
            appId = 0;

            if (string.IsNullOrWhiteSpace(game.GameId))
            {
                return false;
            }

            return int.TryParse(game.GameId, out appId);
        }

        private HashSet<int> GetOwnedGameIds(string steamId)
        {
            if (!ulong.TryParse(steamId, out var id))
            {
                return new HashSet<int>();
            }

            var owned = SteamWebApi.GetOwnedGames(_settings.SteamApiKey, id) ?? new List<SteamOwnedGame>();
            return new HashSet<int>(owned.Select(o => (int)o.Appid));
        }

        // Cached wrapper to avoid repeated GetOwnedGames calls in a single run
        private HashSet<int> GetOwnedGameIdsCached(string steamId)
        {
            if (string.IsNullOrWhiteSpace(steamId))
            {
                return new HashSet<int>();
            }

            if (_ownedGamesCache.TryGetValue(steamId, out var cached))
            {
                return cached;
            }

            var owned = GetOwnedGameIds(steamId);
            _ownedGamesCache[steamId] = owned;
            return owned;
        }

        private List<Models.SteamFriend> GetFriends(string steamId)
        {
            var result = new List<Models.SteamFriend>();

            if (!ulong.TryParse(steamId, out var id))
            {
                return result;
            }

            var friendsRaw = SteamWebApi.GetFriendList(_settings.SteamApiKey, id);
            var ids = friendsRaw != null
                ? friendsRaw.Select(f => f.SteamId).ToList()
                : new List<ulong>();

            var summaries = SteamWebApi.GetPlayerSummaries(_settings.SteamApiKey, ids)
                           ?? new List<SteamPlayerSummaries>();

            foreach (var p in summaries)
            {
                if (p == null)
                {
                    continue;
                }

                result.Add(new Models.SteamFriend
                {
                    SteamId = p.SteamId,
                    PersonaName = p.PersonaName,
                    AvatarMediumUrl = string.IsNullOrEmpty(p.AvatarMedium) ? p.Avatar : p.AvatarMedium
                });
            }

            return result;
        }

        private List<Models.SteamAchievement> GetPlayerAchievements(string steamId, int appId)
        {
            var list = new List<Models.SteamAchievement>();

            if (!ulong.TryParse(steamId, out var id))
            {
                return list;
            }

            var items = SteamWebApi.GetPlayerAchievements(_settings.SteamApiKey, (uint)appId, id)
                        ?? new List<SteamPlayerAchievement>();

            foreach (var a in items)
            {
                list.Add(new Models.SteamAchievement
                {
                    ApiName = a.ApiName,
                    Achieved = a.Achieved == 1,
                    UnlockTime = a.UnlockTime == default ? (DateTime?)null : a.UnlockTime
                });
            }

            return list;
        }

        /// <summary>
        /// Fetch raw Steam schema for a game and map to AchievementMeta by API name.
        /// This is the *Steam-only* view; SuccessStory is merged in ResolveAchievementMeta.
        /// Always returns a non-null dictionary.
        /// </summary>
        private Dictionary<string, AchievementMeta> GetAchievementSchema(int appId)
        {
            var dict = new Dictionary<string, AchievementMeta>(StringComparer.OrdinalIgnoreCase);

            var schema = SteamWebApi.GetSchemaForGame(_settings.SteamApiKey, (uint)appId);
            if (schema?.Achievements == null)
            {
                return dict;
            }

            foreach (var a in schema.Achievements)
            {
                if (string.IsNullOrWhiteSpace(a.Name))
                {
                    continue;
                }

                dict[a.Name] = new AchievementMeta
                {
                    Name = string.IsNullOrWhiteSpace(a.DisplayName) ? a.Name : a.DisplayName,
                    Description = a.Description ?? string.Empty,
                    IconUnlocked = a.Icon
                };
            }

            return dict;
        }

        private HashSet<string> GetYourAchievementsCachedByApp(int appId)
        {
            return _yourAchievementsCache.GetOrAdd(appId, id =>
            {
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    var list = GetPlayerAchievements(_settings.SteamUserId, id);
                    foreach (var a in list)
                    {
                        if (a != null && a.Achieved && !string.IsNullOrWhiteSpace(a.ApiName))
                        {
                            set.Add(a.ApiName);
                        }
                    }
                }
                catch
                {
                    // swallow; treat as no achievements
                }

                return set;
            });
        }

        private Dictionary<string, DateTime?> GetYourAchievementsWithTimesCached(int appId)
        {
            return _yourAchievementsTimesCache.GetOrAdd(appId, id =>
            {
                var dict = new Dictionary<string, DateTime?>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    var list = GetPlayerAchievements(_settings.SteamUserId, id);
                    foreach (var a in list)
                    {
                        if (a == null || string.IsNullOrWhiteSpace(a.ApiName))
                        {
                            continue;
                        }

                        if (a.Achieved && a.UnlockTime.HasValue)
                        {
                            dict[a.ApiName] = a.UnlockTime.Value;
                        }
                        else
                        {
                            dict[a.ApiName] = null;
                        }
                    }
                }
                catch
                {
                    // swallow; treat as no achievements
                }

                return dict;
            });
        }

        /// <summary>
        /// Resolve final achievement metadata by:
        ///  1) Steam schema (cached per app),
        ///  2) SuccessStory (fallback / fill missing fields),
        /// with safe defaults.
        /// </summary>
        private AchievementMeta ResolveAchievementMeta(
            int appId,
            Game game,
            string apiName,
            ConcurrentDictionary<int, Dictionary<string, AchievementMeta>> schemaCache)
        {
            var key = apiName ?? string.Empty;

            var result = new AchievementMeta
            {
                Name = null,
                Description = null,
                IconUnlocked = null,
                IconLocked = null
            };

            if (!string.IsNullOrWhiteSpace(key))
            {
                // 1) Steam schema (cached per appId in this run)
                var schema = schemaCache.GetOrAdd(appId, _ => GetAchievementSchema(appId));

                if (schema != null && schema.TryGetValue(key, out var steamMeta) && steamMeta != null)
                {
                    if (!string.IsNullOrWhiteSpace(steamMeta.Name))
                    {
                        result.Name = steamMeta.Name;
                    }

                    if (!string.IsNullOrWhiteSpace(steamMeta.Description))
                    {
                        result.Description = steamMeta.Description;
                    }

                    if (!string.IsNullOrWhiteSpace(steamMeta.IconUnlocked))
                    {
                        result.IconUnlocked = steamMeta.IconUnlocked;
                    }
                }

                // 2) SuccessStory fallback / fill
                if (SuccessStoryIntegration.TryGetAchievementMeta(_api, _logger, game.Id, key, out var ssMeta) &&
                    ssMeta != null)
                {
                    if (string.IsNullOrWhiteSpace(result.Name) && !string.IsNullOrWhiteSpace(ssMeta.Name))
                    {
                        result.Name = ssMeta.Name;
                    }

                    if (string.IsNullOrWhiteSpace(result.Description) && !string.IsNullOrWhiteSpace(ssMeta.Description))
                    {
                        result.Description = ssMeta.Description;
                    }

                    if (string.IsNullOrWhiteSpace(result.IconUnlocked) && !string.IsNullOrWhiteSpace(ssMeta.IconUnlocked))
                    {
                        result.IconUnlocked = ssMeta.IconUnlocked;
                    }
                    if (string.IsNullOrWhiteSpace(result.IconLocked) && !string.IsNullOrWhiteSpace(ssMeta.IconLocked))
                    {
                        result.IconLocked = ssMeta.IconLocked;
                    }
                }
            }

            // Final fallbacks
            if (string.IsNullOrWhiteSpace(result.Name))
            {
                result.Name = key ?? string.Empty;
            }

            if (string.IsNullOrWhiteSpace(result.Description))
            {
                result.Description = string.Empty;
            }

            return result;
        }

        /// <summary>
        /// Enumerate all Steam games in the library (any appId, regardless of ownership).
        /// </summary>
        private IEnumerable<Game> EnumerateSteamGamesInLibrary()
        {
            return _api.Database.Games.Where(g =>
                !g.Hidden &&
                !string.IsNullOrWhiteSpace(g.GameId) &&
                int.TryParse(g.GameId, out _));
        }

        /// <summary>
        /// Build a dictionary of appId -> Game for Steam games you both own and have in Playnite DB.
        /// </summary>
        private Dictionary<int, Game> BuildOwnedSteamGamesDict(HashSet<int> yourOwnedGames)
        {
            var dict = new Dictionary<int, Game>();

            foreach (var g in EnumerateSteamGamesInLibrary())
            {
                if (!TryGetSteamAppId(g, out var appId) || appId == 0)
                {
                    continue;
                }

                if (!yourOwnedGames.Contains(appId))
                {
                    continue;
                }

                if (!dict.ContainsKey(appId))
                {
                    dict.Add(appId, g);
                }
            }

            return dict;
        }

        // (Removed helper) GetMyUnlockTimeFor was unused after switching to UI-only visibility; keep cached helpers above.

        #endregion

        /// <summary>
        /// Non-modal rebuild that reports progress via the provided IProgress and honors a CancellationToken.
        /// This is intended for embedding progress UI in the view instead of using the global modal dialog.
        /// </summary>
        public async Task RunRebuildAsync(CacheRebuildMode mode, IProgress<ProgressReport> progress, CancellationToken cancel)
        {
            if (mode == CacheRebuildMode.Full)
            {
                await RebuildCacheFullAsync(progress, cancel).ConfigureAwait(false);
            }
            else
            {
                await RebuildCacheIncrementalAsync(progress, cancel).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Start a service-managed rebuild. Only one managed rebuild may run at a time.
        /// Progress is published via the `RebuildProgress` event. Cancellation cancels the active rebuild.
        /// </summary>
        public async Task StartManagedRebuildAsync(CacheRebuildMode mode)
        {
            CancellationTokenSource cts;
            lock (_rebuildLock)
            {
                if (_activeRebuildCts != null)
                {
                    // already rebuilding
                    return;
                }

                _activeRebuildCts = new CancellationTokenSource();
                cts = _activeRebuildCts;
            }

            try
            {
                var progress = new Progress<ProgressReport>(report => OnRebuildProgress(report));
                await RunRebuildAsync(mode, progress, cts.Token).ConfigureAwait(false);
                // Ensure a clear final status is emitted on successful completion
                var completionMessage = ResourceProvider.GetString("LOCFriendsAchFeed_Rebuild_Completed");
                OnRebuildProgress(new ProgressReport { Message = completionMessage, CurrentStep = 1, TotalSteps = 1 });
            }
            catch (OperationCanceledException)
            {
                OnRebuildProgress(new ProgressReport { Message = ResourceProvider.GetString("LOCFriendsAchFeed_Rebuild_Canceled"), CurrentStep = 0, TotalSteps = 1 });
                _logger?.Debug(ResourceProvider.GetString("Debug_ManagedRebuildCanceled"));
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Error in managed rebuild.");
                OnRebuildProgress(new ProgressReport { Message = ResourceProvider.GetString("LOCFriendsAchFeed_Error_FailedRebuild"), CurrentStep = 0, TotalSteps = 1 });
            }
            finally
            {
                lock (_rebuildLock)
                {
                    _activeRebuildCts?.Dispose();
                    _activeRebuildCts = null;
                }
                if (_lastRebuildProgress != null)
                {
                    OnRebuildProgress(_lastRebuildProgress);
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
                    _logger?.Error(ex, "Error cancelling active rebuild.");
                }
            }
        }

        #region Public feed builders (per game / global)

        /// <summary>
        /// Build feed for a specific Playnite game.
        /// Uses cache if available; falls back to live Steam calls otherwise.
        /// </summary>
        public async Task<List<FeedEntry>> BuildGameFeedAsync(Game game, CancellationToken cancel)
        {
            if (!TryGetSteamAppId(game, out var appId))
            {
                return new List<FeedEntry>();
            }

            // 1) Prefer cache when it is valid and has entries for this game
            if (_cacheService.IsCacheValid())
            {
                var cached = _cacheService.GetCachedEntries()
                    .Where(e => e.AppId == appId && e.PlayniteGameId == game.Id)
                    .OrderByDescending(e => e.UnlockTime)
                    .Take(_settings.MaxFeedItems)
                    .ToList();

                if (cached.Any())
                {
                    return cached;
                }
            }

            // 2) Fallback to live calls (single-game path)
            if (!IsSteamConfigured())
            {
                return new List<FeedEntry>();
            }

            var liveEntries = await BuildLiveFeedForGamesAsync(
                new[] { game },
                cancel).ConfigureAwait(false);

            return liveEntries
                .Where(e => e.AppId == appId && e.PlayniteGameId == game.Id)
                .OrderByDescending(e => e.UnlockTime)
                .Take(_settings.MaxFeedItems)
                .ToList();
        }

        /// <summary>
        /// Global feed across your most recent Steam games.
        /// Uses cache when available; falls back to live scraping otherwise.
        /// </summary>
        public async Task<List<FeedEntry>> BuildGlobalFeedAsync(CancellationToken cancel)
        {
            // 1) Prefer cache when available
            if (_cacheService.IsCacheValid())
            {
                return GetCachedGlobalFeed(maxItems: _settings.MaxFeedItems);
            }
            // 2) Live fallback (no cache yet)
            if (!IsSteamConfigured())
            {
                return new List<FeedEntry>();
            }

            var candidateGames = EnumerateSteamGamesInLibrary()
                .OrderByDescending(g => g.LastActivity ?? g.Added ?? DateTime.MinValue)
                .ToList();

            var liveEntries = await BuildLiveFeedForGamesAsync(candidateGames, cancel)
                .ConfigureAwait(false);

            return liveEntries
                .OrderByDescending(e => e.UnlockTime)
                .Take(_settings.MaxFeedItems)
                .ToList();
        }

        /// <summary>
        /// Shared live feed builder used by both per-game and global views.
        /// Only considers mutual games (you own + friend owns + in Playnite DB).
        /// </summary>
        private async Task<List<FeedEntry>> BuildLiveFeedForGamesAsync(
            IEnumerable<Game> games,
            CancellationToken cancel)
        {
            var gamesList = games?.ToList() ?? new List<Game>();
            if (gamesList.Count == 0)
            {
                return new List<FeedEntry>();
            }

            if (!IsSteamConfigured())
            {
                return new List<FeedEntry>();
            }

            // You must own the game for it to be considered
            var yourOwnedGames = GetOwnedGameIdsCached(_settings.SteamUserId);

            // Restrict to games you own, map appId -> Game
            var appIdToGame = new Dictionary<int, Game>();
            foreach (var g in gamesList)
            {
                if (!TryGetSteamAppId(g, out var appId) || appId == 0)
                {
                    continue;
                }

                if (!yourOwnedGames.Contains(appId))
                {
                    continue;
                }

                if (!appIdToGame.ContainsKey(appId))
                {
                    appIdToGame.Add(appId, g);
                }
            }

            if (appIdToGame.Count == 0)
            {
                return new List<FeedEntry>();
            }

            var allFriends = GetFriends(_settings.SteamUserId);
            var schemaCache = new ConcurrentDictionary<int, Dictionary<string, AchievementMeta>>();
            var allEntries = new List<FeedEntry>();

            // Prefetch your (local) achievements (including unlock times) for each relevant app to avoid repeated calls inside loops
            foreach (var appId in appIdToGame.Keys)
            {
                try
                {
                    GetYourAchievementsWithTimesCached(appId);
                }
                catch (Exception ex)
                {
                    _logger?.Debug($"Prefetching achievements failed for app {appId}: {ex.Message}");
                }
            }

            foreach (var friend in allFriends)
            {
                cancel.ThrowIfCancellationRequested();

                var friendOwned = GetOwnedGameIdsCached(friend.SteamId);
                if (!_settings.SearchAllMyGames && (friendOwned == null || friendOwned.Count == 0))
                {
                    // If not searching all my games, skip friends with no owned games
                    continue;
                }

                foreach (var kv in appIdToGame)
                {
                    cancel.ThrowIfCancellationRequested();

                    var appId = kv.Key;
                    var game = kv.Value;

                    if (!friendOwned.Contains(appId))
                    {
                        continue;
                    }

                    var friendAchievements = GetPlayerAchievements(friend.SteamId, appId);

                    foreach (var ach in friendAchievements)
                    {
                        if (!ach.Achieved || !ach.UnlockTime.HasValue)
                        {
                            continue;
                        }

                        var key = ach.ApiName ?? string.Empty;
                        var meta = ResolveAchievementMeta(appId, game, key, schemaCache);

                        var entry = new FeedEntry
                        {
                            Id = friend.SteamId + ":" + appId + ":" + key + ":" + ach.UnlockTime.Value.Ticks,
                            FriendSteamId = friend.SteamId,
                            FriendPersonaName = friend.PersonaName,
                            FriendAvatarUrl = friend.AvatarMediumUrl,
                            GameName = game.Name,
                            PlayniteGameId = game.Id,
                            AppId = appId,
                            AchievementApiName = key,
                            AchievementDisplayName = meta.Name,
                            AchievementDescription = meta.Description,
                            AchievementIconUrl = meta.IconUnlocked,
                            AchievementIconUnlockedUrl = meta.IconUnlocked,
                            UnlockTime = ach.UnlockTime.Value.ToLocalTime()
                        };
                        // If user opted to hide achievements they haven't unlocked, and you haven't unlocked this one,
                        // switch icon to locked and hide description.
                        if (_settings.HideAchievementsLockedForYou)
                        {
                            if (GetYourAchievementsCachedByApp(appId) is HashSet<string> yourSet && !yourSet.Contains(key))
                            {
                                entry.AchievementIconUrl = string.IsNullOrWhiteSpace(meta.IconLocked) ? meta.IconUnlocked : meta.IconLocked;
                                entry.HideDescription = true;
                            }
                        }

                        // Ensure unlocked icon URL is always present to avoid blanking when revealing.
                        if (string.IsNullOrWhiteSpace(entry.AchievementIconUnlockedUrl))
                        {
                            entry.AchievementIconUnlockedUrl =
                                !string.IsNullOrWhiteSpace(meta.IconUnlocked) ? meta.IconUnlocked :
                                !string.IsNullOrWhiteSpace(meta.IconLocked) ? meta.IconLocked :
                                entry.AchievementIconUrl;
                        }

                        // Optionally include the local user's unlock time for this achievement
                        if (_settings.IncludeMyUnlockTime)
                        {
                            try
                            {
                                var dict = GetYourAchievementsWithTimesCached(appId);
                                if (dict != null && dict.TryGetValue(key, out var myTime) && myTime.HasValue)
                                {
                                    entry.MyUnlockTime = myTime.Value.ToLocalTime();
                                }
                            }
                            catch
                            {
                                // ignore
                            }
                        }

                        allEntries.Add(entry);
                    }
                }
            }

            // No sorting / limiting here; callers (per-game/global) decide how to cap.
            return await Task.FromResult(allEntries);
        }

        #endregion

        #region Cache inspection APIs

        /// <summary>
        /// Gets the global feed from cache with optional time filter.
        /// </summary>
        /// <param name="maxItems">Max items to return.</param>
        public List<FeedEntry> GetCachedGlobalFeed(int maxItems = 50)
        {
            var allEntries = _cacheService.GetCachedEntries();
            return allEntries
                .OrderByDescending(e => e.UnlockTime)
                .Take(maxItems)
                .ToList();
        }

        /// <summary>
        /// Gets all cached achievement entries (for filtering).
        /// </summary>
        public List<FeedEntry> GetAllCachedEntries()
        {
            return _cacheService.GetCachedEntries();
        }

        /// <summary>
        /// Checks if the cache has valid data.
        /// </summary>
        public bool IsCacheValid()
        {
            return _cacheService.IsCacheValid();
        }

        /// <summary>
        /// Gets the last time the cache was updated.
        /// </summary>
        public DateTime? GetCacheLastUpdated()
        {
            return _cacheService.GetCacheLastUpdated();
        }

        #endregion

        #region Cache rebuild – shared helpers

        /// <summary>
        /// Build a map of FriendSteamId -> (AppId -> max UnlockTime in cache).
        /// Used by both full and incremental rebuilds to skip obviously-old achievements.
        /// </summary>
        private Dictionary<string, Dictionary<int, DateTime>> BuildFriendAppMaxUnlockMap(IEnumerable<FeedEntry> existingEntries)
        {
            var result = new Dictionary<string, Dictionary<int, DateTime>>();

            foreach (var e in existingEntries)
            {
                if (!result.TryGetValue(e.FriendSteamId, out var appMap))
                {
                    appMap = new Dictionary<int, DateTime>();
                    result[e.FriendSteamId] = appMap;
                }

                var unlockUtc = e.UnlockTime.ToUniversalTime();

                if (!appMap.TryGetValue(e.AppId, out var existing) || unlockUtc > existing)
                {
                    appMap[e.AppId] = unlockUtc;
                }
            }

            return result;
        }

        /// <summary>
        /// Unified friend processor for both Full and Incremental rebuild modes.
        /// Applies per-friend, per-app time skipping and uses ResolveAchievementMeta.
        /// </summary>
        private List<FeedEntry> ProcessFriend(
            Models.SteamFriend friend,
            CacheRebuildMode mode,
            HashSet<int> yourOwnedGames,
            Dictionary<int, Game> steamGamesDict,
            Dictionary<string, Dictionary<int, DateTime>> friendAppMaxUnlock,
            HashSet<string> existingIds,
            ConcurrentDictionary<int, Dictionary<string, AchievementMeta>> schemaCache,
            DateTime? lastUpdatedUtc,
            CancellationToken cancel,
            out int friendAdded)
        {
            var newEntries = new List<FeedEntry>();
            friendAdded = 0;

            try
            {
                cancel.ThrowIfCancellationRequested();


                friendAppMaxUnlock.TryGetValue(friend.SteamId, out var appMap);
                var friendHasCached = appMap != null && appMap.Count > 0;

                var candidateAppIds = new List<int>();

                // Fetch friend's owned games once (may be empty for private profiles)
                var friendOwnedGames = GetOwnedGameIdsCached(friend.SteamId);
                if (!_settings.SearchAllMyGames && (friendOwnedGames == null || friendOwnedGames.Count == 0))
                {
                    _logger.Debug($"Skipping {friend.PersonaName} ({friend.SteamId}) - no owned games or private profile (ownedCount=0)");
                    return newEntries;
                }

                if (mode == CacheRebuildMode.Full)
                {
                    if (_settings.SearchAllMyGames)
                    {
                        // Consider all games you own (that are in your Playnite Steam list)
                        foreach (var appId in steamGamesDict.Keys)
                        {
                            if (yourOwnedGames.Contains(appId) && steamGamesDict.ContainsKey(appId))
                            {
                                candidateAppIds.Add(appId);
                            }
                        }
                    }
                    else
                    {
                        // All mutual games: friend owns it, you own it, and it's in your Playnite Steam list
                        foreach (var appId in friendOwnedGames)
                        {
                            if (yourOwnedGames.Contains(appId) && steamGamesDict.ContainsKey(appId))
                            {
                                candidateAppIds.Add(appId);
                            }
                        }
                    }
                }
                else
                {
                    // Incremental: only apps where this friend already has cached achievements
                    if (!friendHasCached)
                    {
                        return newEntries;
                    }

                    foreach (var kv in appMap)
                    {
                        var appId = kv.Key;
                        // If configured to search all my games, don't require friendOwnedGames.Contains
                        if ((_settings.SearchAllMyGames || (friendOwnedGames != null && friendOwnedGames.Contains(appId))) &&
                            yourOwnedGames.Contains(appId) &&
                            steamGamesDict.ContainsKey(appId))
                        {
                            candidateAppIds.Add(appId);
                        }
                    }
                }

                _logger.Debug(
                    $"Starting friend {friend.PersonaName} ({friend.SteamId}), " +
                    $"owned={friendOwnedGames.Count}, candidates={candidateAppIds.Count}, inCache={friendHasCached}, mode={mode}");

                    if (candidateAppIds.Count == 0)
                    {
                        if (mode == CacheRebuildMode.Full)
                        {
                            _logger.Debug($"Skipping {friend.PersonaName} ({friend.SteamId}) - no mutual games with your Playnite library.");
                        }
                        return newEntries;
                    }

                foreach (var appId in candidateAppIds)
                {
                    cancel.ThrowIfCancellationRequested();

                    try
                    {
                        var friendAchievements = GetPlayerAchievements(friend.SteamId, appId);

                        // Full mode: quick skip if there's nothing newer than lastUpdatedUtc
                        if (mode == CacheRebuildMode.Full &&
                            lastUpdatedUtc.HasValue &&
                            friendHasCached)
                        {
                            var hasNewSinceLastUpdate = friendAchievements.Any(a =>
                                a.Achieved &&
                                a.UnlockTime.HasValue &&
                                a.UnlockTime.Value.ToUniversalTime() > lastUpdatedUtc.Value);

                            if (!hasNewSinceLastUpdate)
                            {
                                continue;
                            }
                        }

                        var game = steamGamesDict[appId];

                        DateTime maxCachedForApp = DateTime.MinValue;
                        var hasMaxForApp = friendHasCached && appMap.TryGetValue(appId, out maxCachedForApp);

                        foreach (var ach in friendAchievements)
                        {
                            if (!ach.Achieved || !ach.UnlockTime.HasValue)
                            {
                                continue;
                            }

                            var unlockUtc = ach.UnlockTime.Value.ToUniversalTime();

                            if (mode == CacheRebuildMode.Incremental)
                            {
                                // Only strictly newer than the cached max
                                if (!hasMaxForApp || unlockUtc <= maxCachedForApp)
                                {
                                    continue;
                                }
                            }
                            else
                            {
                                // Full mode: respect per-app max and global lastUpdatedUtc
                                if (hasMaxForApp && unlockUtc <= maxCachedForApp)
                                {
                                    continue;
                                }

                                if (lastUpdatedUtc.HasValue &&
                                    friendHasCached &&
                                    unlockUtc <= lastUpdatedUtc.Value)
                                {
                                    continue;
                                }
                            }

                            var key = ach.ApiName ?? string.Empty;
                            var entryId = friend.SteamId + ":" + appId + ":" + key + ":" + ach.UnlockTime.Value.Ticks;

                            // Skip if exact ID already exists in cache
                            if (existingIds.Contains(entryId))
                            {
                                continue;
                            }

                            var meta = ResolveAchievementMeta(appId, game, key, schemaCache);

                            var entry = new FeedEntry
                            {
                                Id = entryId,
                                FriendSteamId = friend.SteamId,
                                FriendPersonaName = friend.PersonaName,
                                FriendAvatarUrl = friend.AvatarMediumUrl,
                                GameName = game.Name,
                                PlayniteGameId = game.Id,
                                AppId = appId,
                                AchievementApiName = key,
                                AchievementDisplayName = meta.Name,
                                AchievementDescription = meta.Description,
                                AchievementIconUrl = meta.IconUnlocked,
                                AchievementIconUnlockedUrl = meta.IconUnlocked,
                                UnlockTime = ach.UnlockTime.Value.ToLocalTime()
                            };

                            if (_settings.HideAchievementsLockedForYou)
                            {
                                var yourSet = GetYourAchievementsCachedByApp(appId);
                                if (yourSet != null && !yourSet.Contains(key))
                                {
                                    entry.AchievementIconUrl = string.IsNullOrWhiteSpace(meta.IconLocked) ? meta.IconUnlocked : meta.IconLocked;
                                    entry.HideDescription = true;
                                }
                            }

                            // Ensure unlocked icon URL is always present to avoid blanking when revealing.
                            if (string.IsNullOrWhiteSpace(entry.AchievementIconUnlockedUrl))
                            {
                                entry.AchievementIconUnlockedUrl =
                                    !string.IsNullOrWhiteSpace(meta.IconUnlocked) ? meta.IconUnlocked :
                                    !string.IsNullOrWhiteSpace(meta.IconLocked) ? meta.IconLocked :
                                    entry.AchievementIconUrl;
                            }

                            if (_settings.IncludeMyUnlockTime)
                            {
                                try
                                {
                                    var dict = GetYourAchievementsWithTimesCached(appId);
                                    if (dict != null && dict.TryGetValue(key, out var myTime) && myTime.HasValue)
                                    {
                                        entry.MyUnlockTime = myTime.Value.ToLocalTime();
                                    }
                                }
                                catch
                                {
                                    // ignore
                                }
                            }

                            newEntries.Add(entry);
                            friendAdded++;
                        }
                    }
                    catch (Exception exApp)
                    {
                        _logger.Warn($"Failed to load achievements ({mode}) for {friend.PersonaName} in app {appId}");
                        _logger.Debug($"Exception loading achievements ({mode}) for {friend.PersonaName} app {appId}: {exApp.Message}");
                    }
                }
            }
            catch (Exception e)
            {
                if (e is OperationCanceledException)
                {
                    throw;
                }

                if (e.Message.Contains("private") || e.Message.Contains("403") || e.Message.Contains("401"))
                {
                    _logger.Debug($"Skipping {friend.PersonaName} (private profile, mode={mode})");
                }
                else
                {
                    _logger.Warn($"Failed to load data ({mode}) for friend {friend.PersonaName}");
                    _logger.Debug($"Exception loading data ({mode}) for friend {friend.PersonaName}: {e.Message}");
                }
            }
            _logger.Debug($"Finished friend ({mode}) {friend.PersonaName}, added {friendAdded} achievements");
            return newEntries;
        }

        #endregion

        #region Cache rebuild – public entrypoints

        /// <summary>
        /// Slow but complete rebuild.
        /// Scans all mutual games for all friends (with smart early exits),
        /// and ensures all missing data is filled in.
        /// </summary>
        public async Task RebuildCacheFullAsync(IProgress<ProgressReport> progress, CancellationToken cancel)
        {
            if (!IsSteamConfigured())
            {
                progress?.Report(new ProgressReport
                {
                    Message = ResourceProvider.GetString("LOCFriendsAchFeed_Error_SteamNotConfigured"),
                    CurrentStep = 0,
                    TotalSteps = 1
                });
                return;
            }

            var schemaCache = new ConcurrentDictionary<int, Dictionary<string, AchievementMeta>>();

            // Get your owned games and limit scanned games to ones you own
            var yourOwnedGames = GetOwnedGameIdsCached(_settings.SteamUserId);
            var steamGamesDict = BuildOwnedSteamGamesDict(yourOwnedGames);

            progress?.Report(new ProgressReport
            {
                Message = string.Format(ResourceProvider.GetString("LOCFriendsAchFeed_Rebuild_FoundOwnedGames_LoadingFriends"), steamGamesDict.Count),
                CurrentStep = 0,
                TotalSteps = 1
            });

            // Get friends list
            var allFriends = GetFriends(_settings.SteamUserId);
            var totalSteps = allFriends.Count;

            progress?.Report(new ProgressReport
            {
                Message = string.Format(ResourceProvider.GetString("LOCFriendsAchFeed_Rebuild_FoundFriends_LoadingAchievements"), allFriends.Count),
                CurrentStep = 0,
                TotalSteps = totalSteps
            });

            // Existing cache snapshot (read-only during this rebuild)
            var existingEntries = _cacheService.GetCachedEntries();
            var existingIds = new HashSet<string>(existingEntries.Select(e => e.Id));
            var friendAppMaxUnlock = BuildFriendAppMaxUnlockMap(existingEntries);
            var lastUpdatedUtc = _cacheService.GetCacheLastUpdated();

            var allEntries = new List<FeedEntry>();
            var allEntriesLock = new object();

            // Parallel processing per friend (bounded)
            var maxDegreeOfParallelism = Math.Max(1, _settings.RebuildParallelism);
            var semaphore = new SemaphoreSlim(maxDegreeOfParallelism);
            var tasks = new List<Task>();
            var processedFriends = 0;

            foreach (var friend in allFriends)
            {
                await semaphore.WaitAsync(cancel).ConfigureAwait(false);
                var localFriend = friend;

                var task = Task.Run(() =>
                {
                    try
                    {
                        cancel.ThrowIfCancellationRequested();

                        var friendEntries = ProcessFriend(
                            localFriend,
                            CacheRebuildMode.Full,
                            yourOwnedGames,
                            steamGamesDict,
                            friendAppMaxUnlock,
                            existingIds,
                            schemaCache,
                            lastUpdatedUtc,
                            cancel,
                            out var friendAdded);

                        lock (allEntriesLock)
                        {
                            allEntries.AddRange(friendEntries);
                            processedFriends++;

                                progress?.Report(new ProgressReport
                            {
                                Message = string.Format(ResourceProvider.GetString("LOCFriendsAchFeed_Rebuild_LoadingAchievementsFor"), localFriend.PersonaName, processedFriends, totalSteps),
                                CurrentStep = processedFriends,
                                TotalSteps = totalSteps
                            });
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }, cancel);

                tasks.Add(task);
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);

            progress?.Report(new ProgressReport
            {
                Message = string.Format(ResourceProvider.GetString("LOCFriendsAchFeed_Rebuild_FoundNewEntries"), allEntries.Count),
                CurrentStep = totalSteps,
                TotalSteps = totalSteps
            });

            // Merge or replace cache without re-filtering by time.
            // Per-friend time filtering has already been applied above.
            if (lastUpdatedUtc.HasValue)
            {
                    if (allEntries.Any())
                    {
                        _cacheService.MergeUpdateCache(allEntries);
                        progress?.Report(new ProgressReport
                        {
                            Message = string.Format(ResourceProvider.GetString("LOCFriendsAchFeed_Rebuild_CacheMerged"), allEntries.Count),
                            CurrentStep = totalSteps,
                            TotalSteps = totalSteps
                        });
                    }
                    else
                    {
                        progress?.Report(new ProgressReport
                        {
                            Message = ResourceProvider.GetString("LOCFriendsAchFeed_Rebuild_NoNewSinceLastUpdate"),
                            CurrentStep = totalSteps,
                            TotalSteps = totalSteps
                        });
                    }
            }
            else
            {
                // No existing cache - perform a full replace
                _cacheService.UpdateCache(allEntries);
                progress?.Report(new ProgressReport
                {
                    Message = string.Format(ResourceProvider.GetString("LOCFriendsAchFeed_Rebuild_CacheUpdated"), allEntries.Count),
                    CurrentStep = totalSteps,
                    TotalSteps = totalSteps
                });
            }
        }

        /// <summary>
        /// Fast but lossy incremental rebuild.
        /// Only checks (friend, appId) pairs that already have cached achievements.
        /// This means:
        ///  - It will pick up new achievements for games your friends have already earned something in.
        ///  - It will NOT discover:
        ///      * brand new friends,
        ///      * brand new games for a friend (with no cached achievements),
        ///      * older missing achievements if they are below the known max UnlockTime.
        /// </summary>
        public async Task RebuildCacheIncrementalAsync(IProgress<ProgressReport> progress, CancellationToken cancel)
        {
            if (!IsSteamConfigured())
            {
                progress?.Report(new ProgressReport
                {
                    Message = ResourceProvider.GetString("LOCFriendsAchFeed_Error_SteamNotConfigured"),
                    CurrentStep = 0,
                    TotalSteps = 1
                });
                return;
            }

            // Snapshot of existing cache
            var existingEntries = _cacheService.GetCachedEntries();
            if (existingEntries == null || existingEntries.Count == 0)
            {
                // No cache yet -> fall back to full rebuild, otherwise incremental has nothing to anchor to.
                progress?.Report(new ProgressReport
                {
                    Message = ResourceProvider.GetString("LOCFriendsAchFeed_Rebuild_NoExistingCache_FullRebuild"),
                    CurrentStep = 0,
                    TotalSteps = 1
                });

                await RebuildCacheFullAsync(progress, cancel).ConfigureAwait(false);
                return;
            }

            var existingIds = new HashSet<string>(existingEntries.Select(e => e.Id));
            var friendAppMaxUnlock = BuildFriendAppMaxUnlockMap(existingEntries);

            var schemaCache = new ConcurrentDictionary<int, Dictionary<string, AchievementMeta>>();

            // Build Steam games dict as in full rebuild
            var yourOwnedGames = GetOwnedGameIdsCached(_settings.SteamUserId);
            var steamGamesDict = BuildOwnedSteamGamesDict(yourOwnedGames);

            progress?.Report(new ProgressReport
            {
                Message = ResourceProvider.GetString("LOCFriendsAchFeed_Rebuild_RunningIncremental"),
                CurrentStep = 0,
                TotalSteps = 1
            });

            var allFriends = GetFriends(_settings.SteamUserId);

            // Only process friends that already have cached achievements
            var friendsToProcess = allFriends
                .Where(f => friendAppMaxUnlock.ContainsKey(f.SteamId))
                .ToList();

            var totalSteps = friendsToProcess.Count;

            progress?.Report(new ProgressReport
            {
                Message = string.Format(ResourceProvider.GetString("LOCFriendsAchFeed_Rebuild_FoundFriendsWithCache_CheckingUpdates"), totalSteps),
                CurrentStep = 0,
                TotalSteps = totalSteps
            });

            var allEntries = new List<FeedEntry>();
            var allEntriesLock = new object();
            var maxDegreeOfParallelism = Math.Max(1, _settings.RebuildParallelism);
            var semaphore = new SemaphoreSlim(maxDegreeOfParallelism);
            var tasks = new List<Task>();
            var processedFriends = 0;

            foreach (var friend in friendsToProcess)
            {
                await semaphore.WaitAsync(cancel).ConfigureAwait(false);
                var localFriend = friend;

                var task = Task.Run(() =>
                {
                    try
                    {
                        cancel.ThrowIfCancellationRequested();

                        var friendEntries = ProcessFriend(
                            localFriend,
                            CacheRebuildMode.Incremental,
                            yourOwnedGames,
                            steamGamesDict,
                            friendAppMaxUnlock,
                            existingIds,
                            schemaCache,
                            lastUpdatedUtc: null,   // not used in incremental
                            cancel,
                            out var friendAdded);

                        lock (allEntriesLock)
                        {
                            allEntries.AddRange(friendEntries);
                            processedFriends++;

                            progress?.Report(new ProgressReport
                            {
                                Message = string.Format(ResourceProvider.GetString("LOCFriendsAchFeed_Rebuild_QuickUpdateFor"), localFriend.PersonaName, processedFriends, totalSteps),
                                CurrentStep = processedFriends,
                                TotalSteps = totalSteps
                            });
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // respect cancellation
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }, cancel);

                tasks.Add(task);
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);

            progress?.Report(new ProgressReport
            {
                Message = string.Format(ResourceProvider.GetString("LOCFriendsAchFeed_Rebuild_IncrementalFoundNewEntries"), allEntries.Count),
                CurrentStep = totalSteps,
                TotalSteps = totalSteps
            });

            if (allEntries.Any())
            {
                _cacheService.MergeUpdateCache(allEntries);
                progress?.Report(new ProgressReport
                {
                    Message = string.Format(ResourceProvider.GetString("LOCFriendsAchFeed_Rebuild_CacheMergedIncremental"), allEntries.Count),
                    CurrentStep = totalSteps,
                    TotalSteps = totalSteps
                });
            }
            else
            {
                progress?.Report(new ProgressReport
                {
                    Message = ResourceProvider.GetString("LOCFriendsAchFeed_Rebuild_NoNewIncremental"),
                    CurrentStep = totalSteps,
                    TotalSteps = totalSteps
                });
            }
        }

        #endregion
    }
}
