using System;
using System.Collections.Generic;

namespace FriendsAchievementFeed.Services.Steam.Models
{
    /// <summary>
    /// Represents a scraped achievement row from Steam's HTML.
    /// Extracted from SteamClient.cs for better organization.
    /// </summary>
    public class ScrapedAchievementRow
    {
        public string Key { get; set; }
        public string DisplayName { get; set; }
        public string Description { get; set; }
        public string IconUrl { get; set; }
        public DateTime? UnlockTimeUtc { get; set; }
    }

    /// <summary>
    /// Result of steam achievement health check.
    /// Moved from Services layer for better organization.
    /// </summary>
    public class AchievementsHealthResult
    {
        public List<ScrapedAchievementRow> Rows { get; set; } = new List<ScrapedAchievementRow>();

        public bool TransientFailure { get; set; }
        public bool StatsUnavailable { get; set; }
        public string Detail { get; set; }

        public string RequestedUrl { get; set; }
        public string FinalUrl { get; set; }
        public int StatusCode { get; set; }
        public string StatusDescription { get; set; }
        public string ContentBlurb { get; set; }

        public bool HasRows => Rows?.Count > 0;
        public bool SuccessWithRows => HasRows && !TransientFailure && !StatsUnavailable;
    }
}