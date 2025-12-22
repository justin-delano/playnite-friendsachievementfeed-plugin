using FriendsAchievementFeed.Models;
using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Threading;
using System.Threading.Tasks;
using Common.SteamKitModels;
using SteamFriendModel = FriendsAchievementFeed.Models.SteamFriend;

namespace FriendsAchievementFeed.Services
{
    internal sealed class SteamDataProvider
    {
        private readonly IPlayniteAPI _api;
        private readonly ILogger _logger;
        private readonly FriendsAchievementFeedPlugin _plugin;
        private readonly SteamClient _steamHtml;

        private readonly ConcurrentDictionary<string, Task<HashSet<int>>> _ownedGamesCache =
            new ConcurrentDictionary<string, Task<HashSet<int>>>(StringComparer.OrdinalIgnoreCase);

        private readonly ConcurrentDictionary<string, Task<Dictionary<int, int>>> _ownedGamePlaytimeCache =
            new ConcurrentDictionary<string, Task<Dictionary<int, int>>>(StringComparer.OrdinalIgnoreCase);

        private readonly ConcurrentDictionary<string, Task<List<SteamFriendModel>>> _friendsCache =
            new ConcurrentDictionary<string, Task<List<SteamFriendModel>>>(StringComparer.OrdinalIgnoreCase);

        // --- Self achievement cache (my unlock times + my locked icons) ---
        private readonly ConcurrentDictionary<string, SelfAchievementGameData> _selfAchCache =
            new ConcurrentDictionary<string, SelfAchievementGameData>(StringComparer.OrdinalIgnoreCase);

        private readonly ConcurrentDictionary<string, Task<SelfAchievementGameData>> _selfAchTasks =
            new ConcurrentDictionary<string, Task<SelfAchievementGameData>>(StringComparer.OrdinalIgnoreCase);

        private readonly string _selfCacheRootPath;

        public SteamDataProvider(IPlayniteAPI api, ILogger logger, FriendsAchievementFeedPlugin plugin)
        {
            _api = api;
            _logger = logger;
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));

            _steamHtml = new SteamClient(_api, _logger, () => _plugin.Settings?.SteamApiKey);

            var baseDir = _plugin.GetPluginUserDataPath();
            _selfCacheRootPath = Path.Combine(baseDir, "self_achievement_cache");
            try { Directory.CreateDirectory(_selfCacheRootPath); } catch { }
        }

        private static DateTime AsUtcKind(DateTime dt)
        {
            if (dt.Kind == DateTimeKind.Utc) return dt;
            if (dt.Kind == DateTimeKind.Local) return dt.ToUniversalTime();
            return DateTime.SpecifyKind(dt, DateTimeKind.Utc);
        }

        private static DateTime? AsUtcKind(DateTime? dt)
        {
            if (!dt.HasValue) return null;
            return AsUtcKind(dt.Value);
        }

        private string SelfKey(string steamId64, int appId) => $"{steamId64}:{appId}";
        private string SelfUserDir(string steamId64) => Path.Combine(_selfCacheRootPath, steamId64);
        private string SelfFilePath(string steamId64, int appId) => Path.Combine(SelfUserDir(steamId64), $"{appId}.json");

        public bool TryGetSelfAchievementData(string steamId64, int appId, out SelfAchievementGameData data)
        {
            data = null;
            if (string.IsNullOrWhiteSpace(steamId64) || appId <= 0) return false;
            return _selfAchCache.TryGetValue(SelfKey(steamId64, appId), out data) && data != null;
        }

        public async Task<SelfAchievementGameData> EnsureSelfAchievementDataAsync(string steamId64, int appId, CancellationToken cancel)
        {
            if (string.IsNullOrWhiteSpace(steamId64) || appId <= 0)
            {
                return new SelfAchievementGameData();
            }

            var key = SelfKey(steamId64, appId);

            if (_selfAchCache.TryGetValue(key, out var cached) && cached != null)
            {
                return cached;
            }

            // Try disk
            var disk = TryLoadSelfFromDisk(steamId64, appId);
            if (disk != null)
            {
                _selfAchCache[key] = disk;
                return disk;
            }

            // Fetch once (shared)
            var task = _selfAchTasks.GetOrAdd(key, _ => FetchAndStoreSelfAsync(steamId64, appId, cancel));
            return await AwaitSelfTask(key, task, cancel).ConfigureAwait(false);

            async Task<SelfAchievementGameData> AwaitSelfTask(string k, Task<SelfAchievementGameData> t, CancellationToken ct)
            {
                try
                {
                    return await t.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    _selfAchTasks.TryRemove(k, out _);
                    throw;
                }
                catch
                {
                    _selfAchTasks.TryRemove(k, out _);
                    return new SelfAchievementGameData();
                }
            }
        }

        private SelfAchievementGameData TryLoadSelfFromDisk(string steamId64, int appId)
        {
            try
            {
                var path = SelfFilePath(steamId64, appId);
                if (!File.Exists(path)) return null;

                using (var stream = File.OpenRead(path))
                {
                    var serializer = new DataContractJsonSerializer(typeof(SelfAchievementGameData));
                    var data = (SelfAchievementGameData)serializer.ReadObject(stream);
                    return data ?? null;
                }
            }
            catch
            {
                return null;
            }
        }

        private void SaveSelfToDisk(string steamId64, int appId, SelfAchievementGameData data)
        {
            try
            {
                var dir = SelfUserDir(steamId64);
                Directory.CreateDirectory(dir);

                var path = SelfFilePath(steamId64, appId);
                using (var stream = new FileStream(path, FileMode.Create))
                {
                    var serializer = new DataContractJsonSerializer(typeof(SelfAchievementGameData));
                    serializer.WriteObject(stream, data);
                }
            }
            catch
            {
                // swallow disk errors
            }
        }

        private async Task<SelfAchievementGameData> FetchAndStoreSelfAsync(string steamId64, int appId, CancellationToken cancel)
        {
            var key = SelfKey(steamId64, appId);

            var rows = await GetScrapedAchievementsAsync(steamId64, appId, cancel).ConfigureAwait(false);

            var data = new SelfAchievementGameData
            {
                LastUpdatedUtc = DateTime.UtcNow
            };

            if (rows != null)
            {
                foreach (var r in rows)
                {
                    if (r == null || string.IsNullOrWhiteSpace(r.Key)) continue;

                    var achKey = r.Key;
                    var unlockUtc = AsUtcKind(r.UnlockTimeUtc);

                    if (unlockUtc.HasValue)
                    {
                        data.UnlockTimesUtc[achKey] = unlockUtc;
                    }
                    else
                    {
                        if (!string.IsNullOrWhiteSpace(r.IconUrl))
                        {
                            data.LockedIconUrls[achKey] = r.IconUrl;
                        }
                    }
                }
            }

            _selfAchCache[key] = data;
            SaveSelfToDisk(steamId64, appId, data);

            _selfAchTasks.TryRemove(key, out _);
            return data;
        }

        // -----------------------------
        // Auth / scrape / friends / owned
        // -----------------------------

        public async Task<(bool Success, string Message)> TestSteamAuthAsync(string steamUserId)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(steamUserId))
                {
                    var msg = ResourceProvider.GetString("LOCFriendsAchFeed_Settings_SteamAuth_MissingCredentials")
                        ?? "Missing Steam User ID.";
                    return (false, msg);
                }

                if (!ulong.TryParse(steamUserId, out _))
                {
                    var msg = ResourceProvider.GetString("LOCFriendsAchFeed_Settings_SteamAuth_InvalidSteamId")
                        ?? "Invalid Steam User ID.";
                    return (false, msg);
                }

                var html = await _steamHtml.GetProfileHtmlAsync(steamUserId, CancellationToken.None).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(html) || html.IndexOf("profile", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    var msg = ResourceProvider.GetString("LOCFriendsAchFeed_Settings_SteamAuth_NoResponse")
                        ?? "No profile returned (not logged in or blocked).";
                    return (false, msg);
                }

                var okMsg = ResourceProvider.GetString("LOCFriendsAchFeed_Settings_SteamAuth_OK") ?? "Steam auth OK";
                return (true, okMsg);
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        public async Task<List<ScrapedAchievementRow>> GetScrapedAchievementsAsync(string steamId64, int appId, CancellationToken cancel)
        {
            try
            {
                var html = await _steamHtml.GetAchievementsHtmlAsync(steamId64, appId, cancel).ConfigureAwait(false);
                return _steamHtml.ParseAchievementsPage(html) ?? new List<ScrapedAchievementRow>();
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger?.Debug($"[SteamHtml] Failed achievements scrape for steamId={steamId64} appId={appId}: {ex.Message}");
                return new List<ScrapedAchievementRow>();
            }
        }

        private async Task<HashSet<int>> FetchOwnedGameIdsAsync(string steamId)
        {
            if (!ulong.TryParse(steamId, out _)) return new HashSet<int>();

            try
            {
                var list = await _steamHtml.GetOwnedGameIdsAsync(steamId, CancellationToken.None).ConfigureAwait(false);
                list = list ?? new List<int>();
                return new HashSet<int>(list);
            }
            catch (Exception ex)
            {
                _logger?.Debug($"[SteamHtml] GetOwnedGameIds failed for {steamId}: {ex.Message}");
                return new HashSet<int>();
            }
        }

        public async Task<HashSet<int>> GetOwnedGameIdsAsync(string steamId)
        {
            if (string.IsNullOrWhiteSpace(steamId)) return new HashSet<int>();
            return await _ownedGamesCache.GetOrAdd(steamId, id => FetchOwnedGameIdsAsync(id)).ConfigureAwait(false);
        }

        private async Task<Dictionary<int, int>> FetchOwnedGamePlaytimesAsync(string steamId)
        {
            if (!ulong.TryParse(steamId, out _)) return new Dictionary<int, int>();

            try
            {
                var dict = await _steamHtml.GetOwnedGamePlaytimesAsync(steamId, CancellationToken.None).ConfigureAwait(false);
                return dict ?? new Dictionary<int, int>();
            }
            catch (Exception ex)
            {
                _logger?.Debug($"[SteamHtml] GetOwnedGamePlaytimes failed for {steamId}: {ex.Message}");
                return new Dictionary<int, int>();
            }
        }

        public async Task<Dictionary<int, int>> GetOwnedGamePlaytimesAsync(string steamId, CancellationToken cancel)
        {
            if (string.IsNullOrWhiteSpace(steamId)) return new Dictionary<int, int>();
            cancel.ThrowIfCancellationRequested();

            var dict = await _ownedGamePlaytimeCache
                .GetOrAdd(steamId, id => FetchOwnedGamePlaytimesAsync(id))
                .ConfigureAwait(false);

            cancel.ThrowIfCancellationRequested();
            return dict ?? new Dictionary<int, int>();
        }

        public async Task<Dictionary<int, int>> GetMutualOwnedGamePlaytimesAsync(
            string friendSteamId,
            HashSet<int> yourOwnedAppIds,
            CancellationToken cancel)
        {
            if (string.IsNullOrWhiteSpace(friendSteamId) || yourOwnedAppIds == null || yourOwnedAppIds.Count == 0)
            {
                return new Dictionary<int, int>();
            }

            var all = await GetOwnedGamePlaytimesAsync(friendSteamId, cancel).ConfigureAwait(false);
            if (all == null || all.Count == 0) return new Dictionary<int, int>();

            var mutual = new Dictionary<int, int>();
            foreach (var kv in all)
            {
                if (yourOwnedAppIds.Contains(kv.Key))
                {
                    mutual[kv.Key] = kv.Value; // includes 0
                }
            }

            return mutual;
        }

        private async Task<List<SteamFriendModel>> FetchFriendsAsync(string steamId)
        {
            var result = new List<SteamFriendModel>();
            if (!ulong.TryParse(steamId, out _)) return result;

            try
            {
                var friendIds = await _steamHtml.GetFriendIdsAsync(steamId, CancellationToken.None).ConfigureAwait(false);
                friendIds = friendIds ?? new List<ulong>();

                var summaries = await _steamHtml.GetPlayerSummariesAsync(friendIds, CancellationToken.None).ConfigureAwait(false);
                summaries = summaries ?? new List<SteamPlayerSummaries>();

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
            }
            catch (Exception ex)
            {
                _logger?.Debug($"[SteamHtml] GetFriends failed for {steamId}: {ex.Message}");
            }

            return result;
        }

        public async Task<List<SteamFriendModel>> GetFriendsAsync(string steamId)
        {
            if (string.IsNullOrWhiteSpace(steamId)) return new List<SteamFriendModel>();
            return await _friendsCache.GetOrAdd(steamId, id => FetchFriendsAsync(id)).ConfigureAwait(false);
        }

        public bool TryGetSteamAppId(Game game, out int appId)
        {
            appId = 0;
            if (game == null || string.IsNullOrWhiteSpace(game.GameId)) return false;
            return int.TryParse(game.GameId, out appId);
        }

        public IEnumerable<Game> EnumerateSteamGamesInLibrary()
        {
            return _api.Database.Games.Where(g =>
                g != null &&
                !g.Hidden &&
                !string.IsNullOrWhiteSpace(g.GameId) &&
                int.TryParse(g.GameId, out _));
        }

        public Dictionary<int, Game> BuildOwnedSteamGamesDict(HashSet<int> yourOwnedGames)
        {
            var dict = new Dictionary<int, Game>();
            if (yourOwnedGames == null || yourOwnedGames.Count == 0) return dict;

            foreach (var g in EnumerateSteamGamesInLibrary())
            {
                if (!TryGetSteamAppId(g, out var appId) || appId == 0) continue;
                if (!yourOwnedGames.Contains(appId)) continue;

                if (!dict.ContainsKey(appId))
                {
                    dict.Add(appId, g);
                }
            }

            return dict;
        }
    }
}
