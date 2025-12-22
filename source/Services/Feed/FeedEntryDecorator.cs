using FriendsAchievementFeed.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FriendsAchievementFeed.Services
{
    internal sealed class FeedEntryDecorator
    {
        private readonly FriendsAchievementFeedSettings _settings;
        private readonly SteamDataProvider _steam;

        public FeedEntryDecorator(FriendsAchievementFeedSettings settings, SteamDataProvider steam)
        {
            _settings = settings;
            _steam = steam;
        }

        public async Task<List<FeedEntry>> DecorateForViewAsync(
            IEnumerable<FeedEntry> friendEntries,
            string mySteamId64,
            CancellationToken cancel)
        {
            var list = friendEntries?.ToList() ?? new List<FeedEntry>();
            if (list.Count == 0 || string.IsNullOrWhiteSpace(mySteamId64))
            {
                return list;
            }

            // Ensure self data exists for the appIds in the view slice.
            var appIds = list.Select(e => e.AppId).Distinct().ToList();
            foreach (var appId in appIds)
            {
                cancel.ThrowIfCancellationRequested();
                await _steam.EnsureSelfAchievementDataAsync(mySteamId64, appId, cancel).ConfigureAwait(false);
            }

            var decorated = new List<FeedEntry>(list.Count);
            foreach (var e in list)
            {
                decorated.Add(DecorateOne(e, mySteamId64));
            }
            return decorated;
        }

        private FeedEntry DecorateOne(FeedEntry raw, string mySteamId64)
        {
            // Clone so cache data stays raw/unmodified on disk.
            var e = Clone(raw);

            // Reset visuals to a consistent baseline (so toggling settings works)
            if (!string.IsNullOrWhiteSpace(e.AchievementIconUnlockedUrl))
            {
                e.AchievementIconUrl = e.AchievementIconUnlockedUrl;
            }
            e.HideDescription = false;
            e.MyUnlockTime = null;

            if (!_steam.TryGetSelfAchievementData(mySteamId64, e.AppId, out var self) || self == null)
            {
                return e;
            }

            var key = e.AchievementApiName ?? string.Empty;

            var myUnlocked = self.UnlockTimesUtc.TryGetValue(key, out var myUtc) && myUtc.HasValue;

            if (_settings.HideAchievementsLockedForYou && !myUnlocked)
            {
                if (self.LockedIconUrls.TryGetValue(key, out var lockedUrl) && !string.IsNullOrWhiteSpace(lockedUrl))
                {
                    e.AchievementIconUrl = lockedUrl;
                }
                e.HideDescription = true;
            }

            if (_settings.IncludeMyUnlockTime && myUnlocked)
            {
                e.MyUnlockTime = FeedEntryFactory.AsUtcKind(myUtc.Value);
            }

            return e;
        }

        private static FeedEntry Clone(FeedEntry src)
        {
            // Copy all relevant fields used by UI; add more if you rely on them elsewhere.
            return new FeedEntry
            {
                Id = src.Id,
                FriendSteamId = src.FriendSteamId,
                FriendPersonaName = src.FriendPersonaName,
                FriendAvatarUrl = src.FriendAvatarUrl,
                GameName = src.GameName,
                PlayniteGameId = src.PlayniteGameId,
                AppId = src.AppId,

                AchievementApiName = src.AchievementApiName,
                AchievementDisplayName = src.AchievementDisplayName,
                AchievementDescription = src.AchievementDescription,

                AchievementIconUrl = src.AchievementIconUrl,
                AchievementIconUnlockedUrl = src.AchievementIconUnlockedUrl,

                UnlockTime = src.UnlockTime,

                HideDescription = src.HideDescription,
                MyUnlockTime = src.MyUnlockTime
            };
        }
    }
}
