using FriendsAchievementFeed.Models;
using Playnite.SDK.Models;
using System;

namespace FriendsAchievementFeed.Services
{
    internal sealed class FeedEntryFactory
    {
        public static DateTime AsUtcKind(DateTime dt)
        {
            if (dt.Kind == DateTimeKind.Utc) return dt;
            if (dt.Kind == DateTimeKind.Local) return dt.ToUniversalTime();
            return DateTime.SpecifyKind(dt, DateTimeKind.Utc);
        }

        public static DateTime? AsUtcKind(DateTime? dt)
        {
            if (!dt.HasValue) return null;
            return AsUtcKind(dt.Value);
        }

        public FeedEntry CreateRaw(
            SteamFriend friend,
            Game game,
            int appId,
            ScrapedAchievementRow row,
            DateTime unlockUtc)
        {
            unlockUtc = AsUtcKind(unlockUtc);

            // Raw: friend data only. No UI decoration here.
            return new FeedEntry
            {
                Id = friend.SteamId + ":" + appId + ":" + row.Key + ":" + unlockUtc.Ticks,
                FriendSteamId = friend.SteamId,
                FriendPersonaName = friend.PersonaName,
                FriendAvatarUrl = friend.AvatarMediumUrl,
                GameName = game.Name,
                PlayniteGameId = game.Id,
                AppId = appId,

                AchievementApiName = row.Key,
                AchievementDisplayName = string.IsNullOrWhiteSpace(row.DisplayName) ? row.Key : row.DisplayName,
                AchievementDescription = row.Description ?? string.Empty,

                AchievementIconUrl = row.IconUrl,              // friend icon (unlocked icon)
                AchievementIconUnlockedUrl = row.IconUrl,      // same base
                UnlockTime = unlockUtc,

                // Visual fields stay neutral; UI will decorate.
                HideDescription = false,
                MyUnlockTime = null
            };
        }
    }
}
