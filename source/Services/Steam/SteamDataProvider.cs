using FriendsAchievementFeed.Models;
using FriendsAchievementFeed.Services.Steam.Models;
using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SteamFriendModel = FriendsAchievementFeed.Models.SteamFriend;

namespace FriendsAchievementFeed.Services
{
    internal sealed class SteamDataProvider : ISteamDataProvider, IDisposable
    {
        private readonly ILogger _logger;
        private readonly FriendsAchievementFeedPlugin _plugin;
        private readonly SteamClient _steam;

        private readonly ConcurrentDictionary<string, Lazy<Task<List<SteamFriendModel>>>> _friendsCache =
            new ConcurrentDictionary<string, Lazy<Task<List<SteamFriendModel>>>>(StringComparer.OrdinalIgnoreCase);

        private readonly ConcurrentDictionary<string, Lazy<Task<Dictionary<int, int>>>> _ownedGamePlaytimeCache =
            new ConcurrentDictionary<string, Lazy<Task<Dictionary<int, int>>>>(StringComparer.OrdinalIgnoreCase);

        public SteamDataProvider(IPlayniteAPI api, ILogger logger, FriendsAchievementFeedPlugin plugin, ICacheManager cacheManager)
        {
            _logger = logger;
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));

            if (api == null) throw new ArgumentNullException(nameof(api));
            _steam = new SteamClient(api, _logger, _plugin.GetPluginUserDataPath());
        }

        public void Dispose()
        {
            _steam?.Dispose();
        }

        private static string BuildAchievementsUrl(string steamId64, int appId) =>
            $"https://steamcommunity.com/profiles/{steamId64}/stats/{appId}/?tab=achievements&l=english";

        private string GetApiKeyTrimmed() => 
            _plugin?.Settings?.SteamApiKey?.Trim();

        public Task<bool> RefreshCookiesAsync(CancellationToken cancel, bool force = false)
            => _steam.RefreshCookiesHeadlessAsync(cancel, force);

        private async Task<string> ResolveSteamId64Async(string steamIdMaybe, CancellationToken ct) =>
            InputValidator.IsValidSteamId64(steamIdMaybe)
                ? steamIdMaybe.Trim()
                : (await _steam.GetSelfSteamId64Async(ct).ConfigureAwait(false))?.Trim();

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

            var lazyTask = _ownedGamePlaytimeCache.GetOrAdd(resolved, 
                new Lazy<Task<Dictionary<int, int>>>(() => _steam.GetOwnedGamePlaytimesFromApiAsync(apiKey, resolved, cancel)));

            try
            {
                var dict = await lazyTask.Value.ConfigureAwait(false) ?? new Dictionary<int, int>();
                if (dict.Count == 0) _ownedGamePlaytimeCache.TryRemove(resolved, out _);
                return dict;
            }
            catch
            {
                _ownedGamePlaytimeCache.TryRemove(resolved, out _);
                throw;
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

        private static string Loc(string key, string fallback = null) =>
            StringResources.GetString(key) ?? fallback;

        private static (bool IsRateLimited, bool IsEmpty, bool IsLoggedOut) CheckProfilePage(SteamClient.SteamPageResult page)
        {
            var statusCode = (int)(page?.StatusCode ?? 0);
            var html = page?.Html ?? string.Empty;
            
            return (
                IsRateLimited: statusCode == 429,
                IsEmpty: string.IsNullOrWhiteSpace(html),
                IsLoggedOut: SteamClient.LooksLoggedOutHeader(html)
            );
        }

        public async Task<(bool Success, string Message)> TestSteamAuthAsync(string steamUserId)
        {
            try
            {
                var resolved = await ResolveSteamId64Async(steamUserId, CancellationToken.None).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(resolved))
                    return (false, Loc("LOCFriendsAchFeed_Error_SteamSessionNotFound"));

                var page = await _steam.GetProfilePageAsync(resolved, CancellationToken.None).ConfigureAwait(false);
                var check = CheckProfilePage(page);

                if (check.IsRateLimited)
                    return (false, Loc("LOCFriendsAchFeed_Error_SteamRateLimited"));
                if (check.IsEmpty)
                    return (false, Loc("LOCFriendsAchFeed_Error_NoProfileReturned"));

                if (check.IsLoggedOut)
                {
                    await _steam.RefreshCookiesHeadlessAsync(CancellationToken.None).ConfigureAwait(false);
                    page = await _steam.GetProfilePageAsync(resolved, CancellationToken.None).ConfigureAwait(false);
                    check = CheckProfilePage(page);

                    if (check.IsRateLimited)
                        return (false, Loc("LOCFriendsAchFeed_Error_SteamRateLimited"));
                    if (check.IsEmpty || check.IsLoggedOut)
                        return (false, Loc("LOCFriendsAchFeed_Error_SteamCookiesInvalid"));
                }

                var html = page?.Html ?? string.Empty;
                if (html.IndexOf("actual_persona_name", StringComparison.OrdinalIgnoreCase) < 0)
                    return (false, Loc("LOCFriendsAchFeed_Error_SteamProfileMarkersNotFound"));

                return (true, Loc("LOCFriendsAchFeed_Settings_SteamAuth_OK", "Steam auth OK"));
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        // ---------------------------------------------------------------------
        // Achievements scraping – classification
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
            // 1. URL Check: redirects to login are definitive
            if (IsLoginLikeUrl(finalUrl)) return true;

            if (string.IsNullOrEmpty(html)) return false;

            // 2. Private profile or No Achievements is NOT a logout
            if (html.IndexOf("This profile is private", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (html.IndexOf("no achievements", StringComparison.OrdinalIgnoreCase) >= 0) return false;

            // 3. Positive confirmation: If we see the user menu, we are definitely logged in.
            if (html.IndexOf("global_action_menu", StringComparison.OrdinalIgnoreCase) >= 0) return false;

            // 4. Negative confirmation: Standard login header
            if (SteamClient.LooksLoggedOutHeader(html)) return true;

            // 5. Fallback: Specific Steam variable often present when logged in
            // If this variable is "0", you are usually logged out.
            if (html.IndexOf("g_steamID = \"0\"", StringComparison.OrdinalIgnoreCase) >= 0) return true;

            return false;
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

            // CRITICAL FIX: Only trigger refresh if we are definitively logged out AND it's not a private profile
            if (LooksLoggedOutHeuristic(html1, res.FinalUrl))
            {
                // Double check it isn't just private
                if (LooksPrivateOrUnavailable(html1, out var reason))
                {
                     res.TransientFailure = false;
                     res.StatsUnavailable = true;
                     res.Detail = reason;
                     return res;
                }

                _logger?.Warn($"[FAF] Detected logout on {requested}. Refreshing cookies...");
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

            apiKey = string.IsNullOrWhiteSpace(apiKey) ? GetApiKeyTrimmed() : apiKey.Trim();
            if (string.IsNullOrWhiteSpace(apiKey))
                return new List<SteamFriendModel>();

            var lazyTask = _friendsCache.GetOrAdd(resolved, 
                new Lazy<Task<List<SteamFriendModel>>>(() => LoadFriendsAsync(resolved, apiKey, cancel)));

            try
            {
                var res = await lazyTask.Value.ConfigureAwait(false) ?? new List<SteamFriendModel>();
                if (res.Count == 0) _friendsCache.TryRemove(resolved, out _);
                return res;
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
            // apiKey check already done by caller, but safe to keep basic validation
            if (string.IsNullOrWhiteSpace(apiKey) || !ulong.TryParse(steamId64, out _))
                return result;

            var friendIds = await _steam.GetFriendIdsAsync(steamId64, apiKey, ct).ConfigureAwait(false);
            if (friendIds == null || friendIds.Count == 0)
                return result;

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