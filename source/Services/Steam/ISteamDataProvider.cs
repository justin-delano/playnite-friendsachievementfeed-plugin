using FriendsAchievementFeed.Models;
using Playnite.SDK.Models;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace FriendsAchievementFeed.Services
{
    public interface ISteamDataProvider
    {
        // Achievements (HTML scrape)
        Task<List<ScrapedAchievementRow>> GetScrapedAchievementsAsync(
            string steamId64,
            int appId,
            CancellationToken cancel);

        Task<AchievementsHealthResult> GetScrapedAchievementsWithHealthAsync(
            string steamId64,
            int appId,
            CancellationToken cancel,
            bool includeLocked = false);

        // Auth check (HTML profile check)
        Task<(bool Success, string Message)> TestSteamAuthAsync(string steamUserId);

        // Friends (prefer API) - keep apiKey optional if you want to allow settings fallback
        Task<List<SteamFriend>> GetFriendsAsync(
            string steamId,
            string apiKey,
            CancellationToken cancel);

        // Owned games playtimes (Web API)
        Task<Dictionary<int, int>> GetOwnedGamePlaytimesAsync(
            string steamId,
            CancellationToken cancel);

        Task<Dictionary<int, int>> GetPlaytimesForAppsAsync(
            string friendSteamId,
            ISet<int> appIds,
            CancellationToken cancel);

        // Playnite mapping
        bool TryGetSteamAppId(Game game, out int appId);
    }
}
