using System;
using System.Collections.Generic;

namespace FriendsAchievementFeed.Services
{
    public class SelfAchievementGameData
    {
        public DateTime LastUpdatedUtc { get; set; } = DateTime.UtcNow;

        // Achievement API key -> my unlock time (UTC) if unlocked, otherwise null/missing.
        public Dictionary<string, DateTime?> UnlockTimesUtc { get; set; }
            = new Dictionary<string, DateTime?>(StringComparer.OrdinalIgnoreCase);

        // Achievement API key -> locked icon URL as shown on *my* page (usually greyed).
        public Dictionary<string, string> LockedIconUrls { get; set; }
            = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }
}
