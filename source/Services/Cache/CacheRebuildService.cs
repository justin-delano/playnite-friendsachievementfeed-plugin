using FriendsAchievementFeed.Models;
using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SteamFriendModel = FriendsAchievementFeed.Models.SteamFriend;

namespace FriendsAchievementFeed.Services
{
    internal sealed class CacheRebuildService
    {
        private readonly SteamDataProvider _steam;
        private readonly FeedEntryFactory _entryFactory;
        private readonly CacheService _cacheService;
        private readonly FriendsAchievementFeedSettings _settings;
        private readonly ILogger _logger;

        public CacheRebuildService(
            SteamDataProvider steam,
            FeedEntryFactory entryFactory,
            CacheService cacheService,
            FriendsAchievementFeedSettings settings,
            ILogger logger)
        {
            _steam = steam;
            _entryFactory = entryFactory;
            _cacheService = cacheService;
            _settings = settings;
            _logger = logger;
        }

        /// <summary>
        /// friendSteamId -> (appId -> max unlock time in cache)
        /// </summary>
        private Dictionary<string, Dictionary<int, DateTime>> BuildFriendAppMaxUnlockMap(IEnumerable<FeedEntry> existingEntries)
        {
            var result = new Dictionary<string, Dictionary<int, DateTime>>(StringComparer.OrdinalIgnoreCase);

            foreach (var e in existingEntries)
            {
                if (e == null) continue;

                if (!result.TryGetValue(e.FriendSteamId, out var appMap))
                {
                    appMap = new Dictionary<int, DateTime>();
                    result[e.FriendSteamId] = appMap;
                }

                var unlockUtc = FeedEntryFactory.AsUtcKind(e.UnlockTime);
                if (!appMap.TryGetValue(e.AppId, out var existing) || unlockUtc > existing)
                {
                    appMap[e.AppId] = unlockUtc;
                }
            }

            return result;
        }

        /// <summary>
        /// Delta strategy:
        /// - If no previous baseline for friend: scan games where current playtime > 0
        /// - Otherwise: scan games where current playtime > previous playtime
        /// Also filters to games in steamGamesDict (your Playnite Steam library mapping).
        /// </summary>
        private static List<int> GetCandidateAppsFromPlaytime(
            Dictionary<int, int> currentMutualPlaytime,
            Dictionary<int, int> previousMutualPlaytime,
            bool isInitialForFriend,
            Dictionary<int, Game> steamGamesDict,
            IEnumerable<int> forcedAppIds)
        {
            var candidates = new List<int>();

            // 1) Per-app forced scans (independent of playtime visibility)
            if (forcedAppIds != null)
            {
                foreach (var appId in forcedAppIds)
                {
                    if (appId <= 0) continue;

                    // Must map to a Playnite Steam game (need Game object downstream)
                    if (steamGamesDict != null && !steamGamesDict.ContainsKey(appId))
                    {
                        continue;
                    }

                    candidates.Add(appId);
                }
            }

            // 2) Normal playtime-delta candidates
            if (currentMutualPlaytime == null || currentMutualPlaytime.Count == 0)
            {
                return candidates.Distinct().ToList();
            }

            foreach (var kv in currentMutualPlaytime)
            {
                var appId = kv.Key;
                var cur = kv.Value;

                // Only scan games you can map to a Playnite Steam game (or you'll have no Game object)
                if (steamGamesDict != null && !steamGamesDict.ContainsKey(appId))
                {
                    continue;
                }

                if (isInitialForFriend)
                {
                    if (cur > 0)
                    {
                        candidates.Add(appId);
                    }
                }
                else
                {
                    var prev = 0;
                    previousMutualPlaytime?.TryGetValue(appId, out prev);

                    // catches: increased playtime, and "new mutual game appears" (prev defaults to 0)
                    if (cur > prev)
                    {
                        candidates.Add(appId);
                    }
                }
            }

            return candidates.Distinct().ToList();
        }

        private async Task ProcessFriendAsync(
            SteamFriendModel friend,
            HashSet<int> yourOwnedGames,
            Dictionary<int, Game> steamGamesDict,
            Dictionary<string, Dictionary<int, DateTime>> friendAppMaxUnlock,
            HashSet<string> existingIds,
            DateTime? lastUpdatedUtc,
            Dictionary<string, Dictionary<int, int>> prevPlaytimeSnapshot,
            Dictionary<string, Dictionary<int, int>> newPlaytimeSnapshot,
            ConcurrentBag<FeedEntry> results,
            CancellationToken cancel)
        {
            try
            {
                cancel.ThrowIfCancellationRequested();

                prevPlaytimeSnapshot.TryGetValue(friend.SteamId, out var prevForFriend);
                var isInitialForFriend = (prevForFriend == null || prevForFriend.Count == 0);

                // Pull current mutual playtimes (friend owned + playtime, filtered by your owned set)
                var currentMutualPlaytime = await _steam
                    .GetMutualOwnedGamePlaytimesAsync(friend.SteamId, yourOwnedGames, cancel)
                    .ConfigureAwait(false);

                // If current is empty but we had a previous baseline, treat as "can't update right now"
                // and keep the old baseline so we don't trigger big rescans next run.
                if ((currentMutualPlaytime == null || currentMutualPlaytime.Count == 0) &&
                    prevForFriend != null && prevForFriend.Count > 0)
                {
                    newPlaytimeSnapshot[friend.SteamId] = prevForFriend;
                    return;
                }

                // Record new snapshot (even if empty)
                newPlaytimeSnapshot[friend.SteamId] = currentMutualPlaytime ?? new Dictionary<int, int>();

                var candidateAppIds = GetCandidateAppsFromPlaytime(
                    currentMutualPlaytime,
                    prevForFriend,
                    isInitialForFriend,
                    steamGamesDict,
                    _settings?.ForcedScanAppIds);

                if (candidateAppIds.Count == 0)
                {
                    return;
                }

                // Treat "friendOwnedGames" as the set of mutual app ids from the playtime dict
                var friendOwnedGames = new HashSet<int>((currentMutualPlaytime ?? new Dictionary<int, int>()).Keys);

                var batchEntries = await ProcessFriendAppsAsync(
                    friend,
                    friendOwnedGames,
                    candidateAppIds,
                    yourOwnedGames,
                    steamGamesDict,
                    friendAppMaxUnlock,
                    existingIds,
                    lastUpdatedUtc,
                    cancel).ConfigureAwait(false);

                if (batchEntries != null && batchEntries.Count > 0)
                {
                    foreach (var e in batchEntries)
                    {
                        results.Add(e);
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Error rebuilding cache for friend {friend?.SteamId}");
            }
        }

        private async Task<List<FeedEntry>> ProcessFriendAppsAsync(
            SteamFriendModel friend,
            HashSet<int> friendOwnedGames,
            IEnumerable<int> appIds,
            HashSet<int> yourOwnedGames,
            Dictionary<int, Game> steamGamesDict,
            Dictionary<string, Dictionary<int, DateTime>> friendAppMaxUnlock,
            HashSet<string> existingIds,
            DateTime? lastUpdatedUtc,
            CancellationToken cancel)
        {
            var result = new List<FeedEntry>();
            if (appIds == null || friend == null)
            {
                return result;
            }

            cancel.ThrowIfCancellationRequested();

            // Fallback fetch if needed (should be rare now; we usually pass a set)
            if (friendOwnedGames == null)
            {
                friendOwnedGames = await _steam.GetOwnedGameIdsAsync(friend.SteamId).ConfigureAwait(false);
            }

            Dictionary<int, DateTime> appMap = null;
            var friendHasAnyCached = friendAppMaxUnlock != null
                && friendAppMaxUnlock.TryGetValue(friend.SteamId, out appMap)
                && appMap != null
                && appMap.Count > 0;

            foreach (var appId in appIds)
            {
                cancel.ThrowIfCancellationRequested();

                if (friendOwnedGames == null || friendOwnedGames.Count == 0)
                {
                    continue;
                }

                if (!friendOwnedGames.Contains(appId) || !yourOwnedGames.Contains(appId) || !steamGamesDict.ContainsKey(appId))
                {
                    continue;
                }

                var friendRows = await _steam.GetScrapedAchievementsAsync(friend.SteamId, appId, cancel).ConfigureAwait(false);
                if (friendRows == null || friendRows.Count == 0)
                {
                    continue;
                }

                // Only fetch self data if friend has achievements page
                try
                {
                    await _steam.EnsureSelfAchievementDataAsync(_settings.SteamUserId, appId, cancel).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch { }

                var game = steamGamesDict[appId];

                // Max cached unlock time for this friend+app (if any)
                var maxCachedForApp = DateTime.MinValue;
                var hasMaxForApp = friendHasAnyCached && appMap != null && appMap.TryGetValue(appId, out maxCachedForApp);

                foreach (var row in friendRows)
                {
                    if (row == null || string.IsNullOrWhiteSpace(row.Key))
                    {
                        continue;
                    }

                    var unlockUtc = FeedEntryFactory.AsUtcKind(row.UnlockTimeUtc);
                    if (!unlockUtc.HasValue)
                    {
                        continue;
                    }

                    var u = unlockUtc.Value;

                    // Core rule: only add achievements newer than the max we've cached for this friend+app.
                    // IMPORTANT: missing max must NOT block new pairs.
                    if (hasMaxForApp && u <= maxCachedForApp)
                    {
                        continue;
                    }

                    // Optional: if you want to avoid backfilling old stuff during refreshes,
                    // only apply the global lastUpdated guard when we already have a max for this app.
                    if (hasMaxForApp && lastUpdatedUtc.HasValue && u <= lastUpdatedUtc.Value)
                    {
                        continue;
                    }

                    var entryId = friend.SteamId + ":" + appId + ":" + row.Key + ":" + u.Ticks;
                    if (existingIds.Contains(entryId))
                    {
                        continue;
                    }

                    result.Add(_entryFactory.CreateRaw(friend, game, appId, row, u));
                }
            }

            return result;
        }

        /// <summary>
        /// The only rebuild you need now:
        /// - Loads previous friend playtime baseline (may be empty on first run)
        /// - Fetches current mutual playtimes per friend
        /// - Scans candidate (friend, app) pairs per delta rules
        /// - Merges new achievements into cache
        /// - Writes new friend playtime baseline snapshot
        /// </summary>
        public async Task RebuildCacheAsync(IProgress<ProgressReport> progress, CancellationToken cancel)
        {
            if (string.IsNullOrWhiteSpace(_settings.SteamUserId))
            {
                progress?.Report(new ProgressReport
                {
                    Message = ResourceProvider.GetString("LOCFriendsAchFeed_Error_SteamNotConfigured"),
                    CurrentStep = 0,
                    TotalSteps = 1
                });
                return;
            }

            progress?.Report(new ProgressReport
            {
                Message = ResourceProvider.GetString("LOCFriendsAchFeed_Targeted_LoadingOwnedGames"),
                CurrentStep = 0,
                TotalSteps = 1
            });

            var yourOwnedGames = await _steam.GetOwnedGameIdsAsync(_settings.SteamUserId).ConfigureAwait(false);

            progress?.Report(new ProgressReport
            {
                Message = ResourceProvider.GetString("LOCFriendsAchFeed_Targeted_BuildingOwnedGames"),
                CurrentStep = 0,
                TotalSteps = 1
            });

            var steamGamesDict = _steam.BuildOwnedSteamGamesDict(yourOwnedGames);

            progress?.Report(new ProgressReport
            {
                Message = ResourceProvider.GetString("LOCFriendsAchFeed_Targeted_LoadingFriends"),
                CurrentStep = 0,
                TotalSteps = 1
            });

            var allFriends = await _steam.GetFriendsAsync(_settings.SteamUserId).ConfigureAwait(false);
            allFriends = allFriends ?? new List<SteamFriendModel>();
            var friendsCount = allFriends.Count;

            // Existing achievement cache
            var existingEntries = _cacheService.GetCachedEntries() ?? new List<FeedEntry>();
            var existingIds = new HashSet<string>(existingEntries.Select(e => e.Id), StringComparer.OrdinalIgnoreCase);
            var friendAppMaxUnlock = BuildFriendAppMaxUnlockMap(existingEntries);
            var lastUpdatedUtc = _cacheService.GetCacheLastUpdated();

            // Previous playtime baseline (empty on first run => initial scans happen automatically)
            var prevPlaytime = _cacheService.GetFriendPlaytimeCache()
                ?? new Dictionary<string, Dictionary<int, int>>(StringComparer.OrdinalIgnoreCase);

            progress?.Report(new ProgressReport
            {
                Message = string.Format(ResourceProvider.GetString("LOCFriendsAchFeed_Rebuild_FoundFriends_LoadingAchievements"), friendsCount),
                CurrentStep = 0,
                TotalSteps = Math.Max(1, friendsCount)
            });

            var allNewEntries = new ConcurrentBag<FeedEntry>();
            var newPlaytimeSnapshot = new Dictionary<string, Dictionary<int, int>>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < allFriends.Count; i++)
            {
                cancel.ThrowIfCancellationRequested();

                var friend = allFriends[i];
                progress?.Report(new ProgressReport
                {
                    Message = string.Format(
                        ResourceProvider.GetString("LOCFriendsAchFeed_Rebuild_LoadingAchievementsFor"),
                        friend.PersonaName, i + 1, friendsCount),
                    CurrentStep = i,
                    TotalSteps = Math.Max(1, friendsCount)
                });

                await ProcessFriendAsync(
                    friend,
                    yourOwnedGames,
                    steamGamesDict,
                    friendAppMaxUnlock,
                    existingIds,
                    lastUpdatedUtc,
                    prevPlaytime,
                    newPlaytimeSnapshot,
                    allNewEntries,
                    cancel).ConfigureAwait(false);

                progress?.Report(new ProgressReport
                {
                    Message = string.Format(
                        ResourceProvider.GetString("LOCFriendsAchFeed_Rebuild_LoadingAchievementsFor"),
                        friend.PersonaName, i + 1, friendsCount),
                    CurrentStep = i + 1,
                    TotalSteps = Math.Max(1, friendsCount)
                });
            }

            try { _logger?.Debug($"[CacheRebuild] Refresh finished; totalFound={allNewEntries.Count}"); } catch { }

            progress?.Report(new ProgressReport
            {
                Message = string.Format(ResourceProvider.GetString("LOCFriendsAchFeed_Rebuild_FoundNewEntries"), allNewEntries.Count),
                CurrentStep = friendsCount,
                TotalSteps = Math.Max(1, friendsCount)
            });

            // Persist achievements (create file if missing; otherwise merge)
            if (!_cacheService.CacheFileExists())
            {
                _cacheService.UpdateCache(allNewEntries.ToList());
            }
            else if (allNewEntries.Any())
            {
                _cacheService.MergeUpdateCache(allNewEntries.ToList());
            }

            // Persist playtime baseline snapshot (overwrite)
            _cacheService.UpdateFriendPlaytimeCache(newPlaytimeSnapshot);
        }
    }
}
