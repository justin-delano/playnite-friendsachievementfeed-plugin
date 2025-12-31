using System.Collections.Generic;

namespace FriendsAchievementFeed.Services
{
    public class AchievementsHealthResult
    {
        public List<ScrapedAchievementRow> Rows { get; set; } = new List<ScrapedAchievementRow>();

        public bool TransientFailure { get; set; }
        public bool StatsUnavailable { get; set; }

        public string RequestedUrl { get; set; }
        public string FinalUrl { get; set; }
        public int StatusCode { get; set; }
        public string Detail { get; set; }
    }
}
