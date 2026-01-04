using System;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using Playnite.SDK;

namespace FriendsAchievementFeed.Services
{
    /// <summary>
    /// Manages Steam session cookies and Steam ID extraction.
    /// Centralizes cookie logic to improve maintainability and reduce duplication in SteamClient.
    /// </summary>
    internal static class SteamCookieManager
    {
        private static readonly string[] SteamSessionCookieNames = { "steamLoginSecure", "sessionid" };
        private static readonly Uri CommunityBase = new Uri("https://steamcommunity.com/");
        private static readonly Uri StoreBase = new Uri("https://store.steampowered.com/");

        /// <summary>
        /// Check if Steam session cookies exist in the CEF cookie store.
        /// </summary>
        public static bool HasSteamSessionCookies(IPlayniteAPI api, ILogger logger)
        {
            try
            {
                using (var view = api.WebViews.CreateOffscreenView())
                {
                    var cookies = view.GetCookies();
                    if (cookies == null)
                        return false;

                    return cookies.Any(c =>
                        c != null &&
                        !string.IsNullOrWhiteSpace(c.Domain) &&
                        IsSteamDomain(c.Domain) &&
                        SteamSessionCookieNames.Any(n => string.Equals(c.Name, n, StringComparison.OrdinalIgnoreCase)));
                }
            }
            catch (Exception ex)
            {
                logger?.Debug(ex, "[FAF] Failed to check Steam session cookies.");
                return false;
            }
        }

        private static bool IsSteamDomain(string domain)
        {
            if (string.IsNullOrWhiteSpace(domain)) return false;
            var d = domain.Trim().TrimStart('.');
            return d.EndsWith("steamcommunity.com", StringComparison.OrdinalIgnoreCase) ||
                   d.EndsWith("steampowered.com", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Extract SteamID64 from the steamLoginSecure cookie value.
        /// </summary>
        public static string TryExtractSteamId64FromSteamLoginSecure(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            string decoded;
            try
            {
                decoded = Uri.UnescapeDataString(value);
            }
            catch
            {
                decoded = value;
            }

            var m = Regex.Match(decoded, @"(?<id>\d{17})");
            return m.Success ? m.Groups["id"].Value : null;
        }

        /// <summary>
        /// Get the appropriate URI for adding cookies based on domain.
        /// </summary>
        public static Uri GetAddUriForDomain(string cookieDomain)
        {
            var d = (cookieDomain ?? "").Trim().TrimStart('.');
            if (d.EndsWith("steamcommunity.com", StringComparison.OrdinalIgnoreCase))
                return CommunityBase;
            if (d.EndsWith("steampowered.com", StringComparison.OrdinalIgnoreCase))
                return StoreBase;
            return new Uri("https://" + d);
        }

        /// <summary>
        /// Add CEF cookies to HttpClient's CookieContainer.
        /// Optimized to batch cookie operations and reduce overhead.
        /// </summary>
        public static void LoadCefCookiesIntoJar(
            IPlayniteAPI api,
            CookieContainer cookieJar,
            ILogger logger)
        {
            try
            {
                using (var view = api.WebViews.CreateOffscreenView())
                {
                    var cookies = view.GetCookies();
                    if (cookies == null)
                        return;

                    var steamCookies = cookies
                        .Where(c => c != null && !string.IsNullOrWhiteSpace(c.Domain))
                        .Where(c => IsSteamDomain(c.Domain))
                        .ToList();

                    foreach (var c in steamCookies)
                    {
                        try
                        {
                            var domain = c.Domain.TrimStart('.');
                            var path = string.IsNullOrWhiteSpace(c.Path) ? "/" : c.Path;

                            var cookie = new Cookie(c.Name, c.Value, path)
                            {
                                Domain = domain,
                                Secure = c.Secure,
                                HttpOnly = c.HttpOnly
                            };

                            if (c.Expires.HasValue && c.Expires.Value > DateTime.MinValue)
                            {
                                var expires = c.Expires.Value;
                                cookie.Expires = expires.Kind == DateTimeKind.Utc ? expires : expires.ToUniversalTime();
                            }

                            var uri = GetAddUriForDomain(domain);
                            cookieJar.Add(uri, cookie);
                        }
                        catch (Exception ex)
                        {
                            logger?.Debug(ex, $"[FAF] Failed to add cookie {c.Name} to jar");
                        }
                    }

                    // Add Steam timezone cookie for consistent achievement times
                    AddSteamTimezoneCookie(cookieJar, logger);
                }
            }
            catch (Exception ex)
            {
                logger?.Debug(ex, "[FAF] Failed to load cookies from CEF into jar");
            }
        }

        /// <summary>
        /// Add timezoneOffset cookie to ensure Steam returns achievement times in Pacific Time.
        /// </summary>
        private static void AddSteamTimezoneCookie(CookieContainer cookieJar, ILogger logger)
        {
            try
            {
                var tzCookie = new Cookie("timezoneOffset", SteamTimeParser.GetSteamTimezoneOffsetCookieValue(), "/")
                {
                    Domain = "steamcommunity.com"
                };
                cookieJar.Add(CommunityBase, tzCookie);
            }
            catch (Exception ex)
            {
                logger?.Debug(ex, "[FAF] Failed to set timezoneOffset cookie");
            }
        }

        /// <summary>
        /// Clear all Steam cookies from CEF.
        /// </summary>
        public static void ClearSteamCookiesFromCef(IPlayniteAPI api, ILogger logger)
        {
            try
            {
                using (var view = api.WebViews.CreateOffscreenView())
                {
                    view.DeleteDomainCookies(".steamcommunity.com");
                    view.DeleteDomainCookies("steamcommunity.com");
                    view.DeleteDomainCookies(".steampowered.com");
                    view.DeleteDomainCookies("steampowered.com");
                }
            }
            catch (Exception ex)
            {
                logger?.Debug(ex, "[FAF] Failed to clear Steam cookies from CEF.");
            }
        }
    }
}
