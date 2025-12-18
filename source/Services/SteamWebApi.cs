using Common.SteamKitModels;
using Playnite.SDK.Data;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Diagnostics;

namespace FriendsAchievementFeed.Services
{
    // Lightweight Steam Web API helper (synchronous wrappers)
    public static class SteamWebApi
    {
        private static readonly HttpClient http = new HttpClient();

        private static T GetJson<T>(string url) where T : class
        {
            try
            {
                var json = http.GetStringAsync(url).GetAwaiter().GetResult();
                if (string.IsNullOrEmpty(json)) return default;
                return Serialization.FromJson<T>(json);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"SteamWebApi.GetJson error: {ex}");
                return default;
            }
        }

        public static List<SteamOwnedGame> GetOwnedGames(string apiKey, ulong steamId)
        {
            var url = $"https://api.steampowered.com/IPlayerService/GetOwnedGames/v1/?key={apiKey}&steamid={steamId}&include_appinfo=1&include_played_free_games=1";
            var wrapper = GetJson<OwnedGamesResponse>(url);
            var games = wrapper?.response?.games ?? new List<OwnedGameDto>();
            return games.Select(g => new SteamOwnedGame
            {
                Appid = g.appid,
                Name = g.name,
                ImgIconUrl = g.img_icon_url,
                HasCommunityVisibleStats = g.has_community_visible_stats,
                PlaytimeForever = g.playtime_forever,
                Playtime2weeks = g.playtime_2weeks
            }).ToList();
        }

        public static List<SteamFriend> GetFriendList(string apiKey, ulong steamId)
        {
            var url = $"https://api.steampowered.com/ISteamUser/GetFriendList/v1/?key={apiKey}&steamid={steamId}&relationship=friend";
            var wrapper = GetJson<FriendListResponse>(url);
            var friends = wrapper?.friendslist?.friends ?? new List<FriendDto>();
            return friends.Select(f => new SteamFriend
            {
                SteamId = ulong.TryParse(f.steamid, out var id) ? id : 0UL,
                Relationship = f.relationship,
                FriendSince = UnixTimeToDateTime(f.friend_since)
            }).ToList();
        }

        public static List<SteamPlayerSummaries> GetPlayerSummaries(string apiKey, List<ulong> steamIds)
        {
            if (steamIds == null || steamIds.Count == 0) return new List<SteamPlayerSummaries>();
            var ids = string.Join(",", steamIds);
            var url = $"https://api.steampowered.com/ISteamUser/GetPlayerSummaries/v2/?key={apiKey}&steamids={ids}";
            var wrapper = GetJson<PlayerSummariesResponse>(url);
            var players = wrapper?.response?.players ?? new List<PlayerDto>();
            return players.Select(p => new SteamPlayerSummaries
            {
                SteamId = p.steamid,
                PersonaName = p.personaname,
                Avatar = p.avatar,
                AvatarMedium = p.avatarmedium,
                AvatarFull = p.avatarfull,
                AvatarHash = p.avatarhash,
                CommunityVisibilityState = p.communityvisibilitystate,
                ProfileState = p.profilestate,
                ProfileUrl = p.profileurl,
                TimeCreated = UnixTimeToDateTime(p.timecreated),
                LastLogoff = UnixTimeToDateTime(p.lastlogoff),
                PersonaState = p.personastate,
                PersonaStateFlags = p.personastateflags,
                PrimaryClanId = p.primaryclanid,
                LocCountryCode = p.loccountrycode
            }).ToList();
        }

        public static List<SteamPlayerAchievement> GetPlayerAchievements(string apiKey, uint appId, ulong steamId)
        {
            var url = $"https://api.steampowered.com/ISteamUserStats/GetPlayerAchievements/v1/?key={apiKey}&appid={appId}&steamid={steamId}";
            var wrapper = GetJson<PlayerAchievementsResponse>(url);
            var achievements = wrapper?.playerstats?.achievements ?? new List<PlayerAchievementDto>();
            return achievements.Select(a => new SteamPlayerAchievement
            {
                ApiName = a.apiname,
                Achieved = a.achieved ? 1 : 0,
                UnlockTime = a.unlocktime > 0 ? UnixTimeToDateTime(a.unlocktime) : default(DateTime),
                Name = a.name,
                Description = a.description
            }).ToList();
        }

        public static SteamSchema GetSchemaForGame(string apiKey, uint appId, string language = "english")
        {
            var url = $"https://api.steampowered.com/ISteamUserStats/GetSchemaForGame/v2/?key={apiKey}&appid={appId}&l={language}";
            var wrapper = GetJson<SchemaResponse>(url);
            var schema = new SteamSchema();
            var stats = wrapper?.game?.availableGameStats?.stats ?? new List<SchemaStatDto>();
            var achs = wrapper?.game?.availableGameStats?.achievements ?? new List<SchemaAchievementDto>();
            foreach (var s in stats)
            {
                schema.Stats.Add(new SteamSchemaStats { Name = s.name, DefaultValue = NormalizeDefaultValue(s.defaultvalue), DisplayName = s.displayName });
            }
            foreach (var a in achs)
            {
                schema.Achievements.Add(new SteamSchemaAchievements
                {
                    Name = a.name,
                    DefaultValue = NormalizeDefaultValue(a.defaultvalue),
                    DisplayName = a.displayName,
                    Hidden = a.hidden,
                    Description = a.description,
                    Icon = a.icon,
                    IconGray = a.iconGray
                });
            }
            return schema;
        }
        private static int NormalizeDefaultValue(long? value)
        {
            if (!value.HasValue)
            {
                return 0;
            }

            var v = value.Value;

            // Steam uses 0xFFFFFFFF (4294967295) as “no default” / sentinel for some uints
            if (v == 4294967295L)
            {
                return 0;
            }

            if (v > int.MaxValue) return int.MaxValue;
            if (v < int.MinValue) return int.MinValue;
            return (int)v;
        }
        // Return UTC DateTime for consistent storage; views/services should convert to local when displaying.
        private static DateTime UnixTimeToDateTime(long seconds)
        {
            try
            {
                return DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
            }
            catch
            {
                return DateTime.MinValue;
            }
        }

        #region DTOs
        private class OwnedGamesResponse { public OwnedGamesDto response { get; set; } }
        private class OwnedGamesDto { public List<OwnedGameDto> games { get; set; } }
        private class OwnedGameDto { public int appid { get; set; } public string name { get; set; } public string img_icon_url { get; set; } public bool has_community_visible_stats { get; set; } public int playtime_forever { get; set; } public int playtime_2weeks { get; set; } }

        private class FriendListResponse { public FriendListDto friendslist { get; set; } }
        private class FriendListDto { public List<FriendDto> friends { get; set; } }
        private class FriendDto { public string steamid { get; set; } public string relationship { get; set; } public long friend_since { get; set; } }

        private class PlayerSummariesResponse { public PlayerSummariesDto response { get; set; } }
        private class PlayerSummariesDto { public List<PlayerDto> players { get; set; } }
        private class PlayerDto { public string steamid { get; set; } public string personaname { get; set; } public string profileurl { get; set; } public string avatar { get; set; } public string avatarmedium { get; set; } public string avatarfull { get; set; } public string avatarhash { get; set; } public long lastlogoff { get; set; } public int personastate { get; set; } public int personastateflags { get; set; } public string primaryclanid { get; set; } public long timecreated { get; set; } public int communityvisibilitystate { get; set; } public int profilestate { get; set; } public string loccountrycode { get; set; } }

        private class PlayerAchievementsResponse { public PlayerStatsDto playerstats { get; set; } }
        private class PlayerStatsDto { public List<PlayerAchievementDto> achievements { get; set; } }
        private class PlayerAchievementDto { public string apiname { get; set; } public bool achieved { get; set; } public long unlocktime { get; set; } public string name { get; set; } public string description { get; set; } }

        private class SchemaResponse { public SchemaGameDto game { get; set; } }
        private class SchemaGameDto { public AvailableGameStatsDto availableGameStats { get; set; } }
        private class AvailableGameStatsDto { public List<SchemaStatDto> stats { get; set; } public List<SchemaAchievementDto> achievements { get; set; } }
        private class SchemaStatDto { public string name { get; set; } public long defaultvalue { get; set; } public string displayName { get; set; } }
        private class SchemaAchievementDto { public string name { get; set; } public long defaultvalue { get; set; } public string displayName { get; set; } public bool hidden { get; set; } public string description { get; set; } public string icon { get; set; } public string iconGray { get; set; } }
        #endregion
    }
}
