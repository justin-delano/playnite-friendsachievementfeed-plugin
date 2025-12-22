using FriendsAchievementFeed.Models;
using Playnite.SDK;
using Playnite.SDK.Models;
using Common;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SteamFriendModel = FriendsAchievementFeed.Models.SteamFriend;

namespace FriendsAchievementFeed.Services
{
    internal sealed class LiveFeedBuilder
    {
        private readonly SteamDataProvider _steam;
        private readonly FeedEntryFactory _entryFactory;
        private readonly FriendsAchievementFeedSettings _settings;
        private readonly ILogger _logger;

        public LiveFeedBuilder(
            SteamDataProvider steam,
            FeedEntryFactory entryFactory,
            FriendsAchievementFeedSettings settings,
            ILogger logger)
        {
            _steam = steam;
            _entryFactory = entryFactory;
            _settings = settings;
            _logger = logger;
        }

        public async Task<List<FeedEntry>> BuildLiveFeedForGamesAsync(
            IEnumerable<Game> games,
            Action<ProgressReport> progress,
            CancellationToken cancel)
        {
            var gamesList = games != null ? games.ToList() : new List<Game>();
            if (gamesList.Count == 0 || string.IsNullOrWhiteSpace(_settings.SteamUserId))
            {
                return new List<FeedEntry>();
            }

            try
            {
                progress?.Invoke(new ProgressReport
                {
                    Message = ResourceProvider.GetString("LOCFriendsAchFeed_Targeted_LoadingOwnedGames"),
                    CurrentStep = 0,
                    TotalSteps = 1
                });
            }
            catch { }

            var yourOwnedGames = await _steam.GetOwnedGameIdsAsync(_settings.SteamUserId).ConfigureAwait(false);

            var appIdToGame = new Dictionary<int, Game>();
            foreach (var g in gamesList)
            {
                if (!_steam.TryGetSteamAppId(g, out var appId) || appId == 0)
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

            try
            {
                progress?.Invoke(new ProgressReport
                {
                    Message = string.Format(ResourceProvider.GetString("LOCFriendsAchFeed_Rebuild_FoundOwnedGames_LoadingFriends"), appIdToGame.Count),
                    CurrentStep = 0,
                    TotalSteps = 1
                });
            }
            catch { }

            if (appIdToGame.Count == 0)
            {
                return new List<FeedEntry>();
            }

            var allFriends = await _steam.GetFriendsAsync(_settings.SteamUserId).ConfigureAwait(false);

            try
            {
                progress?.Invoke(new ProgressReport
                {
                    Message = string.Format(ResourceProvider.GetString("LOCFriendsAchFeed_Rebuild_FoundFriends_LoadingAchievements"), allFriends.Count),
                    CurrentStep = 0,
                    TotalSteps = Math.Max(1, allFriends.Count)
                });
            }
            catch { }

            // Parallel fetching setup
            var allEntries = new ConcurrentBag<FeedEntry>();
            using (var semaphore = new SemaphoreSlim(8)) // limit concurrency
            {
                var tasks = new List<Task>();

                foreach (var friend in allFriends)
                {
                    tasks.Add(ProcessFriendAsync(friend, appIdToGame, semaphore, allEntries, cancel));
                }

                await Task.WhenAll(tasks).ConfigureAwait(false);
            }

            return allEntries.ToList();
        }

        private async Task ProcessFriendAsync(
            SteamFriendModel friend,
            Dictionary<int, Game> appIdToGame,
            SemaphoreSlim semaphore,
            ConcurrentBag<FeedEntry> results,
            CancellationToken cancel)
        {
            try
            {
                // Throttle the initial check slightly if needed, but GetOwnedGameIdsAsync is cached
                await semaphore.WaitAsync(cancel).ConfigureAwait(false);
                try
                {
                    var friendOwned = await _steam.GetOwnedGameIdsAsync(friend.SteamId).ConfigureAwait(false);
                    if (friendOwned == null || friendOwned.Count == 0)
                    {
                        return;
                    }

                    // We can process games for this friend in parallel or sequence? 
                    // Let's do sequence per friend to keep complexity down, but friends run in parallel.
                    // Or better: Release semaphore while processing games? No, we want to limit total network calls.
                    
                    foreach (var kv in appIdToGame)
                    {
                        cancel.ThrowIfCancellationRequested();

                        var appId = kv.Key;
                        var game = kv.Value;

                        if (!_settings.SearchAllMyGames && !friendOwned.Contains(appId))
                        {
                            continue;
                        }

                        // Fetch achievements
                        var friendRows = await _steam.GetScrapedAchievementsAsync(friend.SteamId, appId, cancel).ConfigureAwait(false);
                        if (friendRows == null || friendRows.Count == 0)
                        {
                            continue;
                        }

                        // Prefetch self data (fire and forget / independent await)
                        // We await it here so we don't return before it's ready if we needed it?
                        // The original code awaited it.
                        try
                        {
                            await _steam.EnsureSelfAchievementDataAsync(_settings.SteamUserId, appId, cancel).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            _logger?.Debug($"[SelfAchievements] EnsureSelfAchievementDataAsync failed for appId={appId}: {ex.Message}");
                        }

                        foreach (var row in friendRows)
                        {
                            if (row == null || string.IsNullOrWhiteSpace(row.Key)) continue;

                            var unlockUtc = FeedEntryFactory.AsUtcKind(row.UnlockTimeUtc);
                            if (!unlockUtc.HasValue) continue;

                            results.Add(_entryFactory.CreateRaw(friend, game, appId, row, unlockUtc.Value));
                        }
                    }
                }
                finally
                {
                    semaphore.Release();
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Error processing friend {friend.SteamId}");
            }
        }
    }
}