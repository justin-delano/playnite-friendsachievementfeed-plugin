using System;
using System.Collections.Generic;
using FriendsAchievementFeed.Models;

namespace FriendsAchievementFeed.Views
{
    internal sealed class FeedEntryFilter
    {
        public string FriendSearchText { get; set; } = "";
        public string GameSearchText { get; set; } = "";
        public string AchievementSearchText { get; set; } = "";

        // When no text filters are active and DefaultVisibleIds != null,
        // only entries in this set are shown.
        public HashSet<string> DefaultVisibleIds { get; set; }

        public bool Matches(FeedEntry e)
        {
            if (e == null) return false;

            var hasFriend = !string.IsNullOrWhiteSpace(FriendSearchText);
            var hasGame = !string.IsNullOrWhiteSpace(GameSearchText);
            var hasAch = !string.IsNullOrWhiteSpace(AchievementSearchText);

            var hasAnySearch = hasFriend || hasGame || hasAch;

            if (!hasAnySearch && DefaultVisibleIds != null)
            {
                if (string.IsNullOrEmpty(e.Id) || !DefaultVisibleIds.Contains(e.Id))
                {
                    return false;
                }
            }

            if (hasFriend)
            {
                if (string.IsNullOrWhiteSpace(e.FriendPersonaName) ||
                    e.FriendPersonaName.IndexOf(FriendSearchText, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    return false;
                }
            }

            if (hasGame)
            {
                if (string.IsNullOrWhiteSpace(e.GameName) ||
                    e.GameName.IndexOf(GameSearchText, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    return false;
                }
            }

            if (hasAch)
            {
                var nameMatch = !string.IsNullOrWhiteSpace(e.AchievementDisplayName) &&
                                e.AchievementDisplayName.IndexOf(AchievementSearchText, StringComparison.OrdinalIgnoreCase) >= 0;

                var descMatch = !string.IsNullOrWhiteSpace(e.AchievementDescription) &&
                                e.AchievementDescription.IndexOf(AchievementSearchText, StringComparison.OrdinalIgnoreCase) >= 0;

                if (!(nameMatch || descMatch))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
