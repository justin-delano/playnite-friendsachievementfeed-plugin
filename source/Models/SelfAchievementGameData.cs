using System;
using System.Collections.Generic;
using System.Runtime.Serialization;

namespace FriendsAchievementFeed.Models
{
    public sealed class SelfAchievementGameData
    {
        public DateTime LastUpdatedUtc { get; set; }

        public bool NoAchievements { get; set; }

        public Dictionary<string, DateTime> UnlockTimesUtc { get; set; } =
            new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, string> SelfIconUrls { get; set; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

}
