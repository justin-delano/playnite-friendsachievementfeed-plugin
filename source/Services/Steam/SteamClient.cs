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
using System.Security.Cryptography;
using System.Text;
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

    // -------------------------------------------------------------------------
    // Persisted cookie snapshot (encrypted with DPAPI)
    // -------------------------------------------------------------------------

    [DataContract]
    internal sealed class StoredCookie
    {
        [DataMember] public string Name { get; set; }
        [DataMember] public string Value { get; set; }
        [DataMember] public string Domain { get; set; }
        [DataMember] public string Path { get; set; }
        [DataMember] public DateTime? ExpiresUtc { get; set; }
        [DataMember] public bool Secure { get; set; }
        [DataMember] public bool HttpOnly { get; set; }
    }

    [DataContract]
    internal sealed class SteamCookieSnapshot
    {
        [DataMember] public DateTime CapturedAtUtc { get; set; }
        [DataMember] public string SelfSteamId64 { get; set; }
        [DataMember] public List<StoredCookie> Cookies { get; set; } = new List<StoredCookie>();
    }

    internal sealed class SteamCookieStore
    {
        private readonly string _filePath;

        public SteamCookieStore(string pluginUserDataPath)
        {
            if (string.IsNullOrWhiteSpace(pluginUserDataPath))
                throw new ArgumentNullException(nameof(pluginUserDataPath));

            Directory.CreateDirectory(pluginUserDataPath);
            _filePath = Path.Combine(pluginUserDataPath, "steam_cookies.bin");
        }

        public bool TryLoad(out SteamCookieSnapshot snapshot)
        {
            snapshot = null;
            if (!File.Exists(_filePath))
                return false;

            try
            {
                var protectedBytes = File.ReadAllBytes(_filePath);
                var jsonBytes = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);

                var ser = new DataContractJsonSerializer(typeof(SteamCookieSnapshot));
                using (var ms = new MemoryStream(jsonBytes))
                {
                    snapshot = ser.ReadObject(ms) as SteamCookieSnapshot;
                }

                return snapshot?.Cookies?.Count > 0;
            }
            catch
            {
                snapshot = null;
                return false;
            }
        }

        public void Save(SteamCookieSnapshot snapshot)
        {
            if (snapshot?.Cookies == null || snapshot.Cookies.Count == 0)
                return;

            var ser = new DataContractJsonSerializer(typeof(SteamCookieSnapshot));
            byte[] jsonBytes;
            using (var ms = new MemoryStream())
            {
                ser.WriteObject(ms, snapshot);
                jsonBytes = ms.ToArray();
            }

            var protectedBytes = ProtectedData.Protect(jsonBytes, null, DataProtectionScope.CurrentUser);

            var tmp = _filePath + ".tmp";
            File.WriteAllBytes(tmp, protectedBytes);

            try { if (File.Exists(_filePath)) File.Delete(_filePath); } catch { /* ignore */ }
            File.Move(tmp, _filePath);
        }

        public void Clear()
        {
            try { if (File.Exists(_filePath)) File.Delete(_filePath); } catch { /* ignore */ }
        }
    }

    internal sealed class SteamClient : IDisposable
    {
        private static readonly Uri CommunityBase = new Uri("https://steamcommunity.com/");
        private static readonly Uri StoreBase = new Uri("https://store.steampowered.com/");

        private const string DefaultUserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

        private static readonly string[] SteamSessionCookieNames = { "steamLoginSecure", "sessionid" };

        // Note: leaving these defined if referenced elsewhere; otherwise safe to delete.
        private const ulong SteamId64Base = 76561197960265728UL;
        private const int FriendsPageSize = 200;
        private const int FriendsMaxPages = 200;

        private const int MaxAttempts = 3;
        private const int PlayerSummariesBatchSize = 100; // Steam API limit per call :contentReference[oaicite:2]{index=2}

        private static readonly TimeSpan CookieReloadInterval = TimeSpan.FromMinutes(20);
        private static readonly TimeSpan SelfIdRefreshInterval = TimeSpan.FromMinutes(30);

        private static readonly TimeZoneInfo SteamBaseTimeZone =
            TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");

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
        private readonly SteamCookieStore _cookieStore;

        private readonly CookieContainer _cookieJar = new CookieContainer();
        private readonly object _cookieLock = new object();

        private HttpClient _http;
        private HttpClientHandler _handler;

        private HttpClient _apiHttp;
        private HttpClientHandler _apiHandler;

        private DateTime _cookiesLoadedAtUtc = DateTime.MinValue;

        private string _selfSteamId64;
        private DateTime _selfIdLoadedAtUtc = DateTime.MinValue;

        private readonly bool _debugCookieSummary = false;

        public SteamClient(IPlayniteAPI api, ILogger logger, string pluginUserDataPath)
        {
            _api = api ?? throw new ArgumentNullException(nameof(api));
            _logger = logger;

            if (string.IsNullOrWhiteSpace(pluginUserDataPath))
                throw new ArgumentNullException(nameof(pluginUserDataPath));

            _cookieStore = new SteamCookieStore(pluginUserDataPath);

            BuildHttpClientsOnce();
            TryLoadSavedCookiesIntoJar(force: true);
        }

        public void Dispose()
        {
            _http?.Dispose();
            _handler?.Dispose();
            _apiHttp?.Dispose();
            _apiHandler?.Dispose();
        }

        // ---------------------------------------------------------------------
        // Cookies
        // ---------------------------------------------------------------------

        public Task<bool> ReloadCookiesFromDiskAsync(CancellationToken ct) => EnsureCookiesAsync(ct, force: true);

        /// <summary>
        /// User-initiated: opens a WebView dialog, user logs in (Steam Guard), then we store cookies encrypted.
        /// </summary>
        public async Task<(bool Success, string Message)> AuthenticateInteractiveAsync(CancellationToken ct)
        {
            try
            {
                ct.ThrowIfCancellationRequested();

                var snap = await CaptureCookiesFromInteractiveLoginAsync(ct).ConfigureAwait(false);
                if (snap?.Cookies == null || snap.Cookies.Count == 0)
                    return (false, "No cookies captured. Login may have been cancelled.");

                if (!SnapshotHasSessionCookie(snap))
                    return (false, "Steam session cookies not found. Please ensure login completed.");

                _cookieStore.Save(snap);

                lock (_cookieLock)
                {
                    ApplySnapshotToCookieJar_NoThrow(snap);
                }

                _cookiesLoadedAtUtc = DateTime.UtcNow;
                if (!string.IsNullOrWhiteSpace(snap.SelfSteamId64))
                {
                    _selfSteamId64 = snap.SelfSteamId64;
                    _selfIdLoadedAtUtc = DateTime.UtcNow;
                }

                if (_debugCookieSummary)
                    LogSteamCookieSummary("after interactive auth");

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
            _cookieStore.Clear();
            BuildHttpClientsOnce();

            _selfSteamId64 = null;
            _selfIdLoadedAtUtc = DateTime.MinValue;
            _cookiesLoadedAtUtc = DateTime.MinValue;
        }

        public async Task<string> GetSelfSteamId64Async(CancellationToken ct)
        {
            if (!string.IsNullOrWhiteSpace(_selfSteamId64) &&
                (DateTime.UtcNow - _selfIdLoadedAtUtc) < SelfIdRefreshInterval)
            {
                return _selfSteamId64;
            }

            await EnsureCookiesAsync(ct, force: false).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(_selfSteamId64))
            {
                _selfIdLoadedAtUtc = DateTime.UtcNow;
                return _selfSteamId64;
            }

            await EnsureCookiesAsync(ct, force: true).ConfigureAwait(false);
            _selfIdLoadedAtUtc = DateTime.UtcNow;
            return _selfSteamId64;
        }

        private async Task<bool> EnsureCookiesAsync(CancellationToken ct, bool force)
        {
            ct.ThrowIfCancellationRequested();

            var missingSession = !CookieJarHasSessionCookies();
            if (!force && !missingSession && (DateTime.UtcNow - _cookiesLoadedAtUtc) < CookieReloadInterval)
                return true;

            var ok = TryLoadSavedCookiesIntoJar(force);
            _cookiesLoadedAtUtc = DateTime.UtcNow;

            if (_debugCookieSummary)
                LogSteamCookieSummary(force ? "after forced disk reload" : "after disk reload");

            if (string.IsNullOrWhiteSpace(_selfSteamId64))
            {
                _selfSteamId64 = TryExtractSelfSteamIdFromJar();
                if (!string.IsNullOrWhiteSpace(_selfSteamId64))
                    _selfIdLoadedAtUtc = DateTime.UtcNow;
            }

            return ok;
        }

        private bool TryLoadSavedCookiesIntoJar(bool force)
        {
            if (!force && CookieJarHasSessionCookies())
                return true;

            if (!_cookieStore.TryLoad(out var snap) || snap == null)
                return CookieJarHasSessionCookies();

            if (!string.IsNullOrWhiteSpace(snap.SelfSteamId64))
                _selfSteamId64 = snap.SelfSteamId64;

            lock (_cookieLock)
            {
                ApplySnapshotToCookieJar_NoThrow(snap);
            }

            return CookieJarHasSessionCookies();
        }

        private static bool SnapshotHasSessionCookie(SteamCookieSnapshot snap)
        {
            if (snap?.Cookies == null) return false;

            foreach (var c in snap.Cookies)
            {
                if (c == null) continue;
                foreach (var name in SteamSessionCookieNames)
                {
                    if (string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }

            return false;
        }

        private void ApplySnapshotToCookieJar_NoThrow(SteamCookieSnapshot snap)
        {
            if (snap?.Cookies == null) return;

            foreach (var sc in snap.Cookies)
            {
                if (sc == null) continue;

                var name = sc.Name ?? "";
                var domain = (sc.Domain ?? "").Trim();
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(domain))
                    continue;

                if (!IsSteamDomain(domain))
                    continue;

                if (sc.ExpiresUtc.HasValue && sc.ExpiresUtc.Value <= DateTime.UtcNow.AddMinutes(-1))
                    continue;

                try
                {
                    var path = string.IsNullOrWhiteSpace(sc.Path) ? "/" : sc.Path;
                    var sysCookie = new Cookie(name, sc.Value ?? "", path)
                    {
                        Domain = domain,
                        Secure = sc.Secure,
                        HttpOnly = sc.HttpOnly
                    };

                    if (sc.ExpiresUtc.HasValue)
                        sysCookie.Expires = sc.ExpiresUtc.Value;

                    _cookieJar.Add(GetAddUriForDomain(domain), sysCookie);
                }
                catch
                {
                    // ignore individual cookie issues
                }
            }
        }

        private string TryExtractSelfSteamIdFromJar()
        {
            try
            {
                return ExtractSelfId(_cookieJar.GetCookies(CommunityBase)) ??
                       ExtractSelfId(_cookieJar.GetCookies(StoreBase));
            }
            catch
            {
                return null;
            }
        }

        private static string ExtractSelfId(CookieCollection cookies)
        {
            if (cookies == null || cookies.Count == 0) return null;

            foreach (Cookie c in cookies)
            {
                if (c == null) continue;
                if (!c.Name.Equals("steamLoginSecure", StringComparison.OrdinalIgnoreCase)) continue;

                var id = TryExtractSteamId64FromSteamLoginSecure(c.Value);
                if (!string.IsNullOrWhiteSpace(id))
                    return id;
            }

            return null;
        }

        private async Task<SteamCookieSnapshot> CaptureCookiesFromInteractiveLoginAsync(CancellationToken ct)
        {
            SteamCookieSnapshot snap = null;

            await _api.MainView.UIDispatcher.InvokeAsync(() =>
            {
                using (var view = _api.WebViews.CreateView(1000, 800))
                {
                    view.Navigate("https://steamcommunity.com/login/home/?goto=" +
                                  Uri.EscapeDataString("https://steamcommunity.com/my/"));

                    view.OpenDialog();

                    var cookies = view.GetCookies();
                    if (cookies == null || !cookies.Any())
                        return;

                    var steamCookies = cookies
                        .Where(c => c != null)
                        .Where(c => !string.IsNullOrWhiteSpace(c.Domain))
                        .Where(c => IsSteamDomain(c.Domain))
                        .Where(c => !string.Equals(c.Name, "timezoneOffset", StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    if (steamCookies.Count == 0)
                        return;

                    snap = new SteamCookieSnapshot
                    {
                        CapturedAtUtc = DateTime.UtcNow,
                        Cookies = steamCookies.Select(ToStoredCookie).ToList()
                    };

                    var sls = snap.Cookies.FirstOrDefault(x =>
                        x.Name != null && x.Name.Equals("steamLoginSecure", StringComparison.OrdinalIgnoreCase));

                    snap.SelfSteamId64 = TryExtractSteamId64FromSteamLoginSecure(sls?.Value);
                }
            });

            return snap;
        }

        // IMPORTANT: no dynamic here (fixes CS0656 binder error)
        private static StoredCookie ToStoredCookie(HttpCookie c) // Playnite.SDK.HttpCookie :contentReference[oaicite:3]{index=3}
        {
            DateTime? expUtc = null;
            if (c.Expires.HasValue)
            {
                var e = c.Expires.Value;
                expUtc = e.Kind == DateTimeKind.Local ? e.ToUniversalTime()
                     : e.Kind == DateTimeKind.Utc ? e
                     : DateTime.SpecifyKind(e, DateTimeKind.Utc);
            }

            return new StoredCookie
            {
                Name = c.Name,
                Value = c.Value,
                Domain = c.Domain,
                Path = string.IsNullOrWhiteSpace(c.Path) ? "/" : c.Path,
                Secure = c.Secure,
                HttpOnly = c.HttpOnly,
                ExpiresUtc = expUtc
            };
        }

        private bool CookieJarHasSessionCookies()
        {
            try
            {
                return HasAnySessionCookie(_cookieJar.GetCookies(CommunityBase)) ||
                       HasAnySessionCookie(_cookieJar.GetCookies(StoreBase));
            }
            catch
            {
                return false;
            }
        }

        private static bool HasAnySessionCookie(CookieCollection cookies)
        {
            if (cookies == null || cookies.Count == 0) return false;

            foreach (Cookie c in cookies)
            {
                if (c == null) continue;
                foreach (var name in SteamSessionCookieNames)
                {
                    if (string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            return false;
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
            catch { decoded = value; }

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
            var ids = steamIds?
                .Where(x => x > 0)
                .Distinct()
                .ToList() ?? new List<ulong>();

            if (ids.Count == 0)
                return new List<SteamPlayerSummaries>();

            // If no API key, fall back to HTML (slower).
            if (string.IsNullOrWhiteSpace(apiKey))
                return await GetPlayerSummariesFromHtmlAsync(ids, ct).ConfigureAwait(false);

            // Prefer API: one call per 100 ids.
            var byId = new Dictionary<ulong, SteamPlayerSummaries>();

            foreach (var batch in Batch(ids, PlayerSummariesBatchSize))
            {
                ct.ThrowIfCancellationRequested();

                var idParam = string.Join(",", batch);
                var url =
                    "https://api.steampowered.com/ISteamUser/GetPlayerSummaries/v2/" +
                    $"?key={Uri.EscapeDataString(apiKey.Trim())}" +
                    $"&steamids={Uri.EscapeDataString(idParam)}";

                try
                {
                    using (var req = new HttpRequestMessage(HttpMethod.Get, url))
                    {
                        req.Headers.TryAddWithoutValidation("User-Agent", DefaultUserAgent);
                        req.Headers.TryAddWithoutValidation("Accept", "application/json");

                        using (var resp = await _apiHttp.SendAsync(req, ct).ConfigureAwait(false))
                        {
                            if (!resp.IsSuccessStatusCode)
                                continue;

                            var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                            if (string.IsNullOrWhiteSpace(json))
                                continue;

                            var root = Serialization.FromJson<PlayerSummariesRoot>(json);
                            var players = root?.Response?.Players;
                            if (players == null || players.Count == 0)
                                continue;

                            foreach (var p in players)
                            {
                                if (p == null) continue;
                                if (!ulong.TryParse(p.SteamId, out var sid) || sid <= 0) continue;

                                byId[sid] = new SteamPlayerSummaries
                                {
                                    SteamId = p.SteamId,
                                    PersonaName = p.PersonaName,
                                    Avatar = p.Avatar,
                                    AvatarMedium = p.AvatarMedium,
                                    AvatarFull = p.AvatarFull
                                };
                            }
                        }
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger?.Debug(ex, "[FAF] GetPlayerSummaries API request failed (batch).");
                }
            }

            // Preserve original order where possible.
            var ordered = new List<SteamPlayerSummaries>(ids.Count);
            foreach (var id in ids)
            {
                if (byId.TryGetValue(id, out var s) && s != null)
                    ordered.Add(s);
            }

            // If the API returned nothing at all, do a last-resort HTML fallback (optional).
            if (ordered.Count == 0)
                return await GetPlayerSummariesFromHtmlAsync(ids, ct).ConfigureAwait(false);

            return ordered;
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
            if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(steamId64))
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

                var unlockUtc = TryParseSteamUnlockTimeEnglishToUtc(unlockText);

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
                        catch { return false; }

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
            catch { return result; }

            if (requiresCookies && IsSteamCookieHost(uri))
                await EnsureCookiesAsync(ct, force: false).ConfigureAwait(false);

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

        private static bool IsSteamCookieHost(Uri uri)
        {
            if (uri == null) return false;
            return uri.Host.IndexOf("steamcommunity.com", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   uri.Host.IndexOf("steampowered.com", StringComparison.OrdinalIgnoreCase) >= 0;
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

            if (_debugCookieSummary)
                LogSteamCookieSummary("initial");
        }

        private void LogSteamCookieSummary(string when)
        {
            string Summ(CookieCollection cc)
            {
                if (cc == null) return "<null>";
                var names = cc.Cast<Cookie>().Select(c => c.Name).Distinct().OrderBy(x => x).ToList();
                return $"{cc.Count} [{string.Join(", ", names)}]";
            }

            _logger.Info($"[SteamHtml] Cookies ({when}) steamcommunity: {Summ(_cookieJar.GetCookies(CommunityBase))}");
            _logger.Info($"[SteamHtml] Cookies ({when}) steampowered(store): {Summ(_cookieJar.GetCookies(StoreBase))}");
        }

        // ---------------------------------------------------------------------
        // Private parsing helpers
        // ---------------------------------------------------------------------

        private SteamPlayerSummaries TryParseProfileHtmlToSummary(ulong id, string html)
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

        private static DateTime? TryParseSteamUnlockTimeEnglishToUtc(string text)
        {
            var flat = Regex.Replace(text ?? "", @"\s+", " ").Trim();
            if (flat.Length == 0) return null;

            var m = Regex.Match(
                flat,
                @"Unlocked\s+(?<date>.+?)\s+@\s+(?<time>\d{1,2}:\d{2}\s*(?:am|pm)?)",
                RegexOptions.IgnoreCase);

            if (!m.Success) return null;

            var datePart = (m.Groups["date"].Value ?? "").Trim().TrimEnd(',');
            var timePart = Regex.Replace((m.Groups["time"].Value ?? "").Trim(), @"\s+(am|pm)$", "$1", RegexOptions.IgnoreCase);

            var steamNow = GetSteamNow();
            var hasYear = Regex.IsMatch(datePart, @"\b\d{4}\b");
            if (!hasYear)
                datePart = $"{datePart}, {steamNow.Year}";

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

            if (!DateTime.TryParseExact(combined, formats, culture, DateTimeStyles.AllowWhiteSpaces, out var dt) &&
                !DateTime.TryParse(combined, culture, DateTimeStyles.AllowWhiteSpaces, out dt))
            {
                return null;
            }

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

        private static DateTime GetSteamNow()
        {
            try { return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, SteamBaseTimeZone); }
            catch { return DateTime.Now; }
        }
    }
}
