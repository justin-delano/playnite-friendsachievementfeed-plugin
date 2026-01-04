using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Playnite.SDK;
using Playnite.SDK.Data;

namespace FriendsAchievementFeed.Services.Steam
{
    internal sealed class SteamApiHelper
    {
        private const string DefaultUserAgent = 
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

        private const int PlayerSummariesBatchSize = 100;

        private readonly HttpClient _apiHttp;
        private readonly ILogger _logger;

        public SteamApiHelper(HttpClient apiHttp, ILogger logger)
        {
            _apiHttp = apiHttp ?? throw new ArgumentNullException(nameof(apiHttp));
            _logger = logger;
        }

        public async Task<List<SteamPlayerSummaries>> GetPlayerSummariesAsync(string apiKey, IEnumerable<ulong> steamIds, CancellationToken ct)
        {
            var ids = steamIds?
                .Where(x => x > 0)
                .Distinct()
                .ToList() ?? new List<ulong>();

            if (ids.Count == 0)
                return new List<SteamPlayerSummaries>();

            if (string.IsNullOrWhiteSpace(apiKey))
                return new List<SteamPlayerSummaries>(); // Caller should handle fallback

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

            return ordered;
        }

        public async Task<Dictionary<int, int>> GetPlaytimesForAppsAsync(string apiKey, string steamId64, IEnumerable<int> appIds, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(apiKey)) return new Dictionary<int, int>();
            if (string.IsNullOrWhiteSpace(steamId64) || !ulong.TryParse(steamId64, out _)) return new Dictionary<int, int>();

            var appSet = new HashSet<int>(appIds?.Where(x => x > 0) ?? Enumerable.Empty<int>());
            if (appSet.Count == 0) return new Dictionary<int, int>();

            var url = "https://api.steampowered.com/IPlayerService/GetOwnedGames/v1/" +
                      $"?key={Uri.EscapeDataString(apiKey.Trim())}" +
                      $"&steamid={Uri.EscapeDataString(steamId64)}" +
                      "&include_played_free_games=1&format=json";

            try
            {
                using (var req = new HttpRequestMessage(HttpMethod.Get, url))
                {
                    req.Headers.TryAddWithoutValidation("User-Agent", DefaultUserAgent);

                    using (var resp = await _apiHttp.SendAsync(req, ct).ConfigureAwait(false))
                    {
                        if (!resp.IsSuccessStatusCode)
                            return new Dictionary<int, int>();

                        var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                        if (string.IsNullOrWhiteSpace(json)) return new Dictionary<int, int>();

                        return ParsePlaytimesFromJson(json, appSet);
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"[FAF] GetPlaytimes failed for {steamId64}");
                return new Dictionary<int, int>();
            }
        }

        private Dictionary<int, int> ParsePlaytimesFromJson(string json, HashSet<int> targetApps)
        {
            var result = new Dictionary<int, int>();

            try
            {
                var root = Serialization.FromJson<OwnedGamesRoot>(json);
                var games = root?.Response?.Games;
                if (games == null || games.Count == 0) return result;

                foreach (var g in games)
                {
                    if (g?.AppId != null && g.PlaytimeForever.HasValue && targetApps.Contains(g.AppId.Value))
                    {
                        result[g.AppId.Value] = g.PlaytimeForever.Value;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "[FAF] Failed to parse playtimes JSON");
            }

            return result;
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

        // JSON models for API responses
        private class PlayerSummariesRoot
        {
            public PlayerSummariesResponse Response { get; set; }
        }

        private class PlayerSummariesResponse  
        {
            public List<PlayerSummary> Players { get; set; }
        }

        private class PlayerSummary
        {
            public string SteamId { get; set; }
            public string PersonaName { get; set; }
            public string Avatar { get; set; }
            public string AvatarMedium { get; set; }
            public string AvatarFull { get; set; }
        }

        private class OwnedGamesRoot
        {
            public OwnedGamesResponse Response { get; set; }
        }

        private class OwnedGamesResponse
        {
            public List<OwnedGame> Games { get; set; }
        }

        private class OwnedGame
        {
            public int? AppId { get; set; }
            public int? PlaytimeForever { get; set; }
        }
    }
}