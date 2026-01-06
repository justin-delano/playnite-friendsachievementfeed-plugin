using Common;
using FriendsAchievementFeed.Models;
using FriendsAchievementFeed.Services.Steam.Models;
using Playnite.SDK.Models;
using System;

namespace FriendsAchievementFeed.Services
{
    internal sealed class FeedEntryFactory
    {

        // friend-only persisted entry
        public FeedEntry CreateCachedFriendEntry(
            FriendsAchievementFeed.Models.SteamFriend friend,
            Game game,
            int appId,
            ScrapedAchievementRow row,
            DateTime friendUnlockUtc,
            string friendAchievementIconUrl)
        {
            var apiName = row?.Key ?? string.Empty;

            return new FeedEntry
            {
                Id = friend.SteamId + ":" + appId + ":" + apiName + ":" + friendUnlockUtc.Ticks,

                FriendSteamId = friend.SteamId,
                FriendPersonaName = friend.PersonaName,
                FriendAvatarUrl = friend.AvatarMediumUrl,

                GameName = game?.Name,
                PlayniteGameId = game?.Id,
                AppId = appId,

                AchievementApiName = apiName,
                AchievementDisplayName = string.IsNullOrWhiteSpace(row?.DisplayName) ? apiName : row.DisplayName,
                AchievementDescription = row?.Description ?? string.Empty,

                FriendUnlockTimeUtc = friendUnlockUtc,
                FriendAchievementIcon = friendAchievementIconUrl
            };
        }

        // hydrate UI FeedEntry by overlaying self data (not persisted)
        public FeedEntry HydrateUiEntry(FeedEntry e, SelfAchievementGameData self)
        {
            if (e == null) return null;

            DateTime? selfUnlock = null;
            string selfIcon = null;

            var key = e.AchievementApiName;

            if (!string.IsNullOrWhiteSpace(key) && self != null)
            {
                if (self.UnlockTimesUtc != null && self.UnlockTimesUtc.TryGetValue(key, out var t))
                    selfUnlock = t;

                if (self.SelfIconUrls != null && self.SelfIconUrls.TryGetValue(key, out var url))
                    selfIcon = url;
            }

            // Overlay self data onto a shallow clone to avoid mutating cached instance in memory
            var ui = new FeedEntry
            {
                Id = e.Id,

                FriendSteamId = e.FriendSteamId,
                FriendPersonaName = e.FriendPersonaName,
                FriendAvatarUrl = e.FriendAvatarUrl,

                GameName = e.GameName,
                PlayniteGameId = e.PlayniteGameId,
                AppId = e.AppId,

                AchievementApiName = e.AchievementApiName,
                AchievementDisplayName = e.AchievementDisplayName,
                AchievementDescription = e.AchievementDescription,

                FriendUnlockTime = e.FriendUnlockTimeUtc,
                FriendAchievementIcon = e.FriendAchievementIcon,

                // self overlay only at UI time
                SelfUnlockTime = selfUnlock,
                SelfAchievementIcon = selfIcon
            };

            return ui;
        }
    }
}
