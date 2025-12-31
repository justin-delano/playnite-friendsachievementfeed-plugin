using System;
using System.Collections.Generic;

namespace FriendsAchievementFeed.Models
{
    // Wrapper used for atomic disk serialization of the persisted feed cache.
    public sealed class FeedData
    {
        public DateTime LastUpdatedUtc { get; set; }
        public List<FeedEntry> Entries { get; set; } = new List<FeedEntry>();
    }
}
