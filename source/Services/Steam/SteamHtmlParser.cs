using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using Playnite.SDK;
using FriendsAchievementFeed.Services.Steam.Models;

namespace FriendsAchievementFeed.Services.Steam
{
    internal static class SteamHtmlParser
    {
        private static readonly Regex TimeRegex = new Regex(
            @"Unlocked\s+(\w+\s+\d+,\s+\d+\s+@\s+\d+:\d+[ap]m)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static SteamPlayerSummaries TryParseProfileHtmlToSummary(ulong id, string html)
        {
            if (string.IsNullOrWhiteSpace(html)) return null;

            try
            {
                var doc = new HtmlDocument();
                doc.LoadHtml(html);

                var name = WebUtility.HtmlDecode(
                    doc.DocumentNode.SelectSingleNode("//span[contains(@class,'actual_persona_name')]")?.InnerText ?? ""
                ).Trim();

                if (string.IsNullOrEmpty(name)) return null;

                var avatar = doc.DocumentNode.SelectSingleNode("//meta[@property='og:image']")
                    ?.GetAttributeValue("content", "") ?? "";

                return new SteamPlayerSummaries
                {
                    SteamId = id.ToString(),
                    PersonaName = name,
                    Avatar = avatar,
                    AvatarMedium = avatar,
                    AvatarFull = avatar
                };
            }
            catch
            {
                return null;
            }
        }

        public static List<ScrapedAchievementRow> ParseAchievements(string html, bool includeLocked, ILogger logger = null)
        {
            var achievements = new List<ScrapedAchievementRow>();
            if (string.IsNullOrWhiteSpace(html)) return achievements;

            try
            {
                var doc = new HtmlDocument();
                doc.LoadHtml(html);

                var rows = doc.DocumentNode.SelectNodes("//div[contains(@class,'achieveRow')]");
                if (rows == null) return achievements;

                foreach (var row in rows)
                {
                    try
                    {
                        var isUnlocked = !row.GetClasses().Contains("achieveUnlocked") == false;

                        if (!includeLocked && !isUnlocked)
                            continue;

                        var titleNode = row.SelectSingleNode(".//h3");
                        var title = WebUtility.HtmlDecode(titleNode?.InnerText?.Trim() ?? "");

                        var descNode = row.SelectSingleNode(".//h5");
                        var desc = WebUtility.HtmlDecode(descNode?.InnerText?.Trim() ?? "");

                        var iconNode = row.SelectSingleNode(".//img");
                        var iconUrl = iconNode?.GetAttributeValue("src", "") ?? "";

                        DateTime? unlockTime = null;
                        if (isUnlocked)
                        {
                            var unlockText = row.SelectSingleNode(".//div[contains(@class,'achieveUnlockTime')]")?.InnerText ?? "";
                            unlockTime = TryParseUnlockTime(unlockText);
                        }

                        var keyBasisA = !string.IsNullOrWhiteSpace(title) ? title : iconUrl;
                        var keyBasisB = !string.IsNullOrWhiteSpace(desc)
                            ? desc.Length > 20 ? desc.Substring(0, 20) : desc
                            : iconUrl;

                        var combinedKey = $"{keyBasisA}|{keyBasisB}".Replace(" ", "").ToLowerInvariant();
                        if (combinedKey.Length > 60)
                            combinedKey = combinedKey.Substring(0, 60);

                        achievements.Add(new ScrapedAchievementRow
                        {
                            Key = combinedKey,
                            DisplayName = title,
                            Description = desc,
                            IconUrl = iconUrl,
                            UnlockTimeUtc = unlockTime
                        });
                    }
                    catch (Exception ex)
                    {
                        logger?.Debug(ex, "[FAF] Error parsing individual achievement row");
                    }
                }
            }
            catch (Exception ex)
            {
                logger?.Debug(ex, "[FAF] Error parsing achievements HTML");
            }

            return achievements;
        }

        public static bool LooksLoggedOutHeader(string html)
        {
            if (string.IsNullOrWhiteSpace(html)) return false;

            try
            {
                return html.IndexOf("g_steamID = false", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch
            {
                return false;
            }
        }

        public static bool LooksRateLimited(string html)
        {
            if (string.IsNullOrWhiteSpace(html)) return false;

            try
            {
                return html.IndexOf("Please wait and try your request again", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch
            {
                return false;
            }
        }

        private static DateTime? TryParseUnlockTime(string unlockText)
        {
            if (string.IsNullOrWhiteSpace(unlockText)) return null;

            try
            {
                var match = TimeRegex.Match(unlockText);
                if (match.Success)
                {
                    var timeStr = match.Groups[1].Value.Trim();
                    if (DateTime.TryParseExact(timeStr, "MMM d, yyyy @ h:mmtt", 
                        CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dt))
                    {
                        return dt.ToUniversalTime();
                    }
                }
            }
            catch
            {
                // Ignore parse failures
            }

            return null;
        }
    }
}