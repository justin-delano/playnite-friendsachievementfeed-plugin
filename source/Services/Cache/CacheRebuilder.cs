// CacheRebuildService.cs
using Common;
using FriendsAchievementFeed.Models;
using FriendsAchievementFeed.Services.Steam.Models;
using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Diagnostics;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SteamFriendModel = FriendsAchievementFeed.Models.SteamFriend;

namespace FriendsAchievementFeed.Services
{
    internal sealed class CacheRebuilder
    {
        private readonly ISteamDataProvider _steam;
        private readonly FeedEntryFactory _entryFactory;
        private readonly ICacheManager _cacheService;
        private readonly FriendsAchievementFeedSettings _settings;
        private readonly ILogger _logger;
        private readonly IPlayniteAPI _api;
        private readonly SelfAchievementCacheManager _selfAchievementManager;

        public CacheRebuilder(
            ISteamDataProvider steam,
            FeedEntryFactory entryFactory,
            ICacheManager CacheManager,
            FriendsAchievementFeedSettings settings,
            ILogger logger,
            IPlayniteAPI api)
        {
            _steam = steam;
            _entryFactory = entryFactory;
            _cacheService = CacheManager;
            _settings = settings;
            _logger = logger;
            _api = api;
            _selfAchievementManager = new SelfAchievementCacheManager(steam, CacheManager, settings, logger);
        }

        public Task<SelfAchievementGameData> EnsureSelfAchievementDataAsync(string playniteGameId, int appId, CancellationToken cancel)
            => _selfAchievementManager.EnsureSelfAchievementDataAsync(playniteGameId, appId, cancel);

        public Task<SelfAchievementGameData> EnsureSelfAchievementDataAsync(
            string playniteGameId,
            int appId,
            CancellationToken cancel,
            bool forceRefresh)
            => _selfAchievementManager.EnsureSelfAchievementDataAsync(playniteGameId, appId, cancel, forceRefresh);

        private void EmitUpdate(Action<RebuildUpdate> onUpdate, RebuildUpdate update)
        {
            if (onUpdate == null || update == null) return;

            try
            {
                onUpdate(update);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "[FAF] Rebuild progress handler threw.");
            }
        }

        // --------------------
        // Progress helper
        // --------------------
        private sealed class Progress
        {
            private readonly CacheRebuilder _svc;
            private readonly Action<RebuildUpdate> _cb;
            private readonly int _emitEvery;
            private readonly TimeSpan _min;
            private DateTime _last;

            public int OverallIndex { get; private set; }
            public int OverallCount { get; }

            public Progress(CacheRebuilder svc, Action<RebuildUpdate> cb, int overallCount, int emitEvery = 10, int minMs = 250)
            {
                _svc = svc; _cb = cb;
                OverallCount = Math.Max(0, overallCount);
                _emitEvery = Math.Max(1, emitEvery);
                _min = TimeSpan.FromMilliseconds(Math.Max(0, minMs));
            }

            public void Step()
            {
                if (OverallIndex < OverallCount) OverallIndex++;
            }

            public void Emit(RebuildUpdate u, bool force = false, int? i = null, int? total = null)
            {
                if (u == null) return;

                var now = DateTime.UtcNow;
                var ok =
                    force ||
                    (i.HasValue && total.HasValue &&
                        (i.Value == 0 || i.Value == total.Value - 1 ||
                         ((i.Value + 1) % _emitEvery == 0) ||
                         (now - _last) >= _min)) ||
                    (!i.HasValue && (now - _last) >= _min);

                if (!ok) return;

                _last = now;
                u.OverallIndex = OverallIndex;
                u.OverallCount = OverallCount;
                _svc.EmitUpdate(_cb, u);
            }
        }

        // --------------------
        // Helpers: sets/maps
        // --------------------



        private List<int> ResolveExplicitAppIds(CacheScanOptions opt, Dictionary<int, Game> steamGamesDict)
        {
            if (opt == null || steamGamesDict == null || steamGamesDict.Count == 0)
                return null;

            // Preserve insertion-ish order while avoiding duplicates.
            var seen = new HashSet<int>();
            var ordered = new List<int>();

            foreach (var id in opt.SteamAppIds ?? Enumerable.Empty<int>())
            {
                if (id <= 0) continue;
                if (!steamGamesDict.TryGetValue(id, out _)) continue;
                if (seen.Add(id)) ordered.Add(id);
            }

            var playniteIds = opt.PlayniteGameIds;
            if (playniteIds != null && playniteIds.Count > 0)
            {
                var wanted = new HashSet<Guid>(playniteIds);
                var dbGames = _api?.Database?.Games;

                if (dbGames != null && wanted.Count > 0)
                {
                    foreach (var g in dbGames)
                    {
                        if (g == null) continue;
                        if (!wanted.Contains(g.Id)) continue;

                        if (_steam.TryGetSteamAppId(g, out var appId) && appId > 0 && steamGamesDict.TryGetValue(appId, out _))
                        {
                            if (seen.Add(appId)) ordered.Add(appId);
                        }

                        wanted.Remove(g.Id);
                        if (wanted.Count == 0) break;
                    }
                }
            }

            return ordered.Count == 0 ? null : ordered;
        }











        // --------------------
        // Friend achievement row analysis
        // --------------------
        private sealed class FriendRowAnalysis
        {
            public bool SawAnyUnlocked;
            public List<(FriendsAchievementFeed.Services.Steam.Models.ScrapedAchievementRow Row, DateTime UnlockUtc)> NewCandidates =
                new List<(FriendsAchievementFeed.Services.Steam.Models.ScrapedAchievementRow, DateTime)>();
        }

        private FriendRowAnalysis AnalyzeFriendRowsForApp(
            string friendSteamId,
            int appId,
            List<FriendsAchievementFeed.Services.Steam.Models.ScrapedAchievementRow> friendRows,
            bool friendHasAnyCached,
            Dictionary<int, DateTime> maxUnlockByAppForFriend,
            HashSet<string> existingIds)
        {
            var analysis = new FriendRowAnalysis();
            if (friendRows == null || friendRows.Count == 0)
                return analysis;

            var maxCachedForApp = DateTime.MinValue;
            var hasMaxForApp = false;

            // hasMaxForApp = we have baseline for THIS friend+app (not just "any cached anywhere")
            if (friendHasAnyCached && maxUnlockByAppForFriend != null)
                hasMaxForApp = maxUnlockByAppForFriend.TryGetValue(appId, out maxCachedForApp);

            // reduce string churn in the hot loop
            var entryPrefix = friendSteamId + ":" + appId + ":";

            foreach (var row in friendRows)
            {
                if (row == null || string.IsNullOrWhiteSpace(row.Key))
                    continue;

                var unlockUtc = DateTimeUtilities.AsUtcKind(row.UnlockTimeUtc);
                if (!unlockUtc.HasValue)
                    continue;

                analysis.SawAnyUnlocked = true;

                var u = unlockUtc.Value;

                if (hasMaxForApp)
                {
                    // Only gate against the last cached unlock for this friend+app; do not use cache write time
                    // so offline or late-arriving unlocks with older timestamps can still backfill correctly.
                    if (u <= maxCachedForApp)
                        continue;
                }

                var entryId = string.Concat(entryPrefix, row.Key, ":", u.Ticks);
                if (existingIds != null && existingIds.Contains(entryId))
                    continue;

                analysis.NewCandidates.Add((row, u));
            }

            return analysis;
        }

        // --------------------
        // Family-share forced apps inversion
        // --------------------
        private Dictionary<string, HashSet<int>> BuildFamilySharingForcedAppsByFriend(Dictionary<string, int> playniteIdToAppId)
        {
            var result = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);

            var all = _cacheService.LoadAllFamilySharingScanResults()
                ?? new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            if (all.Count == 0 || playniteIdToAppId == null || playniteIdToAppId.Count == 0)
                return result;

            foreach (var kv in all)
            {
                var playniteId = kv.Key;
                if (string.IsNullOrWhiteSpace(playniteId)) continue;

                if (!playniteIdToAppId.TryGetValue(playniteId, out var appId) || appId <= 0)
                    continue;

                foreach (var steamId in kv.Value ?? Enumerable.Empty<string>())
                {
                    if (string.IsNullOrWhiteSpace(steamId)) continue;

                    if (!result.TryGetValue(steamId, out var set))
                    {
                        set = new HashSet<int>();
                        result[steamId] = set;
                    }
                    set.Add(appId);
                }
            }

            return result;
        }

        // --------------------
        // Friend scan plan
        // --------------------
        private struct FriendPlan
        {
            public SteamFriendModel Friend;
            public List<int> Apps;

            public HashSet<int> Owned;   // ownership signal (minutes map keys)
            public HashSet<int> Forced;  // cached family-share forced scans
            public bool AllowUnowned;    // friend in IncludeUnownedFriendIds
            public bool ExplicitApps;    // apps explicitly selected (scan subset)

            public int CandidateGames;   // shared (and non-zero minutes when minutes are available)
            public bool OwnershipUnavailable;

            public bool UnownedScanAllowedForApp(int appId) =>
                ExplicitApps || AllowUnowned || (Forced != null && Forced.Contains(appId));
        }

        // --------------------
        // Unified scan entrypoint
        // --------------------
        public async Task<RebuildPayload> ScanAsync(
            CacheScanOptions options,
            Action<RebuildUpdate> onUpdate,
            CancellationToken cancel)
        {
            options ??= new CacheScanOptions();

            _cacheService.EnsureDiskCacheOrClearMemory();

            var payload = new RebuildPayload();

            if (!InputValidator.HasSteamCredentials(_settings?.SteamUserId, _settings?.SteamApiKey))
            {
                EmitUpdate(onUpdate, new RebuildUpdate { Kind = RebuildUpdateKind.Stage, Stage = RebuildStage.NotConfigured });
                payload.Summary = new RebuildSummary();
                return payload;
            }

            var sw = Stopwatch.StartNew();

            EmitUpdate(onUpdate, new RebuildUpdate { Kind = RebuildUpdateKind.Stage, Stage = RebuildStage.LoadingOwnedGames });

            var steamGamesDict = SteamLibraryProvider.BuildSteamLibraryGamesDict(_api, _steam, _logger);
            var myLibraryAppIds = new HashSet<int>(steamGamesDict.Keys);

            // Defer building PlayniteId->AppId map and family-share forced apps until we know
            // whether they're needed. These operations can be expensive (disk I/O, large maps)
            // and are not required for quick incremental scans.
            Dictionary<string, int> playniteIdToAppId = null;
            Dictionary<string, HashSet<int>> forcedAppsByFriend = null;

            EmitUpdate(onUpdate, new RebuildUpdate { Kind = RebuildUpdateKind.Stage, Stage = RebuildStage.LoadingFriends });

            var friendsResult = options.IncludeFriends
                ? await _steam.GetFriendsAsync(_settings.SteamUserId, _settings.SteamApiKey, cancel).ConfigureAwait(false)
                : null;
            var allFriends = friendsResult ?? new List<SteamFriendModel>();

            var friends = FriendScanner.FilterFriends(allFriends, options.FriendSteamIds);

            EmitUpdate(onUpdate, new RebuildUpdate { Kind = RebuildUpdateKind.Stage, Stage = RebuildStage.LoadingExistingCache });

            var existingEntries = _cacheService.GetCachedFriendEntries() ?? new List<FeedEntry>();

            var existingIds = new HashSet<string>(
                existingEntries.Where(e => e != null && !string.IsNullOrWhiteSpace(e.Id)).Select(e => e.Id),
                StringComparer.OrdinalIgnoreCase);

            var friendAppMaxUnlock = FriendScanner.BuildFriendAppMaxUnlockMap(existingEntries);

            var includeUnownedSet = FriendScanner.ToSet(options.IncludeUnownedFriendIds);

            // Explicit apps still exist as a general scan feature. QuickScanRecentPairs overrides it for friend selection.
            var explicitApps = ResolveExplicitAppIds(options, steamGamesDict);
            var hasExplicitApps = explicitApps != null && explicitApps.Count > 0;

            var quickScan = options.QuickScanRecentPairs;
            var quickFriendsCount = Math.Max(0, options.QuickScanRecentFriendsCount);
            var quickGamesPerFriend = Math.Max(0, options.QuickScanRecentGamesPerFriend);

            // Only build the PlayniteId->AppId map and family-share forced apps when needed.
            // Quick scans intentionally avoid family-share expansion and do not require this mapping.
            if (options.IncludeFriends && !quickScan)
            {
                playniteIdToAppId = SteamLibraryProvider.BuildPlayniteIdToAppId(steamGamesDict);
                forcedAppsByFriend = BuildFamilySharingForcedAppsByFriend(playniteIdToAppId);
            }

            // Track which games have any friend unlocked achievements, so self scan can skip others.
            var appsWithFriendUnlockedAchievements = new HashSet<int>();
            bool anyFriendAchievementRowsFetched = false;

            // ---------
            // Plan: friends
            // ---------
            var friendPlans = new List<FriendPlan>(friends?.Count ?? 0);
            var candidateGamesTotal = 0;
            var includeUnownedCandidatesTotal = 0;
            var friendsOwnershipUnavailable = 0;

            // Union of friend appIds being scanned (used for quick-scan self scan).
            var affectedAppIds = new HashSet<int>();

            if (options.IncludeFriends)
            {
                if (quickScan)
                {
                    // Quick scan picks "most recent" friends/games from cached feed activity.
                    // Build friend lookup without GroupBy/ToDictionary allocations.
                    var friendById = new Dictionary<string, SteamFriendModel>(StringComparer.OrdinalIgnoreCase);
                    foreach (var f in friends)
                    {
                        if (f == null || string.IsNullOrWhiteSpace(f.SteamId)) continue;
                        if (!friendById.ContainsKey(f.SteamId))
                            friendById[f.SteamId] = f;
                    }

                    // Single-pass: choose up to K friends and for each up to N recent distinct apps
                    var recentFriendIds = new List<string>(quickFriendsCount);
                    var recentAppsByFriend = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);

                    if (quickFriendsCount > 0 && quickGamesPerFriend > 0 && friendById.Count > 0 && existingEntries.Count > 0)
                    {
                        foreach (var e in existingEntries
                            .Where(x => x != null && !string.IsNullOrWhiteSpace(x.FriendSteamId) && x.AppId > 0)
                            .OrderByDescending(x => x.FriendUnlockTimeUtc))
                        {
                            var fid = e.FriendSteamId;
                            if (!friendById.ContainsKey(fid)) continue;
                            if (!steamGamesDict.TryGetValue(e.AppId, out _)) continue;

                            if (!recentAppsByFriend.TryGetValue(fid, out var apps))
                            {
                                if (recentFriendIds.Count >= quickFriendsCount)
                                    continue;

                                recentFriendIds.Add(fid);
                                apps = new List<int>(quickGamesPerFriend);
                                recentAppsByFriend[fid] = apps;
                            }

                            if (apps.Count >= quickGamesPerFriend)
                                continue;

                            // lists are tiny; Contains is fine
                            if (!apps.Contains(e.AppId))
                                apps.Add(e.AppId);
                        }
                    }

                    foreach (var fid in recentFriendIds)
                    {
                        cancel.ThrowIfCancellationRequested();

                        if (!friendById.TryGetValue(fid, out var friend) || friend == null)
                            continue;

                        if (!recentAppsByFriend.TryGetValue(fid, out var recentApps) || recentApps == null || recentApps.Count == 0)
                            continue;

                        var plan = new FriendPlan
                        {
                            Friend = friend,
                            Apps = new List<int>(recentApps),
                            Owned = new HashSet<int>(),
                            Forced = null,          // do not expand in quick scan
                            AllowUnowned = false,   // do not expand in quick scan
                            ExplicitApps = true,    // treat as an explicit bounded subset
                            CandidateGames = 0,
                            OwnershipUnavailable = false
                        };

                        // Small ownership/minutes fetch: only used to drop 0-minute games + ownership signal.
                        Dictionary<int, int> pt = null;
                        try
                        {
                            pt = await _steam.GetPlaytimesForAppsAsync(friend.SteamId, new HashSet<int>(plan.Apps), cancel).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            _logger?.Debug(ex, $"[FAF] QuickScan friend minutes lookup failed friend={friend.SteamId}");
                            pt = null;
                        }

                        plan.Owned = pt != null ? new HashSet<int>(pt.Keys) : new HashSet<int>();

                        if (pt != null && pt.Count > 0)
                        {
                            // Only use playtime to drop 0-minute candidates
                            var filtered = new List<int>(plan.Apps.Count);
                            foreach (var a in plan.Apps)
                            {
                                if (pt.TryGetValue(a, out var m) && m > 0)
                                    filtered.Add(a);
                            }
                            plan.Apps = filtered;
                        }
                        else
                        {
                            plan.OwnershipUnavailable = true;
                            friendsOwnershipUnavailable++;
                        }

                        plan.CandidateGames = plan.Apps?.Count ?? 0;

                        if (plan.Apps != null)
                            foreach (var a in plan.Apps)
                                affectedAppIds.Add(a);

                        candidateGamesTotal += plan.CandidateGames;
                        friendPlans.Add(plan);
                    }
                }
                else
                {
                    // Normal planning
                    foreach (var friend in friends)
                    {
                        cancel.ThrowIfCancellationRequested();
                        if (friend == null || string.IsNullOrWhiteSpace(friend.SteamId)) continue;

                        var plan = new FriendPlan
                        {
                            Friend = friend,
                            Apps = new List<int>(),
                            Owned = new HashSet<int>(),
                            Forced = (forcedAppsByFriend != null && forcedAppsByFriend.TryGetValue(friend.SteamId, out var fset)) ? fset : null,
                            AllowUnowned = includeUnownedSet.Contains(friend.SteamId),
                            ExplicitApps = hasExplicitApps,
                            CandidateGames = 0,
                            OwnershipUnavailable = false
                        };

                        // Explicit apps: scan same subset for all selected friends (or all friends).
                        if (hasExplicitApps)
                        {
                            var baseApps = new List<int>(explicitApps.Count);
                            foreach (var a in explicitApps)
                            {
                                if (a > 0 && steamGamesDict.TryGetValue(a, out _))
                                    baseApps.Add(a);
                            }

                            plan.Apps = baseApps;

                            Dictionary<int, int> pt = null;
                            try
                            {
                                _logger?.Info($"[FAF] Fetching explicit friend owned apps data friend={friend.SteamId} appsCount={(plan.Apps?.Count ?? 0)}");
                                pt = await _steam.GetPlaytimesForAppsAsync(friend.SteamId, new HashSet<int>(plan.Apps), cancel).ConfigureAwait(false);
                                _logger?.Info($"[FAF] Fetched explicit friend owned apps data friend={friend.SteamId}: count={(pt?.Count ?? 0)}");
                            }
                            catch (OperationCanceledException) { throw; }
                            catch (Exception ex)
                            {
                                _logger?.Debug(ex, $"[FAF] Friend owned apps lookup failed (explicit apps) {friend.SteamId}");
                                pt = null;
                            }

                            plan.Owned = pt != null ? new HashSet<int>(pt.Keys) : new HashSet<int>();

                            // Only use minutes to drop 0-minute games when we have them.
                            if (pt != null && pt.Count > 0)
                            {
                                var filtered = new List<int>(plan.Apps.Count);
                                foreach (var a in plan.Apps)
                                {
                                    if (pt.TryGetValue(a, out var m) && m > 0)
                                        filtered.Add(a);
                                }
                                plan.Apps = filtered;
                            }
                            else
                            {
                                plan.OwnershipUnavailable = true;
                                friendsOwnershipUnavailable++;
                            }

                            plan.CandidateGames = plan.Apps.Count;
                            candidateGamesTotal += plan.CandidateGames;

                            friendPlans.Add(plan);
                            continue;
                        }

                        // Non-explicit: default is "games in common" per friend.
                        var appsSet = new HashSet<int>();
                        int candidateCount = 0;

                        if (options.FriendsAllLibraryApps)
                        {
                            foreach (var a in steamGamesDict.Keys)
                                if (a > 0) appsSet.Add(a);

                            candidateCount = appsSet.Count;
                        }
                        else
                        {
                            Dictionary<int, int> friendMinutes = null;
                            try
                            {
                                friendMinutes = await _steam.GetPlaytimesForAppsAsync(friend.SteamId, myLibraryAppIds, cancel).ConfigureAwait(false);
                            }
                            catch (OperationCanceledException) { throw; }
                            catch (Exception ex)
                            {
                                _logger?.Debug(ex, $"[FAF] Friend owned apps lookup failed {friend.SteamId}");
                                friendMinutes = null;
                            }

                            if (friendMinutes == null || friendMinutes.Count == 0)
                            {
                                // We can't compute intersection reliably.
                                plan.OwnershipUnavailable = true;
                                friendsOwnershipUnavailable++;
                            }
                            else
                            {
                                plan.Owned = new HashSet<int>(friendMinutes.Keys);

                                // Intersection = friend-owned keys (already intersected by request set),
                                // then drop 0-minute entries.
                                foreach (var kv in friendMinutes)
                                {
                                    var appId = kv.Key;
                                    var mins = kv.Value;
                                    if (appId <= 0) continue;
                                    if (mins <= 0) continue;
                                    if (!steamGamesDict.TryGetValue(appId, out _)) continue;

                                    appsSet.Add(appId);
                                }
                            }

                            candidateCount = appsSet.Count;
                        }

                        plan.CandidateGames = candidateCount;

                        if (plan.AllowUnowned)
                        {
                            // Optional: add missing apps to allow discovery scans.
                            // If we don't have owned info, this becomes "all apps".
                            var ownedSet = plan.Owned ?? new HashSet<int>();
                            foreach (var a in steamGamesDict.Keys)
                            {
                                if (!ownedSet.Contains(a))
                                    appsSet.Add(a);
                            }
                            includeUnownedCandidatesTotal += steamGamesDict.Count;
                        }

                        if (plan.Forced != null && plan.Forced.Count > 0)
                        {
                            foreach (var a in plan.Forced)
                            {
                                if (steamGamesDict.TryGetValue(a, out _))
                                    appsSet.Add(a);
                            }
                        }

                        // final filter
                        appsSet.RemoveWhere(a => a <= 0 || !steamGamesDict.ContainsKey(a));

                        plan.Apps = appsSet.Count == 0 ? new List<int>() : appsSet.ToList();

                        candidateGamesTotal += plan.CandidateGames;
                        friendPlans.Add(plan);
                    }
                }
            }

            var friendsToProcessCount = friendPlans.Count;

            // ---------
            // Plan: self (build list now for unified progress count; execution happens last)
            // ---------
            var selfApps = new List<int>();

            if (options.IncludeSelf)
            {
                EmitUpdate(onUpdate, new RebuildUpdate { Kind = RebuildUpdateKind.Stage, Stage = RebuildStage.LoadingSelfOwnedApps });

                // If quick scan: self scan ONLY for affected appIds
                var selfBaseApps = quickScan
                    ? affectedAppIds.Where(a => a > 0 && steamGamesDict.TryGetValue(a, out _)).ToList()
                    : (List<int>)null;

                Dictionary<int, int> selfMinutes = null;
                try
                {
                    _logger?.Info($"[FAF] Fetching self owned apps data steamId={_settings.SteamUserId}");
                    selfMinutes = await _steam.GetOwnedGamePlaytimesAsync(_settings.SteamUserId, cancel).ConfigureAwait(false);
                    _logger?.Info($"[FAF] Fetched self owned apps data steamId={_settings.SteamUserId}: count={(selfMinutes?.Count ?? 0)}");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger?.Debug(ex, $"[FAF] Failed to load self owned apps data steamId={_settings.SteamUserId}");
                    selfMinutes = null;
                }

                // Only Steam games in Playnite DB
                selfMinutes = SteamLibraryProvider.FilterMinutesToLibrary(selfMinutes, myLibraryAppIds);

                if (quickScan)
                {
                    // Only affected games; still drop 0-minute games if we have minutes.
                    if (selfBaseApps == null || selfBaseApps.Count == 0)
                    {
                        selfApps = new List<int>();
                    }
                    else
                    {
                        selfApps = (selfMinutes != null && selfMinutes.Count > 0)
                            ? SteamLibraryProvider.FilterToNonZeroMinutesApps(selfBaseApps, selfMinutes)
                            : selfBaseApps;
                    }
                }
                else
                {
                    if (hasExplicitApps)
                    {
                        // Explicit selection, only drop 0-minute apps if we have minutes.
                        var baseApps = new List<int>(explicitApps.Count);
                        foreach (var a in explicitApps)
                        {
                            if (a > 0 && steamGamesDict.TryGetValue(a, out _))
                                baseApps.Add(a);
                        }

                        selfApps = (selfMinutes != null && selfMinutes.Count > 0)
                            ? SteamLibraryProvider.FilterToNonZeroMinutesApps(baseApps, selfMinutes)
                            : baseApps;
                    }
                    else if (options.SelfAllLibraryApps)
                    {
                        // Full scan mode (ignores minutes filter).
                        selfApps = steamGamesDict.Keys.Where(a => a > 0).ToList();
                    }
                    else
                    {
                        // Default: nominate all self games; if minutes are available, drop 0-minute games.
                        var baseApps = steamGamesDict.Keys.Where(a => a > 0).ToList();
                        selfApps = (selfMinutes != null && selfMinutes.Count > 0)
                            ? SteamLibraryProvider.FilterToNonZeroMinutesApps(baseApps, selfMinutes)
                            : baseApps;
                    }
                }
            }

            // Unified progress count
            var overallCount = (selfApps?.Count ?? 0) + friendPlans.Sum(p => p.Apps?.Count ?? 0);
            var prog = new Progress(this, onUpdate, overallCount);

            // no parallelism here; keep simple collections
            var allNewEntries = new List<FeedEntry>(Math.Min(512, Math.Max(64, overallCount)));
            var familyShareDiscoveriesByPlayniteId = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

            // ---------
            // Execute: one friend
            // ---------
            async Task<int> ScanFriendAppsAsync(FriendPlan plan, int friendIndex)
            {
                var friend = plan.Friend;
                var totalApps = plan.Apps?.Count ?? 0;

                prog.Emit(new RebuildUpdate
                {
                    Kind = RebuildUpdateKind.FriendStarted,
                    Stage = RebuildStage.ProcessingFriends,
                    FriendSteamId = friend.SteamId,
                    FriendPersonaName = friend.PersonaName,
                    FriendIndex = friendIndex,
                    FriendCount = friendsToProcessCount,
                    CandidateGames = plan.CandidateGames,
                    FriendAppIndex = 0,
                    FriendAppCount = totalApps,
                    FriendOwnershipDataUnavailable = plan.OwnershipUnavailable
                }, force: true);

                if (totalApps == 0)
                {
                    prog.Emit(new RebuildUpdate
                    {
                        Kind = RebuildUpdateKind.FriendCompleted,
                        Stage = RebuildStage.ProcessingFriends,
                        FriendSteamId = friend.SteamId,
                        FriendPersonaName = friend.PersonaName,
                        FriendIndex = friendIndex,
                        FriendCount = friendsToProcessCount,
                        FriendNewEntries = 0,
                        FriendOwnershipDataUnavailable = plan.OwnershipUnavailable,
                        TotalNewEntriesSoFar = allNewEntries.Count,
                        TotalCandidateGamesSoFar = candidateGamesTotal,
                        TotalIncludeUnownedCandidatesSoFar = includeUnownedCandidatesTotal
                    }, force: true);
                    return 0;
                }

                var maxUnlockByAppForFriend =
                    (friendAppMaxUnlock != null && friendAppMaxUnlock.TryGetValue(friend.SteamId, out var m) && m != null)
                        ? m
                        : new Dictionary<int, DateTime>();

                var friendHasAnyCached = maxUnlockByAppForFriend.Count > 0;
                var added = 0;

                for (int i = 0; i < totalApps; i++)
                {
                    cancel.ThrowIfCancellationRequested();

                    var appId = plan.Apps[i];
                    if (!steamGamesDict.TryGetValue(appId, out var game) || game == null)
                    {
                        prog.Step();
                        continue;
                    }

                    prog.Step();
                    prog.Emit(new RebuildUpdate
                    {
                        Kind = RebuildUpdateKind.FriendProgress,
                        Stage = RebuildStage.ProcessingFriends,
                        FriendSteamId = friend.SteamId,
                        FriendPersonaName = friend.PersonaName,
                        FriendIndex = friendIndex,
                        FriendCount = friendsToProcessCount,
                        FriendAppIndex = i + 1,
                        FriendAppCount = totalApps,
                        CurrentAppId = appId,
                        CurrentGameName = game.Name
                    }, i: i, total: totalApps);

                    List<ScrapedAchievementRow> rows = null;
                    try
                    {
                        rows = await _steam.GetScrapedAchievementsAsync(friend.SteamId, appId, cancel).ConfigureAwait(false);
                        _logger?.Info($"[FAF] Fetched friend achievement rows friend={friend.SteamId} appId={appId} rows={(rows?.Count ?? 0)}");
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        _logger?.Debug(ex, $"[FAF] Friend achievement fetch failed friend={friend.SteamId} appId={appId}.");
                        rows = null;
                    }

                    if (rows == null || rows.Count == 0)
                        continue;

                    // We got achievement rows at least once (so self filtering is meaningful).
                    _logger?.Info($"[FAF] Processing friend achievement rows friend={friend.SteamId} appId={appId} rows={rows.Count}");
                    anyFriendAchievementRowsFetched = true;

                    var analysis = AnalyzeFriendRowsForApp(
                        friendSteamId: friend.SteamId,
                        appId: appId,
                        friendRows: rows,
                        friendHasAnyCached: friendHasAnyCached,
                        maxUnlockByAppForFriend: maxUnlockByAppForFriend,
                        existingIds: existingIds);

                    if (analysis.SawAnyUnlocked)
                    {
                        // Record that at least one friend has unlocked achievements in this game.
                        appsWithFriendUnlockedAchievements.Add(appId);
                    }

                    if (analysis.NewCandidates.Count > 0)
                    {
                        foreach (var c in analysis.NewCandidates)
                        {
                            var e = _entryFactory.CreateCachedFriendEntry(friend, game, appId, c.Row, c.UnlockUtc, c.Row.IconUrl);
                            _logger?.Debug($"[FAF] New friend achievement entry created: friend={friend.SteamId} appId={appId} achievementKey={c.Row.Key} unlockUtc={c.UnlockUtc:u} entryId={e?.Id}");
                            if (e != null)
                            {
                                allNewEntries.Add(e);
                                added++;
                            }
                        }
                    }

                    // Discovery rule:
                    // Only run discovery when we have a meaningful ownership signal for this friend.
                    var hasOwnershipSignal = plan.Owned != null && plan.Owned.Count > 0;
                    if (!hasOwnershipSignal)
                        continue;

                    var isActuallyUnowned = !plan.Owned.Contains(appId);
                    var unownedScanAllowed = plan.UnownedScanAllowedForApp(appId);
                    var discoveryAllowed = plan.ExplicitApps ? options.ExplicitAppsAllowUnownedDiscovery : unownedScanAllowed;

                    if (discoveryAllowed && unownedScanAllowed && isActuallyUnowned && analysis.SawAnyUnlocked)
                    {
                        var pid = game.Id.ToString();
                        if (!familyShareDiscoveriesByPlayniteId.TryGetValue(pid, out var set))
                        {
                            set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            familyShareDiscoveriesByPlayniteId[pid] = set;
                        }
                        set.Add(friend.SteamId);
                    }
                }

                prog.Emit(new RebuildUpdate
                {
                    Kind = RebuildUpdateKind.FriendCompleted,
                    Stage = RebuildStage.ProcessingFriends,
                    FriendSteamId = friend.SteamId,
                    FriendPersonaName = friend.PersonaName,
                    FriendIndex = friendIndex,
                    FriendCount = friendsToProcessCount,
                    FriendNewEntries = added,
                    FriendOwnershipDataUnavailable = plan.OwnershipUnavailable,
                    TotalNewEntriesSoFar = allNewEntries.Count,
                    TotalCandidateGamesSoFar = candidateGamesTotal,
                    TotalIncludeUnownedCandidatesSoFar = includeUnownedCandidatesTotal
                }, force: true);

                return added;
            }

            // ---------
            // Execute: self (LAST) with filter
            // ---------
            async Task RefreshSelfAsync(HashSet<int> appsWhereFriendsUnlocked, bool applyFriendAchievementFilter)
            {
                int saved = 0, usedExisting = 0, statsUnavailable = 0, transient = 0, emptyRows = 0, emptyData = 0, errors = 0, noSteamUser = 0;
                int skippedNoFriendAchievements = 0;

                prog.Emit(new RebuildUpdate
                {
                    Kind = RebuildUpdateKind.SelfStarted,
                    Stage = RebuildStage.RefreshingSelfAchievements,
                    SelfAppIndex = 0,
                    SelfAppCount = selfApps?.Count ?? 0
                }, force: true);

                var total = selfApps?.Count ?? 0;
                for (int i = 0; i < total; i++)
                {
                    cancel.ThrowIfCancellationRequested();

                    var appId = selfApps[i];
                    steamGamesDict.TryGetValue(appId, out var g);
                    var pid = g?.Id.ToString();

                    prog.Step();
                    prog.Emit(new RebuildUpdate
                    {
                        Kind = RebuildUpdateKind.SelfProgress,
                        Stage = RebuildStage.RefreshingSelfAchievements,
                        SelfAppIndex = i + 1,
                        SelfAppCount = total,
                        CurrentAppId = appId,
                        CurrentGameName = g?.Name
                    }, i: i, total: total);

                    if (g == null || string.IsNullOrWhiteSpace(pid))
                        continue;

                    // Skip self scan if none of your friends have unlocked achievements in this game.
                    if (applyFriendAchievementFilter && (appsWhereFriendsUnlocked == null || !appsWhereFriendsUnlocked.Contains(appId)))
                    {
                        skippedNoFriendAchievements++;
                        continue;
                    }

                    try
                    {
                        var selfData = await _selfAchievementManager.EnsureSelfAchievementDataAsync(pid, appId, cancel, forceRefresh: true).ConfigureAwait(false);
                        // For now, just count as saved if we got non-empty data
                        if (selfData != null && selfData.LastUpdatedUtc != default(DateTime))
                        {
                            if (selfData.NoAchievements)
                                statsUnavailable++;
                            else if ((selfData.UnlockTimesUtc?.Count ?? 0) > 0 || (selfData.SelfIconUrls?.Count ?? 0) > 0)
                                saved++;
                            else
                                usedExisting++;
                        }
                        else
                        {
                            emptyData++;
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        _logger?.Debug(ex, $"[FAF] Self achievement refresh failed (appId={appId}).");
                        errors++;
                    }
                }

                prog.Emit(new RebuildUpdate
                {
                    Kind = RebuildUpdateKind.SelfCompleted,
                    Stage = RebuildStage.RefreshingSelfAchievements,
                    SelfAppIndex = total,
                    SelfAppCount = total
                }, force: true);

                _logger?.Info(
                    $"[FAF] Self scan summary: total={total}, skippedNoFriendAchievements={skippedNoFriendAchievements}, " +
                    $"saved={saved}, usedExisting={usedExisting}, statsUnavailable={statsUnavailable}, transient={transient}, " +
                    $"emptyRows={emptyRows}, emptyData={emptyData}, noSteamUser={noSteamUser}, errors={errors}");
            }

            // ---------
            // Run stages (FRIENDS FIRST, SELF LAST)
            // ---------
            EmitUpdate(onUpdate, new RebuildUpdate { Kind = RebuildUpdateKind.Stage, Stage = RebuildStage.ProcessingFriends });

            for (int i = 0; i < friendPlans.Count; i++)
                await ScanFriendAppsAsync(friendPlans[i], i + 1).ConfigureAwait(false);

            if (options.IncludeSelf)
            {
                // Apply filter only if we actually got friend achievement rows (otherwise we don't know).
                var applyFilter = friendsToProcessCount > 0 && anyFriendAchievementRowsFetched;
                var friendUnlockedApps = appsWithFriendUnlockedAchievements; // already a HashSet<int>

                EmitUpdate(onUpdate, new RebuildUpdate { Kind = RebuildUpdateKind.Stage, Stage = RebuildStage.RefreshingSelfAchievements });
                await RefreshSelfAsync(friendUnlockedApps, applyFilter).ConfigureAwait(false);
            }

            // ---------
            // Finalize + persist
            // ---------
            var dedup = new Dictionary<string, FeedEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in allNewEntries)
            {
                if (e == null) continue;
                if (string.IsNullOrWhiteSpace(e.Id)) continue;
                if (!dedup.ContainsKey(e.Id))
                    dedup[e.Id] = e;
            }

            var newEntries = dedup.Values
                .OrderByDescending(e => e.FriendUnlockTimeUtc)
                .ToList();

            _cacheService.MergeUpdateFriendFeed(newEntries);

            payload.NewEntries = newEntries;

            payload.Summary = new RebuildSummary
            {
                NewEntriesCount = newEntries.Count,
                CandidateGamesTotal = candidateGamesTotal,
                IncludeUnownedCandidatesTotal = includeUnownedCandidatesTotal,
                NoCandidatesDetected = overallCount == 0,
                FriendsOwnershipDataUnavailable = friendsOwnershipUnavailable
            };

            try
            {
                if (familyShareDiscoveriesByPlayniteId.Count > 0)
                {
                    _cacheService.MergeAndSaveFamilySharingScanResults(
                        familyShareDiscoveriesByPlayniteId.ToDictionary(k => k.Key, v => (IEnumerable<string>)v.Value));
                }
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Failed to persist scan artifacts.");
            }

            prog.Emit(new RebuildUpdate
            {
                Kind = RebuildUpdateKind.Completed,
                Stage = RebuildStage.Completed,
                TotalNewEntriesSoFar = payload.Summary.NewEntriesCount,
                TotalCandidateGamesSoFar = payload.Summary.CandidateGamesTotal,
                TotalIncludeUnownedCandidatesSoFar = payload.Summary.IncludeUnownedCandidatesTotal
            }, force: true);

            sw.Stop();
            _logger?.Info($"[FAF] Scan finished. new={payload.Summary.NewEntriesCount}, candidates={payload.Summary.CandidateGamesTotal}, elapsedMs={sw.ElapsedMilliseconds}");
            return payload;
        }
    }
}
