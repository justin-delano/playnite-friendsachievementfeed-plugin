// SteamDataProvider.cs

using FriendsAchievementFeed.Models;
using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SteamFriendModel = FriendsAchievementFeed.Models.SteamFriend;

namespace FriendsAchievementFeed.Services
{
    internal sealed class SteamDataProvider : ISteamDataProvider
    {
        private readonly ILogger _logger;
        private readonly FriendsAchievementFeedPlugin _plugin;
        private readonly SteamClient _steam;

        private readonly ConcurrentDictionary<string, Task<List<SteamFriendModel>>> _friendsCache =
            new ConcurrentDictionary<string, Task<List<SteamFriendModel>>>(StringComparer.OrdinalIgnoreCase);

        private readonly ConcurrentDictionary<string, Task<Dictionary<int, int>>> _ownedGamePlaytimeCache =
            new ConcurrentDictionary<string, Task<Dictionary<int, int>>>(StringComparer.OrdinalIgnoreCase);

        public SteamDataProvider(IPlayniteAPI api, ILogger logger, FriendsAchievementFeedPlugin plugin, ICacheService cacheService)
        {
            _logger = logger;
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
            _ = cacheService ?? throw new ArgumentNullException(nameof(cacheService)); // currently unused here

            if (api == null) throw new ArgumentNullException(nameof(api));
            _steam = new SteamClient(api, _logger, _plugin.GetPluginUserDataPath());
        }

        private static string BuildAchievementsUrl(string steamId64, int appId) =>
            $"https://steamcommunity.com/profiles/{steamId64}/stats/{appId}/?tab=achievements&l=english";

        private string GetApiKeyTrimmed()
        {
            var key = _plugin?.Settings?.SteamApiKey;
            return string.IsNullOrWhiteSpace(key) ? null : key.Trim();
        }

        public Task<bool> RefreshCookiesAsync(CancellationToken cancel)
            => _steam.RefreshCookiesHeadlessAsync(cancel);

        private async Task<string> ResolveSteamId64Async(string steamIdMaybe, CancellationToken ct)
        {
            if (!string.IsNullOrWhiteSpace(steamIdMaybe) && ulong.TryParse(steamIdMaybe, out _))
                return steamIdMaybe.Trim();

            var self = await _steam.GetSelfSteamId64Async(ct).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(self) ? null : self.Trim();
        }

        // ---------------------------------------------------------------------
        // Owned games playtimes
        // ---------------------------------------------------------------------

        public async Task<Dictionary<int, int>> GetOwnedGamePlaytimesAsync(string steamId, CancellationToken cancel)
        {
            var resolved = await ResolveSteamId64Async(steamId, cancel).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(resolved))
                return new Dictionary<int, int>();

            var apiKey = GetApiKeyTrimmed();
            if (string.IsNullOrWhiteSpace(apiKey))
                return new Dictionary<int, int>();

            var task = _ownedGamePlaytimeCache.GetOrAdd(resolved, id =>
                _steam.GetOwnedGamePlaytimesFromApiAsync(apiKey, id, cancel));

            try
            {
                var dict = await task.ConfigureAwait(false) ?? new Dictionary<int, int>();
                if (dict.Count == 0) _ownedGamePlaytimeCache.TryRemove(resolved, out _);
                return dict;
            }
            catch (OperationCanceledException)
            {
                _ownedGamePlaytimeCache.TryRemove(resolved, out _);
                throw;
            }
            catch
            {
                _ownedGamePlaytimeCache.TryRemove(resolved, out _);
                return new Dictionary<int, int>();
            }
        }

        public async Task<Dictionary<int, int>> GetPlaytimesForAppsAsync(
            string friendSteamId,
            ISet<int> appUniverse,
            CancellationToken cancel)
        {
            var all = await GetOwnedGamePlaytimesAsync(friendSteamId, cancel).ConfigureAwait(false)
                      ?? new Dictionary<int, int>();

            if (appUniverse == null || appUniverse.Count == 0 || all.Count == 0)
                return all;

            return all.Where(kv => appUniverse.Contains(kv.Key))
                      .ToDictionary(kv => kv.Key, kv => kv.Value);
        }

        // ---------------------------------------------------------------------
        // Auth test (HTML profile check)
        // ---------------------------------------------------------------------

        public async Task<(bool Success, string Message)> TestSteamAuthAsync(string steamUserId)
        {
            try
            {
                var resolved = await ResolveSteamId64Async(steamUserId, CancellationToken.None).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(resolved))
                {
                    return (false, ResourceProvider.GetString("LOCFriendsAchFeed_Error_SteamSessionNotFound"));
                }

                var page = await _steam.GetProfilePageAsync(resolved, CancellationToken.None).ConfigureAwait(false);
                var html = page?.Html ?? string.Empty;

                if ((int)(page?.StatusCode ?? 0) == 429)
                    return (false, ResourceProvider.GetString("LOCFriendsAchFeed_Error_SteamRateLimited"));

                if (string.IsNullOrWhiteSpace(html))
                    return (false, ResourceProvider.GetString("LOCFriendsAchFeed_Error_NoProfileReturned"));

                if (SteamClient.LooksLoggedOutHeader(html))
                {
                    await _steam.RefreshCookiesHeadlessAsync(CancellationToken.None).ConfigureAwait(false);

                    var page2 = await _steam.GetProfilePageAsync(resolved, CancellationToken.None).ConfigureAwait(false);
                    html = page2?.Html ?? string.Empty;

                    if ((int)(page2?.StatusCode ?? 0) == 429)
                        return (false, ResourceProvider.GetString("LOCFriendsAchFeed_Error_SteamRateLimited"));

                    if (string.IsNullOrWhiteSpace(html) || SteamClient.LooksLoggedOutHeader(html))
                        return (false, ResourceProvider.GetString("LOCFriendsAchFeed_Error_SteamCookiesInvalid"));
                }

                if (html.IndexOf("actual_persona_name", StringComparison.OrdinalIgnoreCase) < 0)
                    return (false, ResourceProvider.GetString("LOCFriendsAchFeed_Error_SteamProfileMarkersNotFound"));

                var okMsg = ResourceProvider.GetString("LOCFriendsAchFeed_Settings_SteamAuth_OK") ?? "Steam auth OK";
                return (true, okMsg);
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        // ---------------------------------------------------------------------
        // Achievements scraping – classification (UNCHANGED)
        // ---------------------------------------------------------------------

        private static bool IsStatsForApp(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
            var p = u.AbsolutePath.TrimEnd('/');
            return p.IndexOf("/stats/", StringComparison.OrdinalIgnoreCase) >= 0 &&
                   (p.IndexOf("/profiles/", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    p.IndexOf("/id/", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static bool IsLoginLikeUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            return url.IndexOf("/login", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   url.IndexOf("openid", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   url.IndexOf("sign in", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool LooksLoggedOutHeuristic(string html, string finalUrl)
        {
            if (SteamClient.LooksLoggedOutHeader(html)) return true;
            if (IsLoginLikeUrl(finalUrl)) return true;

            if (string.IsNullOrEmpty(html)) return false;

            return html.IndexOf("g_steamID = \"0\"", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   html.IndexOf("steamcommunity.com/login", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   html.IndexOf("store.steampowered.com/login", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool LooksPrivateOrUnavailable(string html, out string reason)
        {
            reason = null;
            if (string.IsNullOrEmpty(html)) return false;

            if (html.IndexOf("This profile is private", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                reason = "profile_private";
                return true;
            }

            if (html.IndexOf("You must be logged in to view this user's stats", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                reason = "requires_login_for_stats";
                return true;
            }

            if (html.IndexOf("The specified profile could not be found", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                reason = "profile_not_found";
                return true;
            }

            if (html.IndexOf("no achievements", StringComparison.OrdinalIgnoreCase) >= 0 &&
                html.IndexOf("achievement", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                reason = "no_achievements";
                return true;
            }

            return false;
        }

        public async Task<List<ScrapedAchievementRow>> GetScrapedAchievementsAsync(string steamId64, int appId, CancellationToken cancel)
        {
            var health = await GetScrapedAchievementsWithHealthAsync(steamId64, appId, cancel).ConfigureAwait(false);
            return health.Rows ?? new List<ScrapedAchievementRow>();
        }

        public async Task<AchievementsHealthResult> GetScrapedAchievementsWithHealthAsync(
            string steamId64,
            int appId,
            CancellationToken cancel,
            bool includeLocked = false)
        {
            var res = new AchievementsHealthResult();

            var resolved = await ResolveSteamId64Async(steamId64, cancel).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(resolved))
            {
                res.TransientFailure = true;
                res.Detail = "no_steam_session";
                res.Rows = new List<ScrapedAchievementRow>();
                return res;
            }

            var requested = BuildAchievementsUrl(resolved, appId);
            res.RequestedUrl = requested;

            async Task<(SteamClient.SteamPageResult Page, string Html)> FetchAsync()
            {
                var page = await _steam.GetAchievementsPageAsync(resolved, appId, cancel).ConfigureAwait(false);
                var html = page?.Html ?? string.Empty;
                return (page, html);
            }

            var (page1, html1) = await FetchAsync().ConfigureAwait(false);
            res.StatusCode = page1 != null ? (int)page1.StatusCode : 0;
            res.FinalUrl = page1?.FinalUrl ?? requested;

            if (res.StatusCode == 429)
            {
                res.TransientFailure = true;
                res.Detail = "429";
                return res;
            }

            if (LooksLoggedOutHeuristic(html1, res.FinalUrl))
            {
                await _steam.RefreshCookiesHeadlessAsync(cancel).ConfigureAwait(false);

                var (page2, html2) = await FetchAsync().ConfigureAwait(false);
                res.StatusCode = page2 != null ? (int)page2.StatusCode : 0;
                res.FinalUrl = page2?.FinalUrl ?? requested;

                if (res.StatusCode == 429)
                {
                    res.TransientFailure = true;
                    res.Detail = "429_after_refresh";
                    return res;
                }

                if (LooksLoggedOutHeuristic(html2, res.FinalUrl))
                {
                    res.TransientFailure = true;
                    res.Detail = "cookies_bad_after_refresh";
                    return res;
                }

                var r2 = ClassifyScrapeOrPrivate(res, html2, includeLocked);
                await MaybeClassifyNoAchievementsBySchemaAsync(r2, appId, cancel).ConfigureAwait(false);
                return r2;
            }

            if (page1?.WasRedirected == true && !IsStatsForApp(res.FinalUrl))
            {
                res.StatsUnavailable = true;
                res.Detail = "redirect_off_stats";
                await MaybeClassifyNoAchievementsBySchemaAsync(res, appId, cancel).ConfigureAwait(false);
                return res;
            }

            var r1 = ClassifyScrapeOrPrivate(res, html1, includeLocked);
            await MaybeClassifyNoAchievementsBySchemaAsync(r1, appId, cancel).ConfigureAwait(false);
            return r1;
        }

        private async Task MaybeClassifyNoAchievementsBySchemaAsync(AchievementsHealthResult res, int appId, CancellationToken ct)
        {
            if (res == null || appId <= 0) return;
            if (!res.StatsUnavailable) return;

            var apiKey = GetApiKeyTrimmed();
            if (string.IsNullOrWhiteSpace(apiKey)) return;

            bool? has;
            try
            {
                has = await _steam.GetAppHasAchievementsAsync(apiKey, appId, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"[FAF] Failed to check if app {appId} has achievements via API.");
                return;
            }

            if (has == false)
                res.Detail = "no_achievements";
        }

        private AchievementsHealthResult ClassifyScrapeOrPrivate(AchievementsHealthResult res, string html, bool includeLocked)
        {
            res.Rows = new List<ScrapedAchievementRow>();
            res.TransientFailure = true;
            res.StatsUnavailable = false;
            res.Detail = "no_rows_unknown";

            var parsed = _steam.ParseAchievements(html, includeLocked) ?? new List<ScrapedAchievementRow>();
            _logger?.Debug($"ClassifyScrapeOrPrivate: Parsed {parsed.Count} achievement rows (includeLocked={includeLocked})");

            if (parsed.Count > 0)
            {
                res.Rows = parsed;
                res.TransientFailure = false;
                res.Detail = "scraped";
                return res;
            }

            if (LooksPrivateOrUnavailable(html, out var why))
            {
                res.TransientFailure = false;
                res.StatsUnavailable = true;
                res.Detail = why ?? "unavailable";
                return res;
            }

            if (SteamClient.HasAnyAchievementRows(html))
            {
                var hasUnlockedMarkers = html.IndexOf("achieveUnlockTime", StringComparison.OrdinalIgnoreCase) >= 0;

                if (!hasUnlockedMarkers && !includeLocked)
                {
                    res.TransientFailure = false;
                    res.Detail = "all_hidden";
                    return res;
                }

                res.Detail = hasUnlockedMarkers ? "unlocked_marker_but_parse_failed" : "rows_marker_but_parse_failed";
                return res;
            }

            return res;
        }

        // ---------------------------------------------------------------------
        // Friends (CHANGED): call summaries via API-backed GetPlayerSummaries
        // ---------------------------------------------------------------------

        public async Task<List<SteamFriendModel>> GetFriendsAsync(string steamId, string apiKey, CancellationToken cancel)
        {
            var resolved = await ResolveSteamId64Async(steamId, cancel).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(resolved))
                return new List<SteamFriendModel>();

            apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
            if (string.IsNullOrWhiteSpace(apiKey))
                apiKey = GetApiKeyTrimmed();

            if (string.IsNullOrWhiteSpace(apiKey))
                return new List<SteamFriendModel>();

            var task = _friendsCache.GetOrAdd(resolved, _ => LoadFriendsAsync(resolved, apiKey, cancel));

            try
            {
                var res = await task.ConfigureAwait(false) ?? new List<SteamFriendModel>();
                if (res.Count == 0) _friendsCache.TryRemove(resolved, out _);
                return res;
            }
            catch (OperationCanceledException)
            {
                _friendsCache.TryRemove(resolved, out _);
                throw;
            }
            catch
            {
                _friendsCache.TryRemove(resolved, out _);
                return new List<SteamFriendModel>();
            }
        }

        private async Task<List<SteamFriendModel>> LoadFriendsAsync(string steamId64, string apiKey, CancellationToken ct)
        {
            var result = new List<SteamFriendModel>();
            if (string.IsNullOrWhiteSpace(apiKey) || !ulong.TryParse(steamId64, out _))
                return result;

            var friendIds = await _steam.GetFriendIdsAsync(steamId64, apiKey, ct).ConfigureAwait(false);
            if (friendIds == null || friendIds.Count == 0)
                return result;

            // This is the key change: API-based summaries (batched), not HTML scraping.
            var summaries = await _steam.GetPlayerSummariesAsync(apiKey, friendIds, ct).ConfigureAwait(false);
            if (summaries == null || summaries.Count == 0)
                return result;

            foreach (var p in summaries)
            {
                if (p == null) continue;

                result.Add(new SteamFriendModel
                {
                    SteamId = p.SteamId,
                    PersonaName = p.PersonaName,
                    AvatarMediumUrl = string.IsNullOrEmpty(p.AvatarMedium) ? p.Avatar : p.AvatarMedium
                });
            }

            return result;
        }

        // ---------------------------------------------------------------------
        // Playnite DB mapping
        // ---------------------------------------------------------------------

        public bool TryGetSteamAppId(Game game, out int appId)
        {
            appId = 0;
            return game != null &&
                   !string.IsNullOrWhiteSpace(game.GameId) &&
                   int.TryParse(game.GameId, out appId);
        }
    }
}
