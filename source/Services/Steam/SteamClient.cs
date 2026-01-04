// SteamClient.cs

using HtmlAgilityPack;
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
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using FriendsAchievementFeed.Services.Steam;

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

    // -------------------------------------------------------------------------
    // Steam Session Data (persisted for self Steam ID only - cookies are in CEF)
    // -------------------------------------------------------------------------

    [DataContract]
    internal sealed class SteamSessionData
    {
        [DataMember] public DateTime LastValidatedUtc { get; set; }
        [DataMember] public string SelfSteamId64 { get; set; }
    }

    internal sealed class SteamSessionStore
    {
        private readonly string _filePath;

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
            if (!File.Exists(_filePath))
                return false;

            try
            {
                var json = File.ReadAllText(_filePath, Encoding.UTF8);
                session = Serialization.FromJson<SteamSessionData>(json);
                return session != null && !string.IsNullOrWhiteSpace(session.SelfSteamId64);
            }
            catch
            {
                session = null;
                return false;
            }
        }

        public void Save(SteamSessionData session)
        {
            if (session == null || string.IsNullOrWhiteSpace(session.SelfSteamId64))
                return;

            try
            {
                var json = Serialization.ToJson(session);
                var tmp = _filePath + ".tmp";
                File.WriteAllText(tmp, json, Encoding.UTF8);
                
                if (File.Exists(_filePath))
                    File.Delete(_filePath);
                File.Move(tmp, _filePath);
            }
            catch (Exception ex)
            {
                // Log but don't crash
                System.Diagnostics.Debug.WriteLine($"Failed to save Steam session: {ex.Message}");
            }
        }

        public void Clear()
        {
            try
            {
                if (File.Exists(_filePath))
                    File.Delete(_filePath);
            }
            catch
            {
                /* ignore */
            }
        }
    }

    internal sealed class SteamClient : IDisposable
    {
        private static readonly Uri CommunityBase = new Uri("https://steamcommunity.com/");
        private static readonly Uri StoreBase = new Uri("https://store.steampowered.com/");

        private const string DefaultUserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

        private const int MaxAttempts = 3;
        private const int PlayerSummariesBatchSize = 100; // Steam API limit per call

        // Session validation intervals
        private static readonly TimeSpan SessionCheckInterval = TimeSpan.FromMinutes(30);
        private static readonly TimeSpan CookieRefreshInterval = TimeSpan.FromHours(6);

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
        private readonly SteamSessionStore _sessionStore;
        private readonly SteamApiHelper _apiHelper;

        private readonly CookieContainer _cookieJar = new CookieContainer();
        private readonly object _cookieLock = new object();

        private HttpClient _http;
        private HttpClientHandler _handler;

        private HttpClient _apiHttp;
        private HttpClientHandler _apiHandler;

        private string _selfSteamId64;
        private DateTime _lastSessionCheckUtc = DateTime.MinValue;
        private DateTime _lastCookieRefreshUtc = DateTime.MinValue;

        public SteamClient(IPlayniteAPI api, ILogger logger, string pluginUserDataPath)
        {
            _api = api ?? throw new ArgumentNullException(nameof(api));
            _logger = logger;

            if (string.IsNullOrWhiteSpace(pluginUserDataPath))
                throw new ArgumentNullException(nameof(pluginUserDataPath));

            _sessionStore = new SteamSessionStore(pluginUserDataPath);

            BuildHttpClientsOnce();
            _apiHelper = new SteamApiHelper(_apiHttp, _logger);
            
            LoadSessionData();
            
            // Load cookies from CEF on startup for immediate use
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
        // Session Management (using CEF's persistent cookie system)
        // ---------------------------------------------------------------------

        private void LoadSessionData()
        {
            if (_sessionStore.TryLoad(out var session))
            {
                _selfSteamId64 = session.SelfSteamId64;
                _lastSessionCheckUtc = session.LastValidatedUtc;
            }
        }

        private void SaveSessionData()
        {
            if (string.IsNullOrWhiteSpace(_selfSteamId64))
                return;

            _sessionStore.Save(new SteamSessionData
            {
                SelfSteamId64 = _selfSteamId64,
                LastValidatedUtc = DateTime.UtcNow
            });
        }

        /// <summary>
        /// Check if logged in by validating session cookies exist in CEF's cookie store
        /// </summary>
        private bool HasSteamSessionCookies()
        {
            return SteamCookieManager.HasSteamSessionCookies(_api, _logger);
        }

        /// <summary>
        /// Headless refresh: load a Steam page in an offscreen WebView so session cookies are automatically
        /// updated by the website and persisted by CEF. No manual cookie handling needed.
        /// </summary>
        public async Task<bool> RefreshCookiesHeadlessAsync(CancellationToken ct)
        {
            try
            {
                ct.ThrowIfCancellationRequested();

                bool success = false;
                string extractedId = null;

                await _api.MainView.UIDispatcher.InvokeAsync(async () =>
                {
                    try
                    {
                        // Use standard CreateOffscreenView() - available in Playnite 9+
                        using (var view = _api.WebViews.CreateOffscreenView())
                        {
                            // Navigate to Steam page - this will use existing cookies and refresh them
                            view.Navigate("https://store.steampowered.com/explore/");
                            await Task.Delay(2000, ct); // Give time for page load and cookie refresh

                            // Extract Steam ID from current cookies
                            var cookies = view.GetCookies();
                            if (cookies != null)
                            {
                                var steamLogin = cookies.FirstOrDefault(c =>
                                    c != null &&
                                    string.Equals(c.Name, "steamLoginSecure", StringComparison.OrdinalIgnoreCase));

                                if (steamLogin != null)
                                {
                                    extractedId = SteamCookieManager.TryExtractSteamId64FromSteamLoginSecure(steamLogin.Value);
                                    success = true;
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.Debug(ex, "[FAF] CreateOffscreenView failed - may not be available in this Playnite version.");
                    }
                });

                if (success && !string.IsNullOrWhiteSpace(extractedId))
                {
                    _selfSteamId64 = extractedId;
                    _lastCookieRefreshUtc = DateTime.UtcNow;
                    _lastSessionCheckUtc = DateTime.UtcNow;
                    SaveSessionData();
                }

                return success;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "[FAF] Headless cookie refresh failed.");
                return false;
            }
        }

        /// <summary>
        /// User-initiated: opens a WebView dialog, user logs in (Steam Guard), cookies are automatically
        /// persisted by CEF.
        /// </summary>
        public async Task<(bool Success, string Message)> AuthenticateInteractiveAsync(CancellationToken ct)
        {
            try
            {
                ct.ThrowIfCancellationRequested();

                bool loggedIn = false;
                string extractedId = null;

                await _api.MainView.UIDispatcher.InvokeAsync(async () =>
                {
                    using (var view = _api.WebViews.CreateView(1000, 800))
                    {
                        // Clear old Steam cookies
                        view.DeleteDomainCookies(".steamcommunity.com");
                        view.DeleteDomainCookies("steamcommunity.com");
                        view.DeleteDomainCookies(".steampowered.com");
                        view.DeleteDomainCookies("steampowered.com");

                        view.Navigate("https://steamcommunity.com/login/home/?goto=" +
                                      Uri.EscapeDataString("https://steamcommunity.com/my/"));

                        view.OpenDialog();

                        // Give page time to settle
                        await Task.Delay(500);

                        // Check if we got session cookies
                        var cookies = view.GetCookies();
                        if (cookies != null)
                        {
                            var steamCookies = cookies
                                .Where(c => c != null && !string.IsNullOrWhiteSpace(c.Domain))
                                .Where(c => IsSteamDomain(c.Domain))
                                .ToList();

                            if (steamCookies.Count > 0)
                            {
                                var steamLogin = steamCookies.FirstOrDefault(c =>
                                    string.Equals(c.Name, "steamLoginSecure", StringComparison.OrdinalIgnoreCase));

                                if (steamLogin != null)
                                {
                                    extractedId = SteamCookieManager.TryExtractSteamId64FromSteamLoginSecure(steamLogin.Value);
                                    loggedIn = !string.IsNullOrWhiteSpace(extractedId);
                                }
                            }
                        }
                    }
                });

                if (!loggedIn)
                    return (false, "Steam session cookies not found. Please ensure login completed.");

                _selfSteamId64 = extractedId;
                _lastCookieRefreshUtc = DateTime.UtcNow;
                _lastSessionCheckUtc = DateTime.UtcNow;
                SaveSessionData();

                return (true, "Steam authentication saved.");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger?.Error(ex, "[FAF] AuthenticateInteractiveAsync failed.");
                return (false, ex.Message);
            }
        }

        public void ClearSavedCookies()
        {
            // Clear cookies from CEF
            SteamCookieManager.ClearSteamCookiesFromCef(_api, _logger);

            _sessionStore.Clear();
            _selfSteamId64 = null;
            _lastSessionCheckUtc = DateTime.MinValue;
            _lastCookieRefreshUtc = DateTime.MinValue;

            BuildHttpClientsOnce();
        }

        public async Task<string> GetSelfSteamId64Async(CancellationToken ct)
        {
            // Return cached if recent
            if (!string.IsNullOrWhiteSpace(_selfSteamId64) &&
                (DateTime.UtcNow - _lastSessionCheckUtc) < SessionCheckInterval)
            {
                return _selfSteamId64;
            }

            // Try to refresh from cookies
            if (!HasSteamSessionCookies())
            {
                _selfSteamId64 = null;
                return null;
            }

            // Extract Steam ID from CEF cookies
            try
            {
                using (var view = _api.WebViews.CreateOffscreenView())
                {
                    var cookies = view.GetCookies();
                    if (cookies != null)
                    {
                        var steamLogin = cookies.FirstOrDefault(c =>
                            c != null &&
                            string.Equals(c.Name, "steamLoginSecure", StringComparison.OrdinalIgnoreCase));

                        if (steamLogin != null)
                        {
                            var id = SteamCookieManager.TryExtractSteamId64FromSteamLoginSecure(steamLogin.Value);
                            if (!string.IsNullOrWhiteSpace(id))
                            {
                                _selfSteamId64 = id;
                                _lastSessionCheckUtc = DateTime.UtcNow;
                                SaveSessionData();
                                return id;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "[FAF] Failed to extract Steam ID from cookies.");
            }

            // Try refreshing headlessly if we don't have ID
            if (string.IsNullOrWhiteSpace(_selfSteamId64))
            {
                await RefreshCookiesHeadlessAsync(ct).ConfigureAwait(false);
            }

            return _selfSteamId64;
        }

        /// <summary>
        /// Ensures session is valid, refreshing if needed and loading cookies into HttpClient
        /// </summary>
        private async Task<bool> EnsureSessionAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            // Check if we need to refresh
            var timeSinceRefresh = DateTime.UtcNow - _lastCookieRefreshUtc;
            var needsRefresh = timeSinceRefresh >= CookieRefreshInterval;

            if (!needsRefresh)
            {
                // Cookies are recent, just ensure they're loaded in HttpClient
                lock (_cookieLock)
                {
                    if (HasCookiesInJar())
                        return true;
                }
            }

            // Need to refresh: use WebView to get fresh cookies from CEF
            _logger?.Debug($"[FAF] Refreshing Steam session (time since last refresh: {timeSinceRefresh})");
            var refreshed = await RefreshCookiesHeadlessAsync(ct).ConfigureAwait(false);
            
            if (refreshed)
            {
                // Load cookies from CEF into HttpClient for fast requests
                LoadCookiesFromCefIntoJar();
            }
            
            return refreshed;
        }

        /// <summary>
        /// Loads current CEF cookies into HttpClient's CookieContainer for fast requests.
        /// Also ensures timezoneOffset cookie is set to Pacific Time to match Steam's default.
        /// </summary>
        private void LoadCookiesFromCefIntoJar()
        {
            lock (_cookieLock)
            {
                SteamCookieManager.LoadCefCookiesIntoJar(_api, _cookieJar, _logger);
            }
        }

        private bool HasCookiesInJar()
        {
            try
            {
                var communityCookies = _cookieJar.GetCookies(CommunityBase);
                var storeCookies = _cookieJar.GetCookies(StoreBase);

                return communityCookies?.Count > 0 || storeCookies?.Count > 0;
            }
            catch
            {
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

        private static Uri GetAddUriForDomain(string cookieDomain)
        {
            var d = (cookieDomain ?? "").Trim().TrimStart('.');
            if (d.EndsWith("steamcommunity.com", StringComparison.OrdinalIgnoreCase))
                return CommunityBase;
            if (d.EndsWith("steampowered.com", StringComparison.OrdinalIgnoreCase))
                return StoreBase;
            return new Uri("https://" + d);
        }

        private static string TryExtractSteamId64FromSteamLoginSecure(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;

            string decoded;
            try { decoded = Uri.UnescapeDataString(value); }
            catch (Exception)
            {
                // Malformed cookie value; use as-is
                decoded = value;
            }

            var m = Regex.Match(decoded, @"(?<id>\d{17})");
            return m.Success ? m.Groups["id"].Value : null;
        }

        // ---------------------------------------------------------------------
        // Steam Web API: Owned games playtimes + schema
        // ---------------------------------------------------------------------

        [DataContract]
        private sealed class OwnedGamesEnvelope
        {
            [DataMember(Name = "response")]
            public OwnedGamesResponse Response { get; set; }
        }

        [DataContract]
        private sealed class OwnedGamesResponse
        {
            [DataMember(Name = "games")]
            public List<OwnedGame> Games { get; set; }
        }

        [DataContract]
        private sealed class OwnedGame
        {
            [DataMember(Name = "appid")]
            public int AppId { get; set; }

            [DataMember(Name = "playtime_forever")]
            public int PlaytimeForever { get; set; }
        }

        [DataContract]
        private sealed class SchemaRoot
        {
            [DataMember(Name = "response")]
            public SchemaResponse Response { get; set; }
        }

        [DataContract]
        private sealed class SchemaResponse
        {
            [DataMember(Name = "game")]
            public SchemaGame Game { get; set; }
        }

        [DataContract]
        private sealed class SchemaGame
        {
            [DataMember(Name = "availableGameStats")]
            public SchemaAvailableGameStats AvailableGameStats { get; set; }
        }

        [DataContract]
        private sealed class SchemaAvailableGameStats
        {
            [DataMember(Name = "achievements")]
            public SchemaAchievement[] Achievements { get; set; }
        }

        [DataContract]
        private sealed class SchemaAchievement
        {
            [DataMember(Name = "name")]
            public string Name { get; set; }
        }

        public async Task<Dictionary<int, int>> GetOwnedGamePlaytimesFromApiAsync(
            string apiKey,
            string steamId64,
            CancellationToken ct,
            bool includePlayedFreeGames = true)
        {
            if (string.IsNullOrWhiteSpace(apiKey)) return new Dictionary<int, int>();
            if (string.IsNullOrWhiteSpace(steamId64) || !ulong.TryParse(steamId64, out _)) return new Dictionary<int, int>();

            var url =
                "https://api.steampowered.com/IPlayerService/GetOwnedGames/v1/" +
                $"?key={Uri.EscapeDataString(apiKey)}" +
                $"&steamid={Uri.EscapeDataString(steamId64)}" +
                $"&include_appinfo=0" +
                $"&include_played_free_games={(includePlayedFreeGames ? "1" : "0")}";

            try
            {
                using (var req = new HttpRequestMessage(HttpMethod.Get, url))
                {
                    req.Headers.TryAddWithoutValidation("User-Agent", DefaultUserAgent);
                    req.Headers.TryAddWithoutValidation("Accept", "application/json");

                    using (var resp = await _apiHttp.SendAsync(req, ct).ConfigureAwait(false))
                    {
                        if (!resp.IsSuccessStatusCode)
                            return new Dictionary<int, int>();

                        var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                        return ParseOwnedGamesJsonToPlaytimes(json);
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"[FAF] OwnedGames API request failed steamId={steamId64}");
                return new Dictionary<int, int>();
            }
        }

        private static Dictionary<int, int> ParseOwnedGamesJsonToPlaytimes(string json)
        {
            var result = new Dictionary<int, int>();
            if (string.IsNullOrWhiteSpace(json)) return result;

            try
            {
                var ser = new DataContractJsonSerializer(typeof(OwnedGamesEnvelope));
                using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                {
                    var env = ser.ReadObject(ms) as OwnedGamesEnvelope;
                    var games = env?.Response?.Games;
                    if (games == null || games.Count == 0) return result;

                    foreach (var g in games)
                    {
                        if (g == null || g.AppId <= 0) continue;

                        var mins = g.PlaytimeForever;
                        if (mins < 0) mins = 0;

                        if (!result.TryGetValue(g.AppId, out var existing) || mins > existing)
                            result[g.AppId] = mins;
                    }
                }
            }
            catch
            {
                return new Dictionary<int, int>();
            }

            return result;
        }

        // ---------------------------------------------------------------------
        // Profile / Achievements
        // ---------------------------------------------------------------------

        public Task<SteamPageResult> GetProfilePageAsync(string steamId64, CancellationToken ct)
        {
            var url = $"https://steamcommunity.com/profiles/{steamId64}/?l=english";
            return GetSteamPageAsync(url, requiresCookies: true, ct);
        }

        public async Task<string> GetProfileHtmlAsync(string steamId64, CancellationToken ct)
        {
            var page = await GetProfilePageAsync(steamId64, ct).ConfigureAwait(false);
            return page?.Html ?? string.Empty;
        }

        public Task<SteamPageResult> GetAchievementsPageAsync(string steamId64, int appId, CancellationToken ct)
        {
            var url = $"https://steamcommunity.com/profiles/{steamId64}/stats/{appId}/?tab=achievements&l=english";
            return GetSteamPageAsync(url, requiresCookies: true, ct);
        }

        // ---------------------------------------------------------------------
        // Player Summaries
        // ---------------------------------------------------------------------

        // Back-compat overload: old callers still work, but will do HTML fallback.
        public Task<List<SteamPlayerSummaries>> GetPlayerSummariesAsync(List<ulong> steamIds, CancellationToken ct)
            => GetPlayerSummariesAsync(apiKey: null, steamIds: steamIds, ct: ct);

        public async Task<List<SteamPlayerSummaries>> GetPlayerSummariesAsync(string apiKey, IEnumerable<ulong> steamIds, CancellationToken ct)
        {
            var ids = steamIds?.
                Where(x => x > 0)
                .Distinct()
                .ToList() ?? new List<ulong>();

            if (ids.Count == 0)
                return new List<SteamPlayerSummaries>();

            // If no API key, fall back to HTML (slower).
            if (string.IsNullOrWhiteSpace(apiKey))
                return await GetPlayerSummariesFromHtmlAsync(ids, ct).ConfigureAwait(false);

            // Prefer API via helper
            var apiResults = await _apiHelper.GetPlayerSummariesAsync(apiKey, ids, ct).ConfigureAwait(false);
            
            // If API returned nothing at all, do a last-resort HTML fallback
            if (apiResults.Count == 0)
                return await GetPlayerSummariesFromHtmlAsync(ids, ct).ConfigureAwait(false);

            return apiResults;
        }

        private static IEnumerable<List<ulong>> Batch(IReadOnlyList<ulong> ids, int batchSize)
        {
            for (int i = 0; i < ids.Count; i += batchSize)
            {
                var size = Math.Min(batchSize, ids.Count - i);
                var chunk = new List<ulong>(size);
                for (int j = 0; j < size; j++)
                    chunk.Add(ids[i + j]);
                yield return chunk;
            }
        }

        private async Task<List<SteamPlayerSummaries>> GetPlayerSummariesFromHtmlAsync(IReadOnlyList<ulong> steamIds, CancellationToken ct)
        {
            var results = new List<SteamPlayerSummaries>();
            if (steamIds == null || steamIds.Count == 0) return results;

            foreach (var id in steamIds)
            {
                ct.ThrowIfCancellationRequested();
                var html = await GetProfileHtmlAsync(id.ToString(), ct).ConfigureAwait(false);
                var p = TryParseProfileHtmlToSummary(id, html);
                if (p != null) results.Add(p);
            }

            return results;
        }

        private sealed class PlayerSummariesRoot
        {
            public PlayerSummariesResponse Response { get; set; }
        }

        private sealed class PlayerSummariesResponse
        {
            public List<PlayerSummaryDto> Players { get; set; }
        }

        private sealed class PlayerSummaryDto
        {
            public string SteamId { get; set; }        // steamid
            public string PersonaName { get; set; }    // personaname
            public string Avatar { get; set; }         // avatar
            public string AvatarMedium { get; set; }   // avatarmedium
            public string AvatarFull { get; set; }     // avatarfull
        }

        // ---------------------------------------------------------------------
        // Friends (API)
        // ---------------------------------------------------------------------

        public Task<List<ulong>> GetFriendIdsAsync(string steamId64, string apiKey, CancellationToken ct)
            => GetFriendSteamIdsAsync(steamId64, apiKey, ct);

        [DataContract]
        private sealed class FriendListResponseRoot
        {
            [DataMember(Name = "friendslist")]
            public FriendList FriendsList { get; set; }
        }

        [DataContract]
        private sealed class FriendList
        {
            [DataMember(Name = "friends")]
            public List<FriendEntry> Friends { get; set; }
        }

        [DataContract]
        private sealed class FriendEntry
        {
            [DataMember(Name = "steamid")]
            public string SteamId { get; set; }

            [DataMember(Name = "relationship")]
            public string Relationship { get; set; }
        }

        public async Task<List<ulong>> GetFriendSteamIdsAsync(string steamId64, string apiKey, CancellationToken ct)
        {
            if (!ValidationHelper.HasSteamCredentials(steamId64, apiKey))
                return new List<ulong>();

            try
            {
                var url =
                    "https://api.steampowered.com/ISteamUser/GetFriendList/v1/" +
                    $"?key={Uri.EscapeDataString(apiKey)}" +
                    $"&steamid={Uri.EscapeDataString(steamId64)}" +
                    $"&relationship=friend";

                using (var req = new HttpRequestMessage(HttpMethod.Get, url))
                {
                    req.Headers.TryAddWithoutValidation("User-Agent", DefaultUserAgent);
                    req.Headers.TryAddWithoutValidation("Accept", "application/json");

                    using (var resp = await _apiHttp.SendAsync(req, ct).ConfigureAwait(false))
                    {
                        if (!resp.IsSuccessStatusCode)
                            return new List<ulong>();

                        var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                        if (string.IsNullOrWhiteSpace(json))
                            return new List<ulong>();

                        var root = Serialization.FromJson<FriendListResponseRoot>(json);

                        return root?.FriendsList?.Friends?
                            .Where(f => string.Equals(f?.Relationship, "friend", StringComparison.OrdinalIgnoreCase))
                            .Select(f => f?.SteamId)
                            .Where(s => !string.IsNullOrWhiteSpace(s) && ulong.TryParse(s, out _))
                            .Select(ulong.Parse)
                            .Distinct()
                            .ToList()
                            ?? new List<ulong>();
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "[FAF] GetFriendSteamIdsAsync API request failed.");
                return new List<ulong>();
            }
        }

        // ---------------------------------------------------------------------
        // Achievements HTML parser (unchanged)
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
                if (row.SelectSingleNode(".//div[contains(@class,'achieveHiddenBox')]") != null)
                    continue;

                var unlockText = WebUtility.HtmlDecode(
                    row.SelectSingleNode(".//div[contains(@class,'achieveUnlockTime')]")?.InnerText ?? ""
                ).Trim();

                var unlockUtc = SteamTimeParser.TryParseSteamUnlockTimeEnglishToUtc(unlockText);

                if (!includeLocked && !unlockUtc.HasValue)
                    continue;

                var title = WebUtility.HtmlDecode(row.SelectSingleNode(".//h3")?.InnerText ?? "").Trim();
                var desc = WebUtility.HtmlDecode(row.SelectSingleNode(".//h5")?.InnerText ?? "").Trim();

                var img =
                    row.SelectSingleNode(".//div[contains(@class,'achieveImgHolder')]//img") ??
                    row.SelectSingleNode(".//img[contains(@src,'steamstatic') or contains(@src,'steamcdn') or contains(@src,'akamai')]") ??
                    row.SelectSingleNode(".//img");

                var iconUrl = img?.GetAttributeValue("src", "")?.Trim() ?? "";

                var keyBasisA = !string.IsNullOrWhiteSpace(title) ? title : iconUrl;
                var keyBasisB = !string.IsNullOrWhiteSpace(desc)
                    ? desc
                    : (unlockUtc.HasValue ? unlockUtc.Value.ToString("O", CultureInfo.InvariantCulture) : string.Empty);

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

        private readonly ConcurrentDictionary<int, Task<bool?>> _hasAchievementsCache =
            new ConcurrentDictionary<int, Task<bool?>>();

        public Task<bool?> GetAppHasAchievementsAsync(string apiKey, int appId, CancellationToken ct)
        {
            if (appId <= 0 || string.IsNullOrWhiteSpace(apiKey))
                return Task.FromResult<bool?>(false);

            return _hasAchievementsCache.GetOrAdd(appId, _ => FetchAppHasAchievementsAsync(apiKey, appId, ct));
        }

        private async Task<bool?> FetchAppHasAchievementsAsync(string apiKey, int appId, CancellationToken ct)
        {
            try
            {
                var url =
                    $"https://api.steampowered.com/ISteamUserStats/GetSchemaForGame/v2/?key={Uri.EscapeDataString(apiKey)}&appid={appId}&l=english";

                using (var req = new HttpRequestMessage(HttpMethod.Get, url))
                {
                    req.Headers.TryAddWithoutValidation("User-Agent", DefaultUserAgent);
                    req.Headers.TryAddWithoutValidation("Accept", "application/json");

                    using (var resp = await _http.SendAsync(req, ct).ConfigureAwait(false))
                    {
                        if (!resp.IsSuccessStatusCode)
                            return false;

                        var stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
                        if (stream == null)
                            return false;

                        var serializer = new DataContractJsonSerializer(typeof(SchemaRoot));
                        SchemaRoot root;
                        try { root = serializer.ReadObject(stream) as SchemaRoot; }
                        catch (Exception)
                        {
                            // Failed to deserialize schema response
                            return false;
                        }

                        var ach = root?.Response?.Game?.AvailableGameStats?.Achievements;
                        return (ach != null && ach.Length > 0);
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                _hasAchievementsCache.TryRemove(appId, out _);
                return false;
            }
        }

        // ---------------------------------------------------------------------
        // HTTP core (HTML)
        // ---------------------------------------------------------------------

        private async Task<string> GetSteamStringAsync(string url, bool requiresCookies, CancellationToken ct)
        {
            var page = await GetSteamPageAsync(url, requiresCookies, ct).ConfigureAwait(false);
            return page?.Html ?? string.Empty;
        }

        private async Task<SteamPageResult> GetSteamPageAsync(string url, bool requiresCookies, CancellationToken ct)
        {
            var result = new SteamPageResult
            {
                RequestedUrl = url,
                FinalUrl = url,
                Html = string.Empty,
                StatusCode = 0,
                WasRedirected = false
            };

            if (string.IsNullOrWhiteSpace(url)) return result;

            Uri uri;
            try { uri = new Uri(url); }
            catch (Exception)
            {
                // Invalid URL format
                return result;
            }

            // If cookies required, ensure session is valid (loads cookies into HttpClient)
            if (requiresCookies && uri.Host.IndexOf("steamcommunity.com", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    uri.Host.IndexOf("steampowered.com", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                await EnsureSessionAsync(ct).ConfigureAwait(false);
            }

            // Use fast HttpClient for all requests (cookies already loaded)
            for (int attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();

                using (var req = new HttpRequestMessage(HttpMethod.Get, uri))
                {
                    req.Headers.TryAddWithoutValidation("User-Agent", DefaultUserAgent);
                    req.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
                    req.Headers.Referrer = uri.Host.IndexOf("steampowered.com", StringComparison.OrdinalIgnoreCase) >= 0
                        ? StoreBase
                        : CommunityBase;

                    try
                    {
                        using (var resp = await _http.SendAsync(req, ct).ConfigureAwait(false))
                        {
                            var finalUri = resp.RequestMessage?.RequestUri;
                            var finalUrl = finalUri?.ToString() ?? url;

                            result.StatusCode = resp.StatusCode;
                            result.FinalUrl = finalUrl;
                            result.WasRedirected = finalUri != null &&
                                !string.Equals(finalUri.AbsoluteUri, uri.AbsoluteUri, StringComparison.OrdinalIgnoreCase);

                            result.Html = await resp.Content.ReadAsStringAsync().ConfigureAwait(false) ?? string.Empty;
                            return result;
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch
                    {
                        if (attempt < MaxAttempts) continue;
                        result.Html = string.Empty;
                        return result;
                    }
                }
            }

            return result;
        }

        // ---------------------------------------------------------------------
        // HTML signals
        // ---------------------------------------------------------------------

        internal static bool LooksLoggedOutHeader(string html)
        {
            if (string.IsNullOrWhiteSpace(html)) return false;

            if (html.IndexOf("global_action_menu", StringComparison.OrdinalIgnoreCase) >= 0)
                return false;

            if (Regex.IsMatch(
                html,
                @"<a[^>]+class\s*=\s*[""'][^""']*\bglobal_action_link\b[^""']*[""'][^>]+href\s*=\s*[""'][^""']*/login[^""']*[""']",
                RegexOptions.IgnoreCase | RegexOptions.Singleline))
            {
                return true;
            }

            return html.IndexOf("Sign In", StringComparison.OrdinalIgnoreCase) >= 0 &&
                   html.IndexOf("/login", StringComparison.OrdinalIgnoreCase) >= 0 &&
                   html.IndexOf("global_action", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        internal static bool HasAnyAchievementRows(string html)
        {
            if (string.IsNullOrWhiteSpace(html)) return false;

            return html.IndexOf("achievements_list", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   html.IndexOf("achieveRow", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   html.IndexOf("achieveImgHolder", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // ---------------------------------------------------------------------
        // HttpClient setup
        // ---------------------------------------------------------------------

        private void BuildHttpClientsOnce()
        {
            _handler?.Dispose();
            _http?.Dispose();
            _apiHandler?.Dispose();
            _apiHttp?.Dispose();

            // Use CookieContainer loaded from CEF for fast authenticated requests
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
        // Private parsing helpers
        // ---------------------------------------------------------------------

        private SteamPlayerSummaries TryParseProfileHtmlToSummary(ulong id, string html)
        {
            return SteamHtmlParser.TryParseProfileHtmlToSummary(id, html);
        }
    }
}
