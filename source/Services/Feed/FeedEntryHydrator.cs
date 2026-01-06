using FriendsAchievementFeed.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace FriendsAchievementFeed.Services
{
    internal sealed class FeedEntryHydrator
    {
        private readonly ICacheManager _cache;
        private readonly FeedEntryFactory _factory;

        public FeedEntryHydrator(ICacheManager cache, FeedEntryFactory factory)
        {
            _cache = cache;
            _factory = factory;
        }

        private static string SelfKey(FeedEntry e)
        {
            if (e == null) return string.Empty;
            // Prefer Playnite GUID string; fall back to app key
            return e.PlayniteGameId.HasValue ? e.PlayniteGameId.Value.ToString() : ("app:" + e.AppId);
        }

        public List<FeedEntry> HydrateForUi(IEnumerable<FeedEntry> cached, CancellationToken cancel)
        {
            var list = cached?.Where(x => x != null).ToList() ?? new List<FeedEntry>();
            var result = new List<FeedEntry>(list.Count);

            // Load self once per key
            foreach (var g in list.GroupBy(SelfKey, StringComparer.OrdinalIgnoreCase))
            {
                cancel.ThrowIfCancellationRequested();

                var key = g.Key;
                var self = !string.IsNullOrWhiteSpace(key)
                    ? _cache.LoadSelfAchievementData(key)
                    : null;

                foreach (var e in g)
                {
                    var ui = _factory.HydrateUiEntry(e, self);
                    if (ui != null) result.Add(ui);
                }
            }

            return result;
        }
    }
}
