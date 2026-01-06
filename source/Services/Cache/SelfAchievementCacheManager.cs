using Common;
using FriendsAchievementFeed.Models;
using FriendsAchievementFeed.Services.Steam.Models;
using Playnite.SDK;
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace FriendsAchievementFeed.Services
{
    /// <summary>
    /// Manages self (local user) achievement data caching and fetching.
    /// Extracted from CacheRebuildService for better separation of concerns.
    /// </summary>
    internal sealed class SelfAchievementCacheManager
    {
        private readonly ConcurrentDictionary<string, Task<SelfAchievementGameData>> _selfAchTasks =
            new ConcurrentDictionary<string, Task<SelfAchievementGameData>>(StringComparer.OrdinalIgnoreCase);

        private readonly ISteamDataProvider _steam;
        private readonly ICacheManager _cacheService;
        private readonly FriendsAchievementFeedSettings _settings;
        private readonly ILogger _logger;

        public SelfAchievementCacheManager(
            ISteamDataProvider steam,
            ICacheManager CacheManager,
            FriendsAchievementFeedSettings settings,
            ILogger logger)
        {
            _steam = steam ?? throw new ArgumentNullException(nameof(steam));
            _cacheService = CacheManager ?? throw new ArgumentNullException(nameof(CacheManager));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _logger = logger;
        }

        private string SelfKey(string playniteGameId, int appId) => playniteGameId + ":" + appId;

        public Task<SelfAchievementGameData> EnsureSelfAchievementDataAsync(string playniteGameId, int appId, CancellationToken cancel)
            => EnsureSelfAchievementDataAsync(playniteGameId, appId, cancel, forceRefresh: false);

        public async Task<SelfAchievementGameData> EnsureSelfAchievementDataAsync(
            string playniteGameId,
            int appId,
            CancellationToken cancel,
            bool forceRefresh)
        {
            if (appId <= 0)
                return new SelfAchievementGameData();

            if (string.IsNullOrWhiteSpace(playniteGameId))
                return new SelfAchievementGameData();

            if (!forceRefresh)
            {
                var diskOrMem = _cacheService.LoadSelfAchievementData(playniteGameId);
                if (diskOrMem != null)
                    return diskOrMem;
            }

            var key = SelfKey(playniteGameId, appId);

            var task = _selfAchTasks.GetOrAdd(key, _ => FetchAndStoreSelfAsync(playniteGameId, appId, cancel));
            try
            {
                return await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _selfAchTasks.TryRemove(key, out _);
                throw;
            }
            catch (Exception ex)
            {
                _selfAchTasks.TryRemove(key, out _);
                _logger?.Debug(ex, $"[FAF] Self achievement fetch failed (appId={appId}).");
                return new SelfAchievementGameData();
            }
        }

        private enum SelfFetchOutcome
        {
            Saved,
            UsedExisting,
            StatsUnavailable,
            TransientFailure,
            EmptyRows,
            EmptyData,
            NoSteamUser,
            Error
        }

        private async Task<(SelfAchievementGameData Data, SelfFetchOutcome Outcome)> FetchSelfInternalAsync(
            string playniteGameId,
            int appId,
            CancellationToken cancel)
        {
            if (appId <= 0 || string.IsNullOrWhiteSpace(playniteGameId))
                return (new SelfAchievementGameData(), SelfFetchOutcome.Error);

            var steamUserId = _settings?.SteamUserId;
            if (!InputValidator.HasSteamCredentials(steamUserId, _settings?.SteamApiKey))
                return (new SelfAchievementGameData(), SelfFetchOutcome.NoSteamUser);

            var existing = _cacheService.LoadSelfAchievementData(playniteGameId);
            if (existing?.NoAchievements == true)
                return (existing, SelfFetchOutcome.UsedExisting);

            AchievementsHealthResult health = null;
            try
            {
                health = await _steam
                    .GetScrapedAchievementsWithHealthAsync(steamUserId, appId, cancel, includeLocked: true)
                    .ConfigureAwait(false);
                _logger?.Debug($"[FAF] Fetched self achievements (appId={appId}): " +
                    $"TransientFailure={health?.TransientFailure}, StatsUnavailable={health?.StatsUnavailable}, Rows={health?.Rows?.Count ?? 0}, Detail={health?.Detail}");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"[FAF] Self achievements health fetch threw (appId={appId}).");
                return (existing ?? new SelfAchievementGameData(), SelfFetchOutcome.Error);
            }

            if (health == null || health.TransientFailure)
                return (existing ?? new SelfAchievementGameData(), SelfFetchOutcome.TransientFailure);

            if (health.StatsUnavailable)
            {
                if (string.Equals(health.Detail, "no_achievements", StringComparison.OrdinalIgnoreCase))
                {
                    var marker = existing ?? new SelfAchievementGameData();
                    marker.LastUpdatedUtc = DateTime.UtcNow;
                    marker.NoAchievements = true;
                    marker.UnlockTimesUtc?.Clear();
                    marker.SelfIconUrls?.Clear();

                    try
                    {
                        _cacheService.SaveSelfAchievementData(playniteGameId, marker);
                        return (marker, SelfFetchOutcome.Saved);
                    }
                    catch (Exception ex)
                    {
                        _logger?.Debug(ex, $"[FAF] Failed to save NoAchievements marker (playniteGameId={playniteGameId}, appId={appId}).");
                        return (marker, SelfFetchOutcome.Error);
                    }
                }

                return (existing ?? new SelfAchievementGameData(), SelfFetchOutcome.StatsUnavailable);
            }

            if (health.Detail == "all_hidden")
            {
                var empty = existing ?? new SelfAchievementGameData();
                empty.LastUpdatedUtc = DateTime.UtcNow;
                empty.NoAchievements = false;
                empty.UnlockTimesUtc?.Clear();
                empty.SelfIconUrls?.Clear();
                try
                {
                    _cacheService.SaveSelfAchievementData(playniteGameId, empty);
                    return (empty, SelfFetchOutcome.Saved);
                }
                catch (Exception ex)
                {
                    _logger?.Debug(ex, $"[FAF] Failed to save AllHidden data (playniteGameId={playniteGameId}, appId={appId}).");
                    return (empty, SelfFetchOutcome.Error);
                }
            }

            if (health.Rows == null || health.Rows.Count == 0)
                return (existing ?? new SelfAchievementGameData(), SelfFetchOutcome.EmptyRows);

            var data = new SelfAchievementGameData { LastUpdatedUtc = DateTime.UtcNow, NoAchievements = false };

            foreach (var r in health.Rows)
            {
                if (r == null || string.IsNullOrWhiteSpace(r.Key))
                    continue;

                var unlockUtc = DateTimeUtilities.AsUtcKind(r.UnlockTimeUtc);
                if (unlockUtc.HasValue)
                    data.UnlockTimesUtc[r.Key] = unlockUtc.Value;

                if (!string.IsNullOrWhiteSpace(r.IconUrl))
                    data.SelfIconUrls[r.Key] = r.IconUrl;
            }

            // Do NOT persist empty caches
            if ((data.UnlockTimesUtc?.Count ?? 0) == 0 && (data.SelfIconUrls?.Count ?? 0) == 0)
            {
                if (existing != null && existing.LastUpdatedUtc != default(DateTime))
                    return (existing, SelfFetchOutcome.UsedExisting);

                return (new SelfAchievementGameData(), SelfFetchOutcome.EmptyData);
            }

            try
            {
                _cacheService.SaveSelfAchievementData(playniteGameId, data);
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"[FAF] Failed to save self achievement cache (playniteGameId={playniteGameId}, appId={appId}).");
                return (data, SelfFetchOutcome.Error);
            }

            return (data, SelfFetchOutcome.Saved);
        }

        private async Task<SelfAchievementGameData> FetchAndStoreSelfAsync(string playniteGameId, int appId, CancellationToken cancel)
        {
            var key = SelfKey(playniteGameId, appId);
            try
            {
                var (data, _) = await FetchSelfInternalAsync(playniteGameId, appId, cancel).ConfigureAwait(false);
                return data ?? new SelfAchievementGameData();
            }
            finally
            {
                _selfAchTasks.TryRemove(key, out _);
            }
        }
    }
}
