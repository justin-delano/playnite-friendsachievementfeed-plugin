using HtmlAgilityPack;
using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace FriendsAchievementFeed.Services
{
    public class SteamPlayerSummaries
    {
        public string SteamId { get; set; }
        public string PersonaName { get; set; }
        public string Avatar { get; set; }
        public string AvatarMedium { get; set; }
        public string AvatarFull { get; set; }
    }

    public class ScrapedAchievementRow
    {
        public string Key { get; set; }
        public string DisplayName { get; set; }
        public string Description { get; set; }
        public string IconUrl { get; set; }
        public DateTime? UnlockTimeUtc { get; set; }
    }

    internal sealed class SteamClient : IDisposable
    {
        private static readonly Uri CommunityBase = new Uri("https://steamcommunity.com/");
        private const string DefaultUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

        private readonly IPlayniteAPI _api;
        private readonly ILogger _logger;
        private readonly Func<string> _getApiKey;

        private HttpClient _http;
        private HttpClientHandler _handler;
        private readonly object _cookieLock = new object();
        private DateTime _cookiesLoadedAtUtc = DateTime.MinValue;
        private static readonly TimeSpan CookieRefreshInterval = TimeSpan.FromMinutes(20);

        private readonly SemaphoreSlim _requestGate = new SemaphoreSlim(1, 1);
        private readonly object _rateLock = new object();
        private DateTime _nextAllowedUtc = DateTime.MinValue;
        private int _backoffMs = 0;
        private readonly Random _rng = new Random();
        private int _penaltyMs = 0;

        private const int MinPenaltyMs = 1200;
        private const int MaxPenaltyMs = 30000;
        private static readonly TimeSpan MinGapBetweenRequests = TimeSpan.FromMilliseconds(750);

        private readonly Dictionary<string, ulong> _vanityCache = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);

        private static readonly TimeZoneInfo SteamBaseTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");

        public SteamClient(IPlayniteAPI api, ILogger logger, Func<string> getApiKey = null)
        {
            _api = api;
            _logger = logger;
            _getApiKey = getApiKey;
            BuildHttpClient(new CookieContainer());
        }

        public void Dispose()
        {
            try { _http?.Dispose(); } catch { }
            try { _handler?.Dispose(); } catch { }
            try { _requestGate?.Dispose(); } catch { }
        }

        // ---------------------------------------------------------------------
        // GAMES & PROFILES
        // ---------------------------------------------------------------------

        /// <summary>
        /// Owned games with playtime are fetched ONLY via Steam Web API + API key.
        /// Returns appid -> playtime_forever (minutes). Includes 0-playtime entries.
        /// </summary>
        public async Task<Dictionary<int, int>> GetOwnedGamePlaytimesAsync(string steamId64, CancellationToken ct)
        {
            var key = GetApiKey();
            if (string.IsNullOrWhiteSpace(key))
            {
                _logger.Warn($"[SteamHtml] Steam Web API key is missing. Cannot fetch owned game playtimes for {steamId64}.");
                return new Dictionary<int, int>();
            }

            var url =
                $"https://api.steampowered.com/IPlayerService/GetOwnedGames/v0001/?key={key}&steamid={steamId64}&include_appinfo=0&include_played_free_games=1&format=json";

            try
            {
                var json = await GetSteamStringAsync(url, requiresCookies: false, ct).ConfigureAwait(false);
                var dict = ParseOwnedGamePlaytimesJson(json);

                _logger.Info($"[SteamHtml] Owned games via API: {dict.Count} AppIDs (with playtime) for {steamId64}");
                return dict;
            }
            catch (Exception ex)
            {
                _logger.Warn($"[SteamHtml] Owned games API failed for {steamId64}: {ex.Message}");
                return new Dictionary<int, int>();
            }
        }

        /// <summary>
        /// Compatibility method for existing callers: returns all owned appids (including 0 playtime).
        /// </summary>
        public async Task<List<int>> GetOwnedGameIdsAsync(string steamId64, CancellationToken ct)
        {
            var dict = await GetOwnedGamePlaytimesAsync(steamId64, ct).ConfigureAwait(false);
            return dict?.Keys?.ToList() ?? new List<int>();
        }

        private Dictionary<int, int> ParseOwnedGamePlaytimesJson(string json)
        {
            var result = new Dictionary<int, int>();
            if (string.IsNullOrWhiteSpace(json))
            {
                return result;
            }

            // Match each game object containing appid and playtime_forever
            var matches = Regex.Matches(
                json,
                @"{[^{}]*?""appid""\s*:\s*(?<id>\d+)[^{}]*?""playtime_forever""\s*:\s*(?<play>\d+)[^{}]*?}",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);

            foreach (Match m in matches)
            {
                if (int.TryParse(m.Groups["id"].Value, out var id) && id > 0)
                {
                    var play = 0;
                    int.TryParse(m.Groups["play"].Value, out play);
                    result[id] = play;
                }
            }

            // Fallback: capture appids with default 0 if payload format shifts
            if (result.Count == 0)
            {
                var idMatches = Regex.Matches(json, @"""appid""\s*:\s*(?<id>\d+)", RegexOptions.IgnoreCase);
                foreach (Match m in idMatches)
                {
                    if (int.TryParse(m.Groups["id"].Value, out var id) && id > 0)
                    {
                        if (!result.ContainsKey(id)) result[id] = 0;
                    }
                }
            }

            return result;
        }

        public async Task<string> GetProfileHtmlAsync(string steamId64, CancellationToken ct)
        {
            var url = $"https://steamcommunity.com/profiles/{steamId64}/?l=english";
            return await GetSteamStringAsync(url, true, ct).ConfigureAwait(false);
        }

        // ---------------------------------------------------------------------
        // PLAYER SUMMARIES
        // ---------------------------------------------------------------------

        public async Task<List<SteamPlayerSummaries>> GetPlayerSummariesAsync(List<ulong> steamIds, CancellationToken ct)
        {
            var results = new List<SteamPlayerSummaries>();
            if (steamIds == null || !steamIds.Any()) return results;

            var distinct = steamIds.Where(x => x > 0).Distinct().ToList();
            var key = GetApiKey();

            if (!string.IsNullOrWhiteSpace(key))
            {
                const int batchSize = 100;
                for (int i = 0; i < distinct.Count; i += batchSize)
                {
                    ct.ThrowIfCancellationRequested();
                    var batch = string.Join(",", distinct.Skip(i).Take(batchSize));
                    var url = $"https://api.steampowered.com/ISteamUser/GetPlayerSummaries/v2/?key={key}&steamids={batch}";
                    try
                    {
                        var json = await GetSteamStringAsync(url, false, ct).ConfigureAwait(false);
                        results.AddRange(ParsePlayerSummariesJson(json));
                    }
                    catch { }
                }
                if (results.Any()) return results;
            }

            // HTML fallback
            foreach (var id in distinct)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var html = await GetProfileHtmlAsync(id.ToString(), ct).ConfigureAwait(false);
                    var p = TryParseProfileHtmlToSummary(id, html);
                    if (p != null) results.Add(p);
                }
                catch { }
            }
            return results;
        }

        private SteamPlayerSummaries TryParseProfileHtmlToSummary(ulong id, string html)
        {
            if (string.IsNullOrWhiteSpace(html)) return null;
            try
            {
                var doc = new HtmlDocument();
                doc.LoadHtml(html);
                var name = WebUtility.HtmlDecode(doc.DocumentNode.SelectSingleNode("//span[contains(@class,'actual_persona_name')]")?.InnerText ?? "").Trim();
                var avatar = doc.DocumentNode.SelectSingleNode("//meta[@property='og:image']")?.GetAttributeValue("content", "") ?? "";

                if (string.IsNullOrEmpty(name)) return null;

                return new SteamPlayerSummaries
                {
                    SteamId = id.ToString(),
                    PersonaName = name,
                    Avatar = avatar,
                    AvatarMedium = avatar,
                    AvatarFull = avatar
                };
            }
            catch { return null; }
        }

        // ---------------------------------------------------------------------
        // ACHIEVEMENTS & FRIENDS
        // ---------------------------------------------------------------------

        public async Task<string> GetAchievementsHtmlAsync(string steamId64, int appId, CancellationToken ct)
        {
            var url = $"https://steamcommunity.com/profiles/{steamId64}/stats/{appId}/?tab=achievements&l=english";
            return await GetSteamStringAsync(url, requiresCookies: true, ct).ConfigureAwait(false);
        }

        public List<ScrapedAchievementRow> ParseAchievementsPage(string html)
        {
            var safe = html ?? string.Empty;

            if (safe.Length < 200) return new List<ScrapedAchievementRow>();
            if (safe.IndexOf("This profile is private", StringComparison.OrdinalIgnoreCase) >= 0) return new List<ScrapedAchievementRow>();

            if (safe.IndexOf("g_steamID", StringComparison.OrdinalIgnoreCase) < 0 &&
                safe.IndexOf("steamcommunity.com", StringComparison.OrdinalIgnoreCase) < 0)
            {
                return new List<ScrapedAchievementRow>();
            }

            if (safe.IndexOf("429", StringComparison.OrdinalIgnoreCase) >= 0 &&
                safe.IndexOf("Too Many Requests", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return new List<ScrapedAchievementRow>();
            }

            if (safe.IndexOf("Sign In", StringComparison.OrdinalIgnoreCase) >= 0 &&
                safe.IndexOf("login", StringComparison.OrdinalIgnoreCase) >= 0 &&
                safe.IndexOf("achievements", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return new List<ScrapedAchievementRow>();
            }

            var doc = new HtmlDocument();
            doc.LoadHtml(safe);

            var nodes = doc.DocumentNode.SelectNodes("//div[contains(@class,'achieveRow')]");
            if (nodes == null || nodes.Count == 0)
            {
                nodes = doc.DocumentNode.SelectNodes(
                    "//*[contains(@class,'achievement') and (.//h3 or .//div[contains(@class,'achieveUnlockTime')])]");
            }

            if (nodes == null || nodes.Count == 0)
            {
                return new List<ScrapedAchievementRow>();
            }

            var results = new List<ScrapedAchievementRow>();

            foreach (var row in nodes)
            {
                if (row.SelectSingleNode(".//div[contains(@class,'achieveHiddenBox')]") != null)
                {
                    continue;
                }

                var unlockText = WebUtility.HtmlDecode(
                    row.SelectSingleNode(".//div[contains(@class,'achieveUnlockTime')]")?.InnerText ?? ""
                ).Trim();

                var unlockUtc = TryParseSteamUnlockTimeEnglishToUtc(unlockText);
                if (!unlockUtc.HasValue)
                {
                    continue;
                }

                var title = WebUtility.HtmlDecode(row.SelectSingleNode(".//h3")?.InnerText ?? "").Trim();
                var desc = WebUtility.HtmlDecode(row.SelectSingleNode(".//h5")?.InnerText ?? "").Trim();

                var img =
                    row.SelectSingleNode(".//div[contains(@class,'achieveImgHolder')]//img") ??
                    row.SelectSingleNode(".//img[contains(@src,'steamstatic') or contains(@src,'steamcdn') or contains(@src,'akamai')]") ??
                    row.SelectSingleNode(".//img");

                var iconUrl = img?.GetAttributeValue("src", "")?.Trim() ?? "";

                var keyBasisA = !string.IsNullOrWhiteSpace(title) ? title : iconUrl;
                var keyBasisB = !string.IsNullOrWhiteSpace(desc) ? desc : unlockUtc.Value.ToString("O", CultureInfo.InvariantCulture);

                results.Add(new ScrapedAchievementRow
                {
                    Key = (keyBasisA + "|" + keyBasisB).Trim(),
                    DisplayName = title,
                    Description = desc,
                    IconUrl = iconUrl,
                    UnlockTimeUtc = unlockUtc
                });
            }

            return results;
        }

        public async Task<List<ulong>> GetFriendIdsAsync(string steamId64, CancellationToken ct)
        {
            var key = GetApiKey();
            if (!string.IsNullOrWhiteSpace(key))
            {
                var url = $"https://api.steampowered.com/ISteamUser/GetFriendList/v1/?key={key}&steamid={steamId64}&relationship=friend";
                try
                {
                    var json = await GetSteamStringAsync(url, false, ct).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(json))
                    {
                        var matches = Regex.Matches(json, @"""steamid""\s*:\s*""(?<id>\d+)""");
                        var apiResults = matches.Cast<Match>().Select(m => ulong.Parse(m.Groups["id"].Value)).ToList();
                        if (apiResults.Any())
                        {
                            _logger.Info($"[SteamHtml] Found {apiResults.Count} friends via API.");
                            return apiResults;
                        }
                    }
                }
                catch (Exception ex) { _logger.Debug($"[SteamHtml] API Friendlist failed: {ex.Message}"); }
            }

            _logger.Info($"[SteamHtml] Falling back to HTML/Ajax friend list for {steamId64}");
            return await GetFriendIdsFromHtmlAsync(steamId64, ct).ConfigureAwait(false);
        }

        private async Task<List<ulong>> GetFriendIdsFromHtmlAsync(string steamId64, CancellationToken ct)
        {
            var url = $"https://steamcommunity.com/profiles/{steamId64}/friends/?ajax=1&l=english";
            var body = await GetSteamStringAsync(url, true, ct).ConfigureAwait(false);
            var html = UnwrapAjaxHtml(body);
            var matches = Regex.Matches(html, @"data-steamid\s*=\s*[""'](?<id>\d+)[""']");
            return matches.Cast<Match>().Select(m => ulong.Parse(m.Groups["id"].Value)).ToList();
        }

        // ---------------------------------------------------------------------
        // HTTP CORE
        // ---------------------------------------------------------------------

        private async Task<string> GetSteamStringAsync(string url, bool requiresCookies, CancellationToken ct)
        {
            await _requestGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var uri = new Uri(url);
                if (requiresCookies && IsSteamCookieHost(uri)) await EnsureCookiesAsync(ct).ConfigureAwait(false);

                for (int attempt = 1; attempt <= 5; attempt++)
                {
                    ct.ThrowIfCancellationRequested();
                    await WaitForPacingAsync(ct).ConfigureAwait(false);

                    using (var req = new HttpRequestMessage(HttpMethod.Get, uri))
                    {
                        req.Headers.TryAddWithoutValidation("User-Agent", DefaultUserAgent);
                        try
                        {
                            var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
                            if ((int)resp.StatusCode == 429) { Register429(null); continue; }
                            resp.EnsureSuccessStatusCode();
                            var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                            RegisterSuccess();
                            return body;
                        }
                        catch when (attempt < 5)
                        {
                            Register429(null);
                        }
                    }
                }

                return string.Empty;
            }
            finally
            {
                _requestGate.Release();
            }
        }

        private async Task EnsureCookiesAsync(CancellationToken ct)
        {
            if ((DateTime.UtcNow - _cookiesLoadedAtUtc) < CookieRefreshInterval) return;

            await _api.MainView.UIDispatcher.InvokeAsync(() =>
            {
                using (var view = _api.WebViews.CreateOffscreenView())
                {
                    var cookies = view.GetCookies();
                    var jar = new CookieContainer();

                    if (cookies != null)
                    {
                        foreach (var c in cookies)
                        {
                            if (string.Equals(c.Name, "timezoneOffset", StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            var domain = c.Domain?.TrimStart('.');
                            if (domain != null && (domain.Contains("steamcommunity.com") || domain.Contains("steampowered.com")))
                            {
                                jar.Add(new Uri($"https://{domain}"), new Cookie(c.Name, c.Value, c.Path, domain));
                            }
                        }
                    }

                    BuildHttpClient(jar);
                    _cookiesLoadedAtUtc = DateTime.UtcNow;
                }
            });
        }

        private void BuildHttpClient(CookieContainer jar)
        {
            _handler?.Dispose(); _http?.Dispose();
            _handler = new HttpClientHandler { CookieContainer = jar, AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate };
            _http = new HttpClient(_handler) { Timeout = TimeSpan.FromSeconds(30) };
        }

        private async Task WaitForPacingAsync(CancellationToken ct)
        {
            TimeSpan delay;
            lock (_rateLock) delay = _nextAllowedUtc - DateTime.UtcNow;
            if (delay > TimeSpan.Zero) await Task.Delay(delay, ct).ConfigureAwait(false);
        }

        private void Register429(TimeSpan? retryAfter)
        {
            lock (_rateLock)
            {
                _backoffMs = Math.Min(60000, _backoffMs + 2000);
                _penaltyMs = Math.Max(MinPenaltyMs, _penaltyMs <= 0 ? 5000 : Math.Min(MaxPenaltyMs, _penaltyMs * 2));
                _nextAllowedUtc = DateTime.UtcNow + TimeSpan.FromMilliseconds(_penaltyMs + _backoffMs);
            }
        }

        private void RegisterSuccess()
        {
            lock (_rateLock)
            {
                _penaltyMs = Math.Max(0, _penaltyMs - 500);
                _backoffMs = Math.Max(0, _backoffMs - 500);
                _nextAllowedUtc = DateTime.UtcNow + MinGapBetweenRequests;
            }
        }

        private static bool IsSteamCookieHost(Uri uri) => uri.Host.Contains("steamcommunity.com") || uri.Host.Contains("steampowered.com");
        private string GetApiKey() => _getApiKey?.Invoke();

        private static string UnwrapAjaxHtml(string body)
        {
            if (string.IsNullOrWhiteSpace(body) || !body.Trim().StartsWith("{")) return body;
            var match = Regex.Match(body, @"""(?:html|results_html|data)""\s*:\s*""(?<html>.*?)""(?:\s*,|\s*})", RegexOptions.Singleline);
            return match.Success ? Regex.Unescape(match.Groups["html"].Value) : body;
        }

        private IEnumerable<SteamPlayerSummaries> ParsePlayerSummariesJson(string json)
        {
            var results = new List<SteamPlayerSummaries>();
            var matches = Regex.Matches(json, @"{""steamid"":""(?<id>\d+)"",""communityvisibilitystate"":\d+,""profilestate"":\d+,""personaname"":""(?<name>.*?)"".*?,""avatar"":""(?<av>.*?)""");
            foreach (Match m in matches)
            {
                var av = Regex.Unescape(m.Groups["av"].Value);
                results.Add(new SteamPlayerSummaries
                {
                    SteamId = m.Groups["id"].Value,
                    PersonaName = Regex.Unescape(m.Groups["name"].Value),
                    Avatar = av,
                    AvatarMedium = av,
                    AvatarFull = av
                });
            }
            return results;
        }

        private static DateTime? TryParseSteamUnlockTimeEnglishToUtc(string text)
        {
            var flat = Regex.Replace(text ?? "", @"\s+", " ").Trim();
            if (flat.Length == 0) return null;

            var m = Regex.Match(
                flat,
                @"Unlocked\s+(?<date>.+?)\s+@\s+(?<time>\d{1,2}:\d{2}\s*(?:am|pm)?)",
                RegexOptions.IgnoreCase);

            if (!m.Success)
            {
                return null;
            }

            var datePart = (m.Groups["date"].Value ?? "").Trim();
            var timePart = (m.Groups["time"].Value ?? "").Trim();

            timePart = Regex.Replace(timePart, @"\s+(am|pm)$", "$1", RegexOptions.IgnoreCase);

            var steamNow = GetSteamNow();
            var hasYear = Regex.IsMatch(datePart, @"\b\d{4}\b");

            datePart = datePart.TrimEnd(',');
            if (!hasYear)
            {
                datePart = $"{datePart}, {steamNow.Year}";
            }

            var combined = $"{datePart} {timePart}".Trim();

            var culture = new CultureInfo("en-US");

            var formats = new[]
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

            if (!DateTime.TryParseExact(combined, formats, culture, DateTimeStyles.AllowWhiteSpaces, out var dt))
            {
                if (!DateTime.TryParse(combined, culture, DateTimeStyles.AllowWhiteSpaces, out dt))
                {
                    return null;
                }
            }

            if (!hasYear)
            {
                var steamCandidate = DateTime.SpecifyKind(dt, DateTimeKind.Unspecified);
                var steamCandidateLocal = TimeZoneInfo.ConvertTime(steamCandidate, SteamBaseTimeZone);

                if (steamCandidateLocal > steamNow.AddDays(2))
                {
                    dt = dt.AddYears(-1);
                }
            }

            var steamLocalUnspecified = DateTime.SpecifyKind(dt, DateTimeKind.Unspecified);

            try
            {
                return TimeZoneInfo.ConvertTimeToUtc(steamLocalUnspecified, SteamBaseTimeZone);
            }
            catch
            {
                return DateTime.SpecifyKind(dt, DateTimeKind.Utc);
            }
        }

        private static DateTime GetSteamNow()
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
    }
}
