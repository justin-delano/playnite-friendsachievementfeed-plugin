using System;
using System.Collections.Generic;
using FriendsAchievementFeed.Models;

namespace FriendsAchievementFeed.Services
{
    public interface ICacheService
    {
        event EventHandler CacheChanged;

        void EnsureDiskCacheOrClearMemory();
        bool CacheFileExists();
        bool IsCacheValid();

        DateTime? GetFriendFeedLastUpdatedUtc();
        List<FeedEntry> GetCachedFriendEntries();
        List<FeedEntry> GetRecentFriendEntries(int count = 50);

        void UpdateFriendFeed(List<FeedEntry> entries);
        void MergeUpdateFriendFeed(List<FeedEntry> newEntries);

        // Family sharing cache:
        // key = PlayniteGameId (Guid.ToString()), value = SteamID64 list
        Dictionary<string, List<string>> LoadAllFamilySharingScanResults();
        void MergeAndSaveFamilySharingScanResults(Dictionary<string, IEnumerable<string>> results);

        SelfAchievementGameData LoadSelfAchievementData(string key);
        void SaveSelfAchievementData(string key, SelfAchievementGameData data);

        void ClearCache();
    }
}
