using FriendsAchievementFeed.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FriendsAchievementFeed.Services
{
    internal sealed class FeedEntryViewComposer
    {
        private readonly SteamDataProvider _steam;
        private readonly FriendsAchievementFeedSettings _settings;

        public FeedEntryViewComposer(SteamDataProvider steam, FriendsAchievementFeedSettings settings)
        {
            _steam = steam;
            _settings = settings;
        }

        public async Task<List<FeedEntry>> ComposeAsync(
            IEnumerable<FeedEntry> rawEntries,
            string mySteamId64,
            CancellationToken cancel)
        {
            var rawList = rawEntries?.Where(e => e != null).ToList() ?? new List<FeedEntry>();
            if (rawList.Count == 0 || string.IsNullOrWhiteSpace(mySteamId64))
            {
                return rawList;
            }

            // Only appIds that actually appear in friend feed
            var appIds = rawList.Select(e => e.AppId).Where(id => id > 0).Distinct().ToList();

            // Ensure self cache exists for those appIds.
            // SteamDataProvider should cache internally to avoid duplicate network work.
            await EnsureSelfCachesAsync(mySteamId64, appIds, cancel).ConfigureAwait(false);

            // Build UI clones with decoration
            var result = new List<FeedEntry>(rawList.Count);

            foreach (var e in rawList)
            {
                cancel.ThrowIfCancellationRequested();

                var clone = CloneEntry(e);

                // Ensure unlocked url always has a value for reveal toggle
                if (string.IsNullOrWhiteSpace(clone.AchievementIconUnlockedUrl))
                {
                    clone.AchievementIconUnlockedUrl = clone.AchievementIconUrl;
                }

                if (!_steam.TryGetSelfAchievementData(mySteamId64, e.AppId, out var selfData) || selfData == null)
                {
                    result.Add(clone);
                    continue;
                }

                var key = clone.AchievementApiName ?? clone.AchievementDisplayName;

                // Hide locked-for-you is a VIEW concern; do not affect stored cache
                if (_settings.HideAchievementsLockedForYou && !string.IsNullOrWhiteSpace(key))
                {
                    var myUnlock = GetUtcOrNull(selfData, key);

                    // If I haven't unlocked it, swap to my locked icon and hide description.
                    if (!myUnlock.HasValue)
                    {
                        if (selfData.LockedIconUrls.TryGetValue(key, out var lockedUrl) &&
                            !string.IsNullOrWhiteSpace(lockedUrl))
                        {
                            clone.AchievementIconUrl = lockedUrl;
                            if (string.IsNullOrWhiteSpace(clone.AchievementIconUnlockedUrl))
                            {
                                clone.AchievementIconUnlockedUrl = lockedUrl;
                            }
                        }

                        clone.HideDescription = true;
                    }
                }

                // IncludeMyUnlockTime is VISUAL ONLY
                if (_settings.IncludeMyUnlockTime && !string.IsNullOrWhiteSpace(key))
                {
                    var myUnlock = GetUtcOrNull(selfData, key);
                    if (myUnlock.HasValue)
                    {
                        clone.MyUnlockTime = FeedEntryFactory.AsUtcKind(myUnlock.Value);
                    }
                }
                else
                {
                    clone.MyUnlockTime = null;
                }

                result.Add(clone);
            }

            return result
                .OrderByDescending(x => x.UnlockTime)
                .ToList();
        }

        private static DateTime? GetUtcOrNull(SelfAchievementGameData data, string key)
        {
            if (data == null || string.IsNullOrWhiteSpace(key)) return null;
            if (!data.UnlockTimesUtc.TryGetValue(key, out var utc)) return null;
            return FeedEntryFactory.AsUtcKind(utc);
        }

        private async Task EnsureSelfCachesAsync(string mySteamId64, List<int> appIds, CancellationToken cancel)
        {
            if (appIds == null || appIds.Count == 0) return;

            // small throttle to avoid hammering Steam pages on first view open
            using var gate = new SemaphoreSlim(1,1);

            var tasks = appIds.Select(async appId =>
            {
                cancel.ThrowIfCancellationRequested();
                await gate.WaitAsync(cancel).ConfigureAwait(false);
                try
                {
                    await _steam.EnsureSelfAchievementDataAsync(mySteamId64, appId, cancel).ConfigureAwait(false);
                }
                finally
                {
                    gate.Release();
                }
            });

            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        private static FeedEntry CloneEntry(FeedEntry e)
        {
            // Copy the fields your UI uses. If FeedEntry grows, either add here,
            // or add a copy ctor / Clone() on FeedEntry.
            return new FeedEntry
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

                AchievementIconUrl = e.AchievementIconUrl,
                AchievementIconUnlockedUrl = e.AchievementIconUnlockedUrl,

                UnlockTime = FeedEntryFactory.AsUtcKind(e.UnlockTime),

                // View state
                HideDescription = e.HideDescription,
                MyUnlockTime = e.MyUnlockTime,
                IsRevealed = e.IsRevealed
            };
        }
    }
}
