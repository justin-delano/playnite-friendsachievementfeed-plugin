namespace FriendsAchievementFeed.Services
{
    /// <summary>
    /// Centralized utility for building cache storage keys.
    /// Ensures consistent key formatting across the cache system.
    /// </summary>
    internal static class CacheKeyBuilder
    {
        /// <summary>
        /// Builds a cache key for self achievement data using the Playnite game ID.
        /// </summary>
        public static string SelfAchievements(string playniteGameId) => playniteGameId;

        /// <summary>
        /// Builds a cache key for self achievement data using the Steam app ID when Playnite ID is unavailable.
        /// </summary>
        public static string SelfAchievementsByAppId(int appId) => $"app:{appId}";
    }
}
