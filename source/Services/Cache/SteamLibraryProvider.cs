using System;
using System.Collections.Generic;
using System.Linq;
using Playnite.SDK;
using Playnite.SDK.Models;

namespace FriendsAchievementFeed.Services
{
    internal static class SteamLibraryProvider
    {
        private static readonly Guid SteamPluginId = Guid.Parse("CB91DFC9-B977-43BF-8E70-55F46E410FAB");

        public static Dictionary<int, Game> BuildSteamLibraryGamesDict(IPlayniteAPI api, ISteamDataProvider steam, ILogger logger)
        {
            var dict = new Dictionary<int, Game>();

            var dbGames = api?.Database?.Games;
            if (dbGames == null)
            {
                logger?.Info("[FAF] Steam games in Playnite DB: 0 (no DB)");
                return dict;
            }

            foreach (var g in dbGames)
            {
                if (g?.PluginId != SteamPluginId)
                    continue;

                if (steam.TryGetSteamAppId(g, out var appId) && appId > 0)
                {
                    if (!dict.ContainsKey(appId))
                        dict[appId] = g;
                }
            }

            logger?.Info($"[FAF] Steam games in Playnite DB: {dict.Count}");
            return dict;
        }

        public static Dictionary<string, int> BuildPlayniteIdToAppId(Dictionary<int, Game> steamGamesDict)
        {
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (steamGamesDict == null) return map;

            foreach (var kv in steamGamesDict)
            {
                var appId = kv.Key;
                var game = kv.Value;
                if (game == null) continue;
                var pid = game.Id.ToString();
                if (!string.IsNullOrWhiteSpace(pid) && !map.ContainsKey(pid))
                    map[pid] = appId;
            }
            return map;
        }

        public static Dictionary<int, int> FilterMinutesToLibrary(Dictionary<int, int> minutesByApp, ISet<int> libraryAppIds)
        {
            if (minutesByApp == null || minutesByApp.Count == 0)
                return new Dictionary<int, int>();

            if (libraryAppIds == null || libraryAppIds.Count == 0)
                return new Dictionary<int, int>(minutesByApp);

            return minutesByApp
                .Where(kv => libraryAppIds.Contains(kv.Key))
                .ToDictionary(kv => kv.Key, kv => kv.Value);
        }

        public static bool HasNonZeroMinutes(Dictionary<int, int> minutesByApp, int appId)
        {
            return minutesByApp != null && minutesByApp.TryGetValue(appId, out var m) && m > 0;
        }

        public static List<int> FilterToNonZeroMinutesApps(IEnumerable<int> appIds, Dictionary<int, int> minutesByApp)
        {
            if (appIds == null) return new List<int>();

            var hasMinutes = minutesByApp?.Any() == true;
            var set = new HashSet<int>();

            foreach (var appId in appIds)
            {
                if (appId <= 0) continue;
                if (!hasMinutes || HasNonZeroMinutes(minutesByApp, appId))
                    set.Add(appId);
            }

            return set.ToList();
        }
    }
}
