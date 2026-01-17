using HtmlAgilityPack;
using FriendsAchievementFeed.Services.Steam.Models;
using FriendsAchievementFeed.Services.Steam;
using Playnite.SDK;
using Playnite.SDK.Data;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace FriendsAchievementFeed.Services
{
    // -------------------------------------------------------------------------
    // Steam Session Store
    // -------------------------------------------------------------------------
    internal sealed class SteamSessionStore
    {
        private readonly string _filePath;
        private readonly object _fileLock = new object();

        public SteamSessionStore(string pluginUserDataPath)
        {
            if (string.IsNullOrWhiteSpace(pluginUserDataPath))
                throw new ArgumentNullException(nameof(pluginUserDataPath));

            Directory.CreateDirectory(pluginUserDataPath);
            _filePath = Path.Combine(pluginUserDataPath, "steam_session.json");
        }

        public bool TryLoad(out SteamSessionData session)
        {
            session = null;
            lock (_fileLock)
            {
                if (!File.Exists(_filePath)) return false;
                try
                {
                    var json = File.ReadAllText(_filePath, Encoding.UTF8);
                    session = Serialization.FromJson<SteamSessionData>(json);
                    return session != null && !string.IsNullOrWhiteSpace(session.SelfSteamId64);
                }
                catch { return false; }
            }
        }

        public void Save(SteamSessionData session)
        {
            if (session == null || string.IsNullOrWhiteSpace(session.SelfSteamId64)) return;
            lock (_fileLock)
            {
                try
                {
                    var json = Serialization.ToJson(session);
                    File.WriteAllText(_filePath, json, Encoding.UTF8);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to save Steam session: {ex.Message}");
                }
            }
        }

        public void Clear()
        {
            lock (_fileLock)
            {
                try { if (File.Exists(_filePath)) File.Delete(_filePath); } catch { }
            }
        }
    }

    public sealed class SteamClient : IDisposable
    {
        private static readonly Uri CommunityBase = new Uri("https://steamcommunity.com/");
        private static readonly Uri StoreBase = new Uri("https://store.steampowered.com/");
        private const string DefaultUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";
        private const int MaxAttempts = 3;

        public sealed class SteamPageResult
        {
            public string RequestedUrl { get; set; }
            public string FinalUrl { get; set; }
            public HttpStatusCode StatusCode { get; set; }
            public string Html { get; set; }
            public bool WasRedirected { get; set; }
        }

        private readonly IPlayniteAPI _api;
        private readonly ILogger _logger;
        private readonly SteamSessionManager _sessionManager;
        private readonly SteamApiHelper _apiHelper;
        private readonly CookieContainer _cookieJar = new CookieContainer();
        private readonly object _cookieLock = new object();

        private HttpClient _http;
        private HttpClientHandler _handler;
        private HttpClient _apiHttp;
        private HttpClientHandler _apiHandler;

        private readonly ConcurrentDictionary<int, Lazy<Task<bool?>>> _hasAchievementsCache =
            new ConcurrentDictionary<int, Lazy<Task<bool?>>>();

        public SteamClient(IPlayniteAPI api, ILogger logger, string pluginUserDataPath)
        {
            _api = api ?? throw new ArgumentNullException(nameof(api));
            _logger = logger;
            _sessionManager = new SteamSessionManager(api, logger, pluginUserDataPath);

            BuildHttpClientsOnce();
            _apiHelper = new SteamApiHelper(_apiHttp, _logger);
            
            LoadCookiesFromCefIntoJar();
        }

        public void Dispose()
        {
            _http?.Dispose();
            _handler?.Dispose();
            _apiHttp?.Dispose();
            _apiHandler?.Dispose();
        }

        // ---------------------------------------------------------------------
        // Session Management
        // ---------------------------------------------------------------------

        public Task<bool> RefreshCookiesHeadlessAsync(CancellationToken ct, bool force = false) =>
            _sessionManager.RefreshCookiesHeadlessAsync(ct, force);

        public Task<(bool Success, string Message)> AuthenticateInteractiveAsync(CancellationToken ct) => 
            _sessionManager.AuthenticateInteractiveAsync(ct);

        public void ClearSavedCookies()
        {
            _sessionManager.ClearSession();
            BuildHttpClientsOnce();
        }

        public Task<string> GetRequiredSelfSteamId64Async(CancellationToken ct) =>
            _sessionManager.GetRequiredSelfSteamId64Async(ct);

        private async Task<bool> EnsureSessionAsync(CancellationToken ct, bool forceRefresh = false)
        {
            ct.ThrowIfCancellationRequested();

            if (!forceRefresh && !_sessionManager.NeedsRefresh)
            {
                lock (_cookieLock)
                {
                    if (HasCookiesInJar()) return true;
                }
                
                LoadCookiesFromCefIntoJar();
                lock (_cookieLock)
                {
                    if (HasCookiesInJar()) return true;
                }
            }

            _logger?.Debug($"[FAF] Refreshing Steam session (Force={forceRefresh})...");
            var refreshed = await _sessionManager.RefreshCookiesHeadlessAsync(ct).ConfigureAwait(false);

            if (refreshed)
            {
                LoadCookiesFromCefIntoJar();
            }

            return refreshed;
        }

        private void LoadCookiesFromCefIntoJar()
        {
            lock (_cookieLock)
            {
                SteamCookieManager.LoadCefCookiesIntoJar(_api, _cookieJar, _logger);
            }
        }

        private bool HasCookiesInJar()
        {
            bool Has(CookieCollection cc) =>
                cc.Cast<Cookie>().Any(c => c.Name.Equals("steamLoginSecure", StringComparison.OrdinalIgnoreCase));

            try
            {
                return Has(_cookieJar.GetCookies(CommunityBase))
                    || Has(_cookieJar.GetCookies(StoreBase));
            }
            catch { return false; }
        }

        // ---------------------------------------------------------------------
        // Steam Web API
        // ---------------------------------------------------------------------

        public async Task<Dictionary<int, int>> GetOwnedGamePlaytimesFromApiAsync(
            string apiKey, string steamId64, CancellationToken ct, bool includePlayedFreeGames = true)
        {
            if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(steamId64)) 
                return new Dictionary<int, int>();

            var url = $"https://api.steampowered.com/IPlayerService/GetOwnedGames/v1/" +
                      $"?key={Uri.EscapeDataString(apiKey)}" +
                      $"&steamid={Uri.EscapeDataString(steamId64)}" +
                      $"&include_appinfo=0" +
                      $"&include_played_free_games={(includePlayedFreeGames ? "1" : "0")}";

            try
            {
                var json = await _apiHttp.GetStringAsync(url).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(json)) return new Dictionary<int, int>();

                var env = Serialization.FromJson<OwnedGamesEnvelope>(json);
                var games = env?.Response?.Games;
                
                if (games == null) return new Dictionary<int, int>();

                var result = new Dictionary<int, int>();
                foreach (var g in games)
                {
                    if (!g.AppId.HasValue || g.AppId.Value <= 0) continue;
                    var mins = Math.Max(0, g.PlaytimeForever ?? 0);
                    
                    if (!result.ContainsKey(g.AppId.Value) || mins > result[g.AppId.Value])
                        result[g.AppId.Value] = mins;
                }
                return result;
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"[FAF] OwnedGames API request failed steamId={steamId64}");
                return new Dictionary<int, int>();
            }
        }

        // ---------------------------------------------------------------------
        // Profile / Achievements
        // ---------------------------------------------------------------------

        public async Task<(bool IsLoggedIn, string FinalUrl)> ProbeLoggedInAsync(CancellationToken ct)
        {
            var probeUrl = "https://steamcommunity.com/my/friends";
            var res = await GetSteamPageAsync(probeUrl, requiresCookies: true, ct).ConfigureAwait(false);

            var final = res?.FinalUrl ?? "";
            if (string.IsNullOrWhiteSpace(final))
                return (false, final);

            var looksLikeLogin = final.IndexOf("/login", StringComparison.OrdinalIgnoreCase) >= 0
                            || final.IndexOf("openid", StringComparison.OrdinalIgnoreCase) >= 0
                            || final.IndexOf("login.steampowered.com", StringComparison.OrdinalIgnoreCase) >= 0;

            return (!looksLikeLogin, final);
        }

        public Task<SteamPageResult> GetAchievementsPageAsync(string steamId64, int appId, CancellationToken ct)
            => GetSteamPageAsync($"https://steamcommunity.com/profiles/{steamId64}/stats/{appId}/?tab=achievements&l=english", true, ct);

        // ---------------------------------------------------------------------
        // Player Summaries
        // ---------------------------------------------------------------------

        public async Task<List<SteamPlayerSummaries>> GetPlayerSummariesAsync(string apiKey, IEnumerable<ulong> steamIds, CancellationToken ct)
        {
            var ids = steamIds?.Where(x => x > 0).Distinct().ToList() ?? new List<ulong>();
            if (ids.Count == 0) return new List<SteamPlayerSummaries>();

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                _logger?.Warn("[FAF] An API key is required to fetch Steam friend data.");
                return new List<SteamPlayerSummaries>();
            }

            var apiResults = await _apiHelper.GetPlayerSummariesAsync(apiKey, ids, ct).ConfigureAwait(false);
            return apiResults ?? new List<SteamPlayerSummaries>();
        }

        // ---------------------------------------------------------------------
        // Friends (API)
        // ---------------------------------------------------------------------

        private const ulong SteamId64Base = 76561197960265728UL; // SteamID64 = base + AccountID (miniprofile)

        private static IEnumerable<ulong> ExtractSteamIdsFromHtml(string html)
        {
            if (string.IsNullOrWhiteSpace(html)) yield break;

            // 1) Direct SteamID64 links
            foreach (Match m in Regex.Matches(html, @"/profiles/(\d{17})", RegexOptions.IgnoreCase))
            {
                if (m.Groups.Count > 1 && ulong.TryParse(m.Groups[1].Value, out var id))
                    yield return id;
            }

            // 2) data-miniprofile="<accountid>" (32-bit account id)
            foreach (Match m in Regex.Matches(
                html,
                @"data-miniprofile\s*=\s*[""'](\d{1,12})[""']",
                RegexOptions.IgnoreCase))
            {
                if (m.Groups.Count > 1 && ulong.TryParse(m.Groups[1].Value, out var accountId))
                    yield return SteamId64Base + accountId;
            }

            // 3) Embedded JSON patterns (sometimes present)
            foreach (Match m in Regex.Matches(html, @"""steamid""\s*:\s*""(\d{17})""", RegexOptions.IgnoreCase))
            {
                if (m.Groups.Count > 1 && ulong.TryParse(m.Groups[1].Value, out var id))
                    yield return id;
            }
        }

        private static List<ulong> RemoveSelfAndDistinct(IEnumerable<ulong> ids, string selfSteamId64)
        {
            var set = new HashSet<ulong>();
            ulong self = 0;
            ulong.TryParse(selfSteamId64, out self);

            if (ids != null)
            {
                foreach (var id in ids)
                {
                    if (id == 0) continue;
                    if (self != 0 && id == self) continue;
                    set.Add(id);
                }
            }

            return set.ToList();
        }

        private async Task<List<ulong>> GetMyFriendIdsByScrapingAsync(string selfSteamId64, CancellationToken ct)
        {
            // Ensure we have fresh cookies
            var ok = await EnsureSessionAsync(ct).ConfigureAwait(false);
            if (!ok) return new List<ulong>();

            // Single fetch only (no paging)
            var url = $"https://steamcommunity.com/profiles/{selfSteamId64}/friends/?l=english";
            var res = await GetSteamPageAsync(url, requiresCookies: true, ct).ConfigureAwait(false);

            if (res == null || string.IsNullOrWhiteSpace(res.Html)) return new List<ulong>();

            // If bounced to login/openid, not authenticated
            if (!string.IsNullOrEmpty(res.FinalUrl) &&
                (res.FinalUrl.IndexOf("/login", StringComparison.OrdinalIgnoreCase) >= 0 ||
                res.FinalUrl.IndexOf("openid", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                return new List<ulong>();
            }

            if (LooksLoggedOutHeader(res.Html))
                return new List<ulong>();

            return RemoveSelfAndDistinct(ExtractSteamIdsFromHtml(res.Html), selfSteamId64);
        }

        public async Task<List<ulong>> GetFriendIdsAsync(string steamId64, string apiKey, CancellationToken ct)
        {
            if (!InputValidator.HasSteamCredentials(steamId64, apiKey)) return new List<ulong>();

            var url =
                $"https://api.steampowered.com/ISteamUser/GetFriendList/v1/" +
                $"?key={Uri.EscapeDataString(apiKey)}" +
                $"&steamid={Uri.EscapeDataString(steamId64)}" +
                $"&relationship=friend";

            try
            {
                using (var resp = await _apiHttp.GetAsync(url, ct).ConfigureAwait(false))
                {
                    if (resp.IsSuccessStatusCode)
                    {
                        var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                        var root = Serialization.FromJson<FriendListResponseRoot>(json);

                        var ids = root?.FriendsList?.Friends?
                            .Where(f => string.Equals(f?.Relationship, "friend", StringComparison.OrdinalIgnoreCase))
                            .Select(f => f.SteamId)
                            .Where(s => ulong.TryParse(s, out _))
                            .Select(ulong.Parse)
                            .ToList() ?? new List<ulong>();

                        // Filter out self + distinct
                        return RemoveSelfAndDistinct(ids, steamId64);
                    }

                    if (resp.StatusCode != HttpStatusCode.Unauthorized && resp.StatusCode != HttpStatusCode.Forbidden)
                    {
                        _logger?.Debug($"[FAF] GetFriendList API non-success: {(int)resp.StatusCode} {resp.ReasonPhrase}");
                        return new List<ulong>();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "[FAF] GetFriendSteamIdsAsync API request failed. " + url);
            }

            _logger?.Debug("[FAF] GetFriendSteamIdsAsync falling back to scrape.");

            // Self-only fallback scrape
            try
            {
                var selfId64 = await _sessionManager.GetRequiredSelfSteamId64Async(ct).ConfigureAwait(false);
                if (!string.Equals(selfId64, steamId64, StringComparison.OrdinalIgnoreCase))
                    return new List<ulong>();

                return await GetMyFriendIdsByScrapingAsync(selfId64, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "[FAF] Friend scrape fallback failed.");
                return new List<ulong>();
            }
        }


        // ---------------------------------------------------------------------
        // Parsing / Schema
        // ---------------------------------------------------------------------

        public List<ScrapedAchievementRow> ParseAchievements(string html, bool includeLocked)
        {
            var safe = html ?? string.Empty;
            if (safe.Length < 200) return new List<ScrapedAchievementRow>();

            var doc = new HtmlDocument();
            doc.LoadHtml(safe);

            var nodes = doc.DocumentNode.SelectNodes("//div[contains(@class,'achieveRow')]") ??
                        doc.DocumentNode.SelectNodes("//*[contains(@class,'achievement') and (.//h3 or .//div[contains(@class,'achieveUnlockTime')])]");

            if (nodes == null || nodes.Count == 0) return new List<ScrapedAchievementRow>();

            var results = new List<ScrapedAchievementRow>();
            foreach (var row in nodes)
            {
                if (row.SelectSingleNode(".//div[contains(@class,'achieveHiddenBox')]") != null) continue;

                var unlockText = WebUtility.HtmlDecode(row.SelectSingleNode(".//div[contains(@class,'achieveUnlockTime')]")?.InnerText ?? "").Trim();
                var unlockUtc = SteamTimeParser.TryParseSteamUnlockTimeEnglishToUtc(unlockText);

                if (!includeLocked && !unlockUtc.HasValue) continue;

                var title = WebUtility.HtmlDecode(row.SelectSingleNode(".//h3")?.InnerText ?? "").Trim();
                var desc = WebUtility.HtmlDecode(row.SelectSingleNode(".//h5")?.InnerText ?? "").Trim();
                var img = row.SelectSingleNode(".//div[contains(@class,'achieveImgHolder')]//img") ??
                          row.SelectSingleNode(".//img");
                var iconUrl = img?.GetAttributeValue("src", "")?.Trim() ?? "";

                var keyBasisA = !string.IsNullOrWhiteSpace(title) ? title : iconUrl;
                var keyBasisB = !string.IsNullOrWhiteSpace(desc) ? desc : (unlockUtc.HasValue ? unlockUtc.Value.ToString("O") : "");

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

        public Task<bool?> GetAppHasAchievementsAsync(string apiKey, int appId, CancellationToken ct)
        {
            if (appId <= 0 || string.IsNullOrWhiteSpace(apiKey)) return Task.FromResult<bool?>(false);
            
            return _hasAchievementsCache.GetOrAdd(appId, 
                new Lazy<Task<bool?>>(() => FetchAppHasAchievementsAsync(apiKey, appId, ct))).Value;
        }

        private async Task<bool?> FetchAppHasAchievementsAsync(string apiKey, int appId, CancellationToken ct)
        {
            try
            {
                var url = $"https://api.steampowered.com/ISteamUserStats/GetSchemaForGame/v2/?key={apiKey}&appid={appId}&l=english";
                var json = await _http.GetStringAsync(url).ConfigureAwait(false);
                var root = Serialization.FromJson<SchemaRoot>(json);
                var ach = root?.Response?.Game?.AvailableGameStats?.Achievements;
                return (ach != null && ach.Length > 0);
            }
            catch 
            { 
                return false; 
            }
        }

        // ---------------------------------------------------------------------
        // HTTP Core
        // ---------------------------------------------------------------------

        private async Task<SteamPageResult> GetSteamPageAsync(string url, bool requiresCookies, CancellationToken ct)
        {
            var result = new SteamPageResult { RequestedUrl = url, FinalUrl = url, Html = "", StatusCode = 0, WasRedirected = false };
            if (string.IsNullOrWhiteSpace(url)) return result;

            Uri uri;
            try { uri = new Uri(url); } catch { return result; }

            bool isSteamAuth = uri.Host.EndsWith("steamcommunity.com", StringComparison.OrdinalIgnoreCase) ||
                               uri.Host.EndsWith("steampowered.com", StringComparison.OrdinalIgnoreCase);

            if (requiresCookies && isSteamAuth)
            {
                await EnsureSessionAsync(ct).ConfigureAwait(false);
            }

            for (int attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();

                using (var req = new HttpRequestMessage(HttpMethod.Get, uri))
                {
                    req.Headers.TryAddWithoutValidation("User-Agent", DefaultUserAgent);
                    req.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
                    
                    if (isSteamAuth)
                        req.Headers.Referrer = uri.Host.Contains("store") ? StoreBase : CommunityBase;

                    try
                    {
                        using (var resp = await _http.SendAsync(req, ct).ConfigureAwait(false))
                        {
                            result.StatusCode = resp.StatusCode;
                            result.FinalUrl = resp.RequestMessage?.RequestUri?.ToString() ?? url;
                            result.WasRedirected = !string.Equals(result.FinalUrl, url, StringComparison.OrdinalIgnoreCase);

                            if (requiresCookies && isSteamAuth && attempt == 1)
                            {
                                bool softRedirect = result.FinalUrl.Contains("/login") || result.FinalUrl.Contains("openid");
                                if (resp.StatusCode == HttpStatusCode.Unauthorized || resp.StatusCode == HttpStatusCode.Forbidden || softRedirect)
                                {
                                    _logger.Warn($"[FAF] Auth failed for {url} (Status={resp.StatusCode}, Url={result.FinalUrl}). Forcing session refresh.");
                                    await EnsureSessionAsync(ct, forceRefresh: true).ConfigureAwait(false);
                                    continue; 
                                }
                            }

                            result.Html = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                            return result;
                        }
                    }
                    catch (Exception)
                    {
                        if (attempt < MaxAttempts) continue;
                        return result;
                    }
                }
            }
            return result;
        }

        private void BuildHttpClientsOnce()
        {
            _handler?.Dispose(); _http?.Dispose();
            _apiHandler?.Dispose(); _apiHttp?.Dispose();

            _cookieJar.PerDomainCapacity = 300;

            _handler = new HttpClientHandler
            {
                CookieContainer = _cookieJar,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                AllowAutoRedirect = true,
                UseCookies = true
            };
            _http = new HttpClient(_handler) { Timeout = TimeSpan.FromSeconds(30) };

            _apiHandler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                AllowAutoRedirect = true,
                UseCookies = false 
            };
            _apiHttp = new HttpClient(_apiHandler) { Timeout = TimeSpan.FromSeconds(30) };
        }

        // ---------------------------------------------------------------------
        // Static Helpers (Restored)
        // ---------------------------------------------------------------------

        public static bool LooksLoggedOutHeader(string html)
        {
            if (string.IsNullOrWhiteSpace(html)) return false;
            
            if (html.IndexOf("global_action_menu", StringComparison.OrdinalIgnoreCase) >= 0)
                return false;

            if (Regex.IsMatch(html, @"<a[^>]+class\s*=\s*[""'][^""']*\bglobal_action_link\b[^""']*[""'][^>]+href\s*=\s*[""'][^""']*/login[^""']*[""']", RegexOptions.IgnoreCase | RegexOptions.Singleline))
                return true;

            return html.IndexOf("Sign In", StringComparison.OrdinalIgnoreCase) >= 0 &&
                   html.IndexOf("/login", StringComparison.OrdinalIgnoreCase) >= 0 &&
                   html.IndexOf("global_action", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static bool HasAnyAchievementRows(string html)
        {
            if (string.IsNullOrWhiteSpace(html)) return false;
            return html.IndexOf("achievements_list", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   html.IndexOf("achieveRow", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   html.IndexOf("achieveImgHolder", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}