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

        public Task<SelfAchievementGameData> EnsureSelfAchievementDataAsync(string playniteGameId, int appId, CancellationToken cancel, bool forceRefresh)
            => _selfAchievementManager.EnsureSelfAchievementDataAsync(playniteGameId, appId, cancel, forceRefresh);

        private void EmitUpdate(Action<RebuildUpdate> onUpdate, RebuildUpdate update)
        {
            if (onUpdate == null || update == null) return;
            try { onUpdate(update); }
            catch (Exception ex) { _logger?.Debug(ex, "[FAF] Rebuild progress handler threw."); }
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
            private int _currentCounter;

            public int OverallCount { get; }

            public Progress(CacheRebuilder svc, Action<RebuildUpdate> cb, int overallCount, int emitEvery = 5, int minMs = 16)
            {
                _svc = svc; _cb = cb;
                OverallCount = Math.Max(0, overallCount);
                _emitEvery = Math.Max(1, emitEvery);
                _min = TimeSpan.FromMilliseconds(Math.Max(0, minMs));
            }

            public void Step()
            {
                Interlocked.Increment(ref _currentCounter);
            }

            public void Emit(RebuildUpdate u, bool force = false, int? i = null, int? total = null)
            {
                if (u == null) return;

                var now = DateTime.UtcNow;
                
                // Logic to force update on first or last item of a sub-loop
                bool isBoundary = i.HasValue && total.HasValue && (i.Value == 0 || i.Value >= total.Value - 1);
                
                var ok = force || isBoundary || (now - _last) >= _min;

                if (!ok) return;

                lock (this)
                {
                    if (!force && !isBoundary && (DateTime.UtcNow - _last) < _min) return;
                    _last = DateTime.UtcNow;
                }

                u.OverallIndex = Math.Min(_currentCounter, OverallCount);
                u.OverallCount = OverallCount;
                _svc.EmitUpdate(_cb, u);
            }
        }

        // --------------------
        // Helpers
        // --------------------

        private List<int> ResolveExplicitAppIds(CacheScanOptions opt, Dictionary<int, Game> steamGamesDict)
        {
            if (opt == null || steamGamesDict == null || steamGamesDict.Count == 0)
                return null;

            var result = new HashSet<int>();

            if (opt.SteamAppIds != null)
            {
                foreach (var id in opt.SteamAppIds)
                {
                    if (id > 0 && steamGamesDict.ContainsKey(id))
                        result.Add(id);
                }
            }

            if (opt.PlayniteGameIds != null && opt.PlayniteGameIds.Count > 0)
            {
                var wantedGuids = new HashSet<Guid>(opt.PlayniteGameIds);
                foreach (var g in _api.Database.Games)
                {
                    if (wantedGuids.Contains(g.Id))
                    {
                        if (_steam.TryGetSteamAppId(g, out var appId) && appId > 0 && steamGamesDict.ContainsKey(appId))
                        {
                            result.Add(appId);
                        }
                    }
                }
            }

            return result.Count == 0 ? null : result.ToList();
        }

        private sealed class FriendRowAnalysis
        {
            public bool SawAnyUnlocked;
            public List<(ScrapedAchievementRow Row, DateTime UnlockUtc)> NewCandidates = new List<(ScrapedAchievementRow, DateTime)>();
        }

        private FriendRowAnalysis AnalyzeFriendRowsForApp(
            string friendSteamId,
            int appId,
            List<ScrapedAchievementRow> friendRows,
            bool friendHasAnyCached,
            Dictionary<int, DateTime> maxUnlockByAppForFriend,
            HashSet<string> existingIds)
        {
            var analysis = new FriendRowAnalysis();
            if (friendRows == null || friendRows.Count == 0)
                return analysis;

            var maxCachedForApp = DateTime.MinValue;
            var hasMaxForApp = false;

            if (friendHasAnyCached && maxUnlockByAppForFriend != null)
                hasMaxForApp = maxUnlockByAppForFriend.TryGetValue(appId, out maxCachedForApp);

            var entryPrefix = string.Concat(friendSteamId, ":", appId.ToString(), ":");

            foreach (var row in friendRows)
            {
                if (row == null || string.IsNullOrWhiteSpace(row.Key)) continue;

                var unlockUtc = DateTimeUtilities.AsUtcKind(row.UnlockTimeUtc);
                if (!unlockUtc.HasValue) continue;

                analysis.SawAnyUnlocked = true;

                var u = unlockUtc.Value;

                if (hasMaxForApp && u <= maxCachedForApp)
                    continue;

                var entryId = string.Concat(entryPrefix, row.Key, ":", u.Ticks.ToString());
                if (existingIds != null && existingIds.Contains(entryId))
                    continue;

                analysis.NewCandidates.Add((row, u));
            }

            return analysis;
        }

        private Dictionary<string, HashSet<int>> BuildFamilySharingForcedAppsByFriend(Dictionary<string, int> playniteIdToAppId)
        {
            var result = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
            var all = _cacheService.LoadAllFamilySharingScanResults();

            if (all == null || all.Count == 0 || playniteIdToAppId == null) return result;

            foreach (var kv in all)
            {
                if (playniteIdToAppId.TryGetValue(kv.Key, out var appId) && appId > 0)
                {
                    foreach (var steamId in kv.Value ?? Enumerable.Empty<string>())
                    {
                        if (!string.IsNullOrWhiteSpace(steamId))
                        {
                            if (!result.TryGetValue(steamId, out var set))
                            {
                                set = new HashSet<int>();
                                result[steamId] = set;
                            }
                            set.Add(appId);
                        }
                    }
                }
            }
            return result;
        }

        // --------------------
        // Planning
        // --------------------
        private struct FriendPlan
        {
            public SteamFriendModel Friend;
            public List<int> Apps;
            public HashSet<int> Owned;
            public HashSet<int> Forced;
            public bool AllowUnowned;
            public bool ExplicitApps;
            public int CandidateGames;
            public bool OwnershipUnavailable;

            public bool UnownedScanAllowedForApp(int appId) =>
                ExplicitApps || AllowUnowned || (Forced != null && Forced.Contains(appId));
        }

        // --------------------
        // MAIN SCAN
        // --------------------
        public async Task<RebuildPayload> ScanAsync(CacheScanOptions options, Action<RebuildUpdate> onUpdate, CancellationToken cancel)
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

            Dictionary<string, int> playniteIdToAppId = null;
            Dictionary<string, HashSet<int>> forcedAppsByFriend = null;

            EmitUpdate(onUpdate, new RebuildUpdate { Kind = RebuildUpdateKind.Stage, Stage = RebuildStage.LoadingFriends });

            var friendsResult = options.IncludeFriends
                ? await _steam.GetFriendsAsync(_settings.SteamUserId, _settings.SteamApiKey, cancel).ConfigureAwait(false)
                : new List<SteamFriendModel>();

            var friends = FriendScanner.FilterFriends(friendsResult, options.FriendSteamIds);

            EmitUpdate(onUpdate, new RebuildUpdate { Kind = RebuildUpdateKind.Stage, Stage = RebuildStage.LoadingExistingCache });
            var existingEntries = _cacheService.GetCachedFriendEntries() ?? new List<FeedEntry>();
            var existingIds = new HashSet<string>(existingEntries.Where(e => !string.IsNullOrWhiteSpace(e.Id)).Select(e => e.Id), StringComparer.OrdinalIgnoreCase);
            var friendAppMaxUnlock = FriendScanner.BuildFriendAppMaxUnlockMap(existingEntries);
            var includeUnownedSet = FriendScanner.ToSet(options.IncludeUnownedFriendIds);
            var explicitApps = ResolveExplicitAppIds(options, steamGamesDict);
            var hasExplicitApps = explicitApps != null && explicitApps.Count > 0;
            var quickScan = options.QuickScanRecentPairs;

            if (options.IncludeFriends && !quickScan)
            {
                playniteIdToAppId = SteamLibraryProvider.BuildPlayniteIdToAppId(steamGamesDict);
                forcedAppsByFriend = BuildFamilySharingForcedAppsByFriend(playniteIdToAppId);
            }

            var allNewEntries = new ConcurrentBag<FeedEntry>();
            var appsWithFriendUnlockedAchievements = new ConcurrentDictionary<int, byte>();
            var familyShareDiscoveries = new ConcurrentDictionary<string, ConcurrentDictionary<string, byte>>();

            // --------------------
            // 3. Build Plans
            // --------------------
            var (friendPlans, appIdsForQuickScan, candidateGamesTotal, includeUnownedCandidatesTotal, friendsOwnershipUnavailable) = await BuildFriendPlansAsync(
                friends, quickScan, options, existingEntries, steamGamesDict, myLibraryAppIds,
                hasExplicitApps, explicitApps, includeUnownedSet, forcedAppsByFriend, cancel).ConfigureAwait(false);

            // 4. Calculate Self Plan
            var selfApps = await CalculateSelfAppsPlanAsync(options, quickScan, hasExplicitApps, explicitApps, appIdsForQuickScan, steamGamesDict, myLibraryAppIds, cancel).ConfigureAwait(false);
            
            // Total steps for progress bar
            var overallCount = (selfApps?.Count ?? 0) + friendPlans.Sum(p => p.Apps?.Count ?? 0);
            var prog = new Progress(this, onUpdate, overallCount);

            // --------------------
            // 5. Execute Friend Scans
            // --------------------
            EmitUpdate(onUpdate, new RebuildUpdate { Kind = RebuildUpdateKind.Stage, Stage = RebuildStage.ProcessingFriends });

            for (int i = 0; i < friendPlans.Count; i++)
            {
                await ProcessFriendPlanAsync(friendPlans[i], i, friendPlans.Count, steamGamesDict, friendAppMaxUnlock, existingIds,
                    prog, allNewEntries, appsWithFriendUnlockedAchievements, familyShareDiscoveries, options, cancel).ConfigureAwait(false);
            }

            // --------------------
            // 6. Execute Self Scan
            // --------------------
            if (options.IncludeSelf && selfApps != null && selfApps.Count > 0)
            {
                EmitUpdate(onUpdate, new RebuildUpdate { Kind = RebuildUpdateKind.Stage, Stage = RebuildStage.RefreshingSelfAchievements });

                var unlockedSet = new HashSet<int>(appsWithFriendUnlockedAchievements.Keys);
                bool applyFilter = friendPlans.Count > 0 && !appsWithFriendUnlockedAchievements.IsEmpty;

                await ProcessSelfPlanAsync(selfApps, unlockedSet, applyFilter, steamGamesDict, prog, cancel).ConfigureAwait(false);
            }

            // --------------------
            // 7. Finalize
            // --------------------
            var deduped = allNewEntries
                .GroupBy(e => e.Id)
                .Select(g => g.First())
                .OrderByDescending(e => e.FriendUnlockTimeUtc)
                .ToList();

            _cacheService.MergeUpdateFriendFeed(deduped);
            payload.NewEntries = deduped;

            payload.Summary = new RebuildSummary
            {
                NewEntriesCount = deduped.Count,
                CandidateGamesTotal = candidateGamesTotal,
                IncludeUnownedCandidatesTotal = includeUnownedCandidatesTotal,
                NoCandidatesDetected = overallCount == 0,
                FriendsOwnershipDataUnavailable = friendsOwnershipUnavailable
            };

            if (!familyShareDiscoveries.IsEmpty)
            {
                var dict = familyShareDiscoveries.ToDictionary(
                    k => k.Key,
                    v => (IEnumerable<string>)v.Value.Keys);
                _cacheService.MergeAndSaveFamilySharingScanResults(dict);
            }

            prog.Emit(new RebuildUpdate { Kind = RebuildUpdateKind.Completed, Stage = RebuildStage.Completed }, force: true);
            sw.Stop();
            _logger?.Info($"[FAF] Scan finished. new={payload.Summary.NewEntriesCount}, elapsedMs={sw.ElapsedMilliseconds}");

            return payload;
        }

        // --------------------
        // Logic Splitting
        // --------------------

        private async Task<(List<FriendPlan> Plans, HashSet<int> AppIdsForQuickScan, int CandidateGamesTotal, int IncludeUnownedCandidatesTotal, int FriendsOwnershipUnavailable)> BuildFriendPlansAsync(
            List<SteamFriendModel> friends, bool quickScan, CacheScanOptions options, List<FeedEntry> existingEntries,
            Dictionary<int, Game> steamGamesDict, HashSet<int> myLibraryAppIds,
            bool hasExplicitApps, List<int> explicitApps, HashSet<string> includeUnownedSet,
            Dictionary<string, HashSet<int>> forcedAppsByFriend, CancellationToken cancel)
        {
            var plans = new List<FriendPlan>();
            var appIdsForQuickScan = new HashSet<int>();
            int candidateGamesTotal = 0;
            int includeUnownedCandidatesTotal = 0;
            int friendsOwnershipUnavailable = 0;

            if (friends == null) return (plans, appIdsForQuickScan, candidateGamesTotal, includeUnownedCandidatesTotal, friendsOwnershipUnavailable);

            if (quickScan)
            {
                var friendById = friends.Where(f => !string.IsNullOrWhiteSpace(f.SteamId))
                                        .ToDictionary(f => f.SteamId, f => f, StringComparer.OrdinalIgnoreCase);

                var recentAppsByFriend = new Dictionary<string, List<int>>();
                int limitFriends = Math.Max(1, options.QuickScanRecentFriendsCount);
                int limitGames = Math.Max(1, options.QuickScanRecentGamesPerFriend);

                // Pre-sort collection once to avoid redundant sorting on each iteration
                var sortedEntries = existingEntries.OrderByDescending(x => x.FriendUnlockTimeUtc).ToList();
                foreach (var e in sortedEntries)
                {
                    if (recentAppsByFriend.Count >= limitFriends && !recentAppsByFriend.ContainsKey(e.FriendSteamId)) continue;
                    if (!friendById.ContainsKey(e.FriendSteamId) || !steamGamesDict.ContainsKey(e.AppId)) continue;

                    if (!recentAppsByFriend.TryGetValue(e.FriendSteamId, out var list))
                    {
                        list = [];
                        recentAppsByFriend[e.FriendSteamId] = list;
                    }

                    if (list.Count < limitGames && !list.Contains(e.AppId))
                        list.Add(e.AppId);
                }

                foreach (var kv in recentAppsByFriend)
                {
                    var friend = friendById[kv.Key];
                    var apps = kv.Value;
                    var plan = new FriendPlan
                    {
                        Friend = friend,
                        Apps = apps,
                        Owned = [.. apps],
                        ExplicitApps = true,
                        CandidateGames = apps.Count
                    };
                    plans.Add(plan);
                    candidateGamesTotal += plan.CandidateGames;
                    foreach (var a in apps) appIdsForQuickScan.Add(a);
                }
            }
            else
            {
                foreach (var friend in friends)
                {
                    if (string.IsNullOrWhiteSpace(friend.SteamId)) continue;

                    var appsSet = new HashSet<int>();
                    var plan = new FriendPlan
                    {
                        Friend = friend,
                        Owned = new HashSet<int>(),
                        Forced = (forcedAppsByFriend != null && forcedAppsByFriend.TryGetValue(friend.SteamId, out var f)) ? f : null,
                        AllowUnowned = includeUnownedSet.Contains(friend.SteamId),
                        ExplicitApps = hasExplicitApps
                    };

                    List<int> appsToScanForMinutes;
                    if (hasExplicitApps)
                        appsToScanForMinutes = explicitApps;
                    else if (options.FriendsAllLibraryApps)
                        appsToScanForMinutes = myLibraryAppIds.ToList();
                    else
                        appsToScanForMinutes = myLibraryAppIds.ToList();

                    try
                    {
                        var minutes = await _steam.GetPlaytimesForAppsAsync(friend.SteamId, new HashSet<int>(appsToScanForMinutes), cancel).ConfigureAwait(false);
                        if (minutes != null && minutes.Count > 0)
                        {
                            plan.Owned = new HashSet<int>(minutes.Keys);
                            foreach (var kv in minutes)
                            {
                                if (kv.Value > 0 && steamGamesDict.ContainsKey(kv.Key))
                                    appsSet.Add(kv.Key);
                            }
                        }
                        else
                        {
                            plan.OwnershipUnavailable = true;
                            friendsOwnershipUnavailable++;
                        }
                    }
                    catch { /* ignore */ }

                    if (plan.OwnershipUnavailable || plan.AllowUnowned)
                    {
                        if (hasExplicitApps)
                        {
                            foreach (var a in explicitApps) if (steamGamesDict.ContainsKey(a)) appsSet.Add(a);
                        }
                    }

                    if (plan.AllowUnowned && !hasExplicitApps)
                    {
                        foreach (var a in myLibraryAppIds) appsSet.Add(a);
                        includeUnownedCandidatesTotal += myLibraryAppIds.Count;
                    }

                    if (plan.Forced != null)
                    {
                        foreach (var a in plan.Forced) if (steamGamesDict.ContainsKey(a)) appsSet.Add(a);
                    }

                    plan.Apps = appsSet.ToList();
                    plan.CandidateGames = plan.Apps.Count;
                    candidateGamesTotal += plan.CandidateGames;
                    plans.Add(plan);
                }
            }
            return (plans, appIdsForQuickScan, candidateGamesTotal, includeUnownedCandidatesTotal, friendsOwnershipUnavailable);
        }

        private async Task ProcessFriendPlanAsync(
            FriendPlan plan, int index, int totalFriends, Dictionary<int, Game> steamGamesDict,
            Dictionary<string, Dictionary<int, DateTime>> friendAppMaxUnlock, HashSet<string> existingIds,
            Progress prog, ConcurrentBag<FeedEntry> allNewEntries, ConcurrentDictionary<int, byte> appsWithFriendUnlockedAchievements,
            ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> familyShareDiscoveries,
            CacheScanOptions options, CancellationToken cancel)
        {
            var friend = plan.Friend;
            var totalApps = plan.Apps.Count;

            prog.Emit(new RebuildUpdate
            {
                Kind = RebuildUpdateKind.FriendStarted,
                FriendSteamId = friend.SteamId,
                FriendPersonaName = friend.PersonaName,
                FriendIndex = index + 1,
                FriendCount = totalFriends,
                FriendAppCount = totalApps
            }, force: true);

            var maxUnlockByApp = (friendAppMaxUnlock != null && friendAppMaxUnlock.TryGetValue(friend.SteamId, out var m)) ? m : new Dictionary<int, DateTime>();
            bool friendHasAnyCached = maxUnlockByApp.Count > 0;
            int addedCount = 0;

            for (int i = 0; i < totalApps; i++)
            {
                cancel.ThrowIfCancellationRequested();
                var appId = plan.Apps[i];
                if (!steamGamesDict.TryGetValue(appId, out var game)) continue;

                prog.Step();
                
                prog.Emit(new RebuildUpdate
                {
                    Kind = RebuildUpdateKind.FriendProgress,
                    FriendIndex = index + 1,
                    FriendCount = totalFriends,
                    FriendAppIndex = i + 1,
                    FriendAppCount = totalApps,
                    CurrentGameName = game.Name,
                    FriendSteamId = friend.SteamId,
                    FriendPersonaName = friend.PersonaName
                }, i: i, total: totalApps);

                var rows = await _steam.GetScrapedAchievementsAsync(friend.SteamId, appId, cancel).ConfigureAwait(false);
                if (rows == null || rows.Count == 0) continue;

                var analysis = AnalyzeFriendRowsForApp(friend.SteamId, appId, rows, friendHasAnyCached, maxUnlockByApp, existingIds);

                if (analysis.SawAnyUnlocked)
                    appsWithFriendUnlockedAchievements.TryAdd(appId, 1);

                foreach (var c in analysis.NewCandidates)
                {
                    var e = _entryFactory.CreateCachedFriendEntry(friend, game, appId, c.Row, c.UnlockUtc, c.Row.IconUrl);
                    if (e != null)
                    {
                        allNewEntries.Add(e);
                        addedCount++;
                    }
                }

                bool discoveryAllowed = plan.ExplicitApps ? options.ExplicitAppsAllowUnownedDiscovery : plan.UnownedScanAllowedForApp(appId);
                bool isActuallyUnowned = !plan.Owned.Contains(appId);

                if (discoveryAllowed && isActuallyUnowned && analysis.SawAnyUnlocked)
                {
                    var pid = game.Id.ToString();
                    var set = familyShareDiscoveries.GetOrAdd(pid, _ => new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase));
                    set.TryAdd(friend.SteamId, 1);
                }
            }

            prog.Emit(new RebuildUpdate
            {
                Kind = RebuildUpdateKind.FriendCompleted,
                FriendSteamId = friend.SteamId,
                FriendPersonaName = friend.PersonaName,
                FriendIndex = index + 1,
                FriendCount = totalFriends,
                FriendNewEntries = addedCount
            }, force: true);
        }

        private async Task<List<int>> CalculateSelfAppsPlanAsync(
            CacheScanOptions options, bool quickScan, bool hasExplicitApps, List<int> explicitApps,
            HashSet<int> appIdsForQuickScan, Dictionary<int, Game> steamGamesDict, HashSet<int> myLibraryAppIds,
            CancellationToken cancel)
        {
            if (quickScan) return appIdsForQuickScan.Where(a => a > 0 && steamGamesDict.ContainsKey(a)).ToList();

            var baseApps = new List<int>();
            if (hasExplicitApps) baseApps.AddRange(explicitApps.Where(a => a > 0 && steamGamesDict.ContainsKey(a)));
            else if (options.SelfAllLibraryApps) baseApps.AddRange(steamGamesDict.Keys);
            else baseApps.AddRange(steamGamesDict.Keys);

            try
            {
                var selfMinutes = await _steam.GetOwnedGamePlaytimesAsync(_settings.SteamUserId, cancel).ConfigureAwait(false);
                if (selfMinutes != null && selfMinutes.Count > 0)
                {
                    return SteamLibraryProvider.FilterToNonZeroMinutesApps(baseApps, selfMinutes);
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Failed to load self owned apps data.");
            }

            return baseApps;
        }

        private async Task ProcessSelfPlanAsync(
            List<int> selfApps, HashSet<int> unlockedSet, bool applyFilter, Dictionary<int, Game> steamGamesDict,
            Progress prog, CancellationToken cancel)
        {
            prog.Emit(new RebuildUpdate { Kind = RebuildUpdateKind.SelfStarted, SelfAppCount = selfApps.Count }, force: true);

            for (int i = 0; i < selfApps.Count; i++)
            {
                cancel.ThrowIfCancellationRequested();
                var appId = selfApps[i];
                steamGamesDict.TryGetValue(appId, out var g);
                if (g == null) continue;

                prog.Step();
                prog.Emit(new RebuildUpdate
                {
                    Kind = RebuildUpdateKind.SelfProgress,
                    SelfAppIndex = i + 1,
                    SelfAppCount = selfApps.Count,
                    CurrentGameName = g.Name
                }, i: i, total: selfApps.Count);

                if (applyFilter && !unlockedSet.Contains(appId)) continue;

                try
                {
                    await _selfAchievementManager.EnsureSelfAchievementDataAsync(g.Id.ToString(), appId, cancel, forceRefresh: true).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger?.Debug(ex, $"[FAF] Self achievement refresh failed (appId={appId}).");
                }
            }

            prog.Emit(new RebuildUpdate { Kind = RebuildUpdateKind.SelfCompleted }, force: true);
        }
    }
}