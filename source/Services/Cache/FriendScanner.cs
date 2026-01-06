using System;
using System.Collections.Generic;
using System.Linq;
using Common;
using FriendsAchievementFeed.Models;

namespace FriendsAchievementFeed.Services
{
    internal static class FriendScanner
    {
        public static HashSet<string> ToSet(IEnumerable<string> ids)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (ids == null) return set;

            foreach (var id in ids.Where(id => !string.IsNullOrWhiteSpace(id)))
            {
                set.Add(id.Trim());
            }
            return set;
        }

        public static List<SteamFriend> FilterFriends(List<SteamFriend> all, IReadOnlyCollection<string> ids)
        {
            all ??= new List<SteamFriend>();
            if (ids == null || ids.Count == 0) return all;

            var set = ToSet(ids);
            return all.Where(f => f != null && 
                                  !string.IsNullOrWhiteSpace(f.SteamId) && 
                                  set.Contains(f.SteamId))
                      .ToList();
        }

        public static Dictionary<string, Dictionary<int, DateTime>> BuildFriendAppMaxUnlockMap(IEnumerable<FeedEntry> existingEntries)
        {
            var result = new Dictionary<string, Dictionary<int, DateTime>>(StringComparer.OrdinalIgnoreCase);
            if (existingEntries == null) return result;

            foreach (var e in existingEntries)
            {
                if (e == null || string.IsNullOrWhiteSpace(e.FriendSteamId))
                    continue;

                if (!result.TryGetValue(e.FriendSteamId, out var appMap))
                {
                    appMap = new Dictionary<int, DateTime>();
                    result[e.FriendSteamId] = appMap;
                }

                var unlockUtc = DateTimeUtilities.AsUtcKind(e.FriendUnlockTimeUtc);

                if (!appMap.TryGetValue(e.AppId, out var existing) || unlockUtc > existing)
                    appMap[e.AppId] = unlockUtc;
            }

            return result;
        }
    }
}
