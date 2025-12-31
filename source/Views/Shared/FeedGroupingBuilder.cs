using System;
using System.Collections.Generic;
using FriendsAchievementFeed.Models;

namespace FriendsAchievementFeed.Views
{
    internal static class FeedGroupingBuilder
    {
        public static List<FeedGroup> BuildGroups(IEnumerable<FeedEntry> entries, bool showGameInHeader, Func<DateTime, DateTime> asLocalFromUtc)
        {
            var result = new List<FeedGroup>();
            if (entries == null) return result;

            FeedGroup current = null;

            foreach (var e in entries)
            {
                if (e == null) continue;

                var local = asLocalFromUtc(e.FriendUnlockTime);
                var day = local.Date;

                if (current == null ||
                    current.FriendSteamId != e.FriendSteamId ||
                    current.Date != day ||
                    !string.Equals(current.GameName, e.GameName, StringComparison.OrdinalIgnoreCase))
                {
                    current = new FeedGroup
                    {
                        FriendSteamId = e.FriendSteamId,
                        FriendPersonaName = e.FriendPersonaName,
                        FriendAvatarUrl = e.FriendAvatarUrl,
                        Date = day,
                        GameName = e.GameName,
                        ShowGameName = showGameInHeader && !string.IsNullOrWhiteSpace(e.GameName),
                        SubheaderText = day.ToString("D")
                    };

                    result.Add(current);
                }

                current.Achievements.Add(e);
            }

            return result;
        }
    }
}
