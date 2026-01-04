using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace FriendsAchievementFeed.Services
{
    /// <summary>
    /// Utilities for parsing Steam achievement unlock times and handling Steam's Pacific timezone.
    /// </summary>
    internal static class SteamTimeParser
    {
        private static readonly TimeZoneInfo SteamBaseTimeZone =
            TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");

        private static readonly string[] TimeFormats = new[]
        {
            "MMM d, yyyy h:mmtt", "MMM dd, yyyy h:mmtt",
            "MMMM d, yyyy h:mmtt", "MMMM dd, yyyy h:mmtt",
            "MMM d, yyyy H:mm",   "MMM dd, yyyy H:mm",
            "MMMM d, yyyy H:mm",  "MMMM dd, yyyy H:mm",
            "MMM d yyyy h:mmtt",  "MMM dd yyyy h:mmtt",
            "MMMM d yyyy h:mmtt", "MMMM dd yyyy h:mmtt",
            "MMM d yyyy H:mm",    "MMM dd yyyy H:mm",
            "MMMM d yyyy H:mm",   "MMMM dd yyyy H:mm",
            "d MMM, yyyy h:mmtt", "dd MMM, yyyy h:mmtt",
            "d MMMM, yyyy h:mmtt","dd MMMM, yyyy h:mmtt",
            "d MMM, yyyy H:mm",   "dd MMM, yyyy H:mm",
            "d MMMM, yyyy H:mm",  "dd MMMM, yyyy H:mm",
            "d MMM yyyy h:mmtt",  "dd MMM yyyy h:mmtt",
            "d MMMM yyyy h:mmtt", "dd MMMM yyyy h:mmtt",
            "d MMM yyyy H:mm",    "dd MMM yyyy H:mm",
            "d MMMM yyyy H:mm",   "dd MMMM yyyy H:mm",
        };

        /// <summary>
        /// Parse Steam's English achievement unlock time format to UTC.
        /// Example: "Unlocked Jan 15, 2024 @ 3:42pm" -> UTC DateTime
        /// </summary>
        public static DateTime? TryParseSteamUnlockTimeEnglishToUtc(string text)
        {
            var flat = Regex.Replace(text ?? "", @"\s+", " ").Trim();
            if (flat.Length == 0)
                return null;

            var m = Regex.Match(
                flat,
                @"Unlocked\s+(?<date>.+?)\s+@\s+(?<time>\d{1,2}:\d{2}\s*(?:am|pm)?)",
                RegexOptions.IgnoreCase);

            if (!m.Success)
                return null;

            var datePart = (m.Groups["date"].Value ?? "").Trim().TrimEnd(',');
            var timePart = Regex.Replace((m.Groups["time"].Value ?? "").Trim(), @"\s+(am|pm)$", "$1", RegexOptions.IgnoreCase);

            var steamNow = GetSteamNow();
            var hasYear = Regex.IsMatch(datePart, @"\b\d{4}\b");
            if (!hasYear)
                datePart = $"{datePart}, {steamNow.Year}";

            var combined = $"{datePart} {timePart}".Trim();
            var culture = new CultureInfo("en-US");

            if (!DateTime.TryParseExact(combined, TimeFormats, culture, DateTimeStyles.AllowWhiteSpaces, out var dt) &&
                !DateTime.TryParse(combined, culture, DateTimeStyles.AllowWhiteSpaces, out dt))
            {
                return null;
            }

            // Handle year rollover for dates without explicit year
            if (!hasYear)
            {
                var steamCandidate = DateTime.SpecifyKind(dt, DateTimeKind.Unspecified);
                var steamCandidateLocal = TimeZoneInfo.ConvertTime(steamCandidate, SteamBaseTimeZone);

                if (steamCandidateLocal > steamNow.AddDays(2))
                    dt = dt.AddYears(-1);
            }

            try
            {
                return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(dt, DateTimeKind.Unspecified), SteamBaseTimeZone);
            }
            catch
            {
                return DateTime.SpecifyKind(dt, DateTimeKind.Utc);
            }
        }

        /// <summary>
        /// Get current time in Steam's Pacific timezone.
        /// </summary>
        public static DateTime GetSteamNow()
        {
            try
            {
                return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, SteamBaseTimeZone);
            }
            catch
            {
                return DateTime.Now;
            }
        }

        /// <summary>
        /// Get the timezone offset for Steam's Pacific timezone for cookie usage.
        /// Returns -28800 seconds (UTC-8) as Steam's standard offset.
        /// </summary>
        public static string GetSteamTimezoneOffsetCookieValue()
        {
            return "-28800,0";
        }
    }
}
