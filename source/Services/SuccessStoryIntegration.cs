using System;
using System.IO;
using System.Collections.Generic;
using FriendsAchievementFeed.Models;
using Playnite.SDK;
using Playnite.SDK.Data;

namespace FriendsAchievementFeed.Services
{
    public static class SuccessStoryIntegration
    {
        // Known SuccessStory extension data id
        private const string SuccessStoryDataId = "cebe6d32-8c46-4459-b993-5a5189d60788";

        // -----------------------------
        // 1) Existing config DTOs
        // -----------------------------
        private class SuccessStoryConfigRoot
        {
            public SuccessStorySteamConfig Steam { get; set; }
        }

        private class SuccessStorySteamConfig
        {
            public string ApiKey { get; set; }
            public string AccountId { get; set; }
            public bool DisableApiUsage { get; set; }
        }

        // -----------------------------
        // 2) Per-game JSON DTOs
        // -----------------------------
        // These match the per-game JSON files in:
        //   <ExtensionsDataPath>\cebe6d32-8c46-4459-b993-5a5189d60788\SuccessStory
        private class SuccessStoryGameFile
        {
            public Guid Id { get; set; }          // Playnite Game.Id
            public string Name { get; set; }
            public List<SuccessStoryGameItem> Items { get; set; }
        }

        private class SuccessStoryGameItem
        {
            public string Name { get; set; }
            public string ApiName { get; set; }
            public string Description { get; set; }
            public string UrlUnlocked { get; set; }
            public string UrlLocked { get; set; }
            public bool IsHidden { get; set; }
        }

        // -----------------------------
        // 3) Config import (unchanged)
        // -----------------------------
        public static void TryApplySteamConfigFromSuccessStory(
            IPlayniteAPI api,
            FriendsAchievementFeedSettings settings,
            ILogger logger)
        {
            try
            {
                var basePath = Path.Combine(api.Paths.ExtensionsDataPath, SuccessStoryDataId);
                var configPath = Path.Combine(basePath, "config.json");

                if (!File.Exists(configPath))
                {
                    logger.Debug(ResourceProvider.GetString("Debug_SuccessStory_ConfigNotFound"));
                    return;
                }

                var json = File.ReadAllText(configPath);

                if (!Serialization.TryFromJson(json, out SuccessStoryConfigRoot root) || root?.Steam == null)
                {
                    logger.Debug(ResourceProvider.GetString("Debug_SuccessStory_ParseFailed"));
                    return;
                }

                var steam = root.Steam;

                if (steam.DisableApiUsage)
                {
                    logger.Debug(ResourceProvider.GetString("Debug_SuccessStory_ApiDisabled"));
                    return;
                }

                if (!string.IsNullOrWhiteSpace(steam.ApiKey) &&
                    string.IsNullOrWhiteSpace(settings.SteamApiKey))
                {
                    settings.SteamApiKey = steam.ApiKey;
                    logger.Debug(ResourceProvider.GetString("Debug_SuccessStory_ImportedApiKey"));
                }

                if (!string.IsNullOrWhiteSpace(steam.AccountId) &&
                    string.IsNullOrWhiteSpace(settings.SteamUserId))
                {
                    settings.SteamUserId = steam.AccountId;
                    logger.Debug(ResourceProvider.GetString("Debug_SuccessStory_ImportedAccountId"));
                }
            }
            catch (Exception e)
            {
                logger.Debug(string.Format(ResourceProvider.GetString("Debug_SuccessStory_ReadError"), e.Message));
            }
        }

        // -----------------------------
        // 4) Achievement metadata cache
        // -----------------------------
        // key = "<gameId:N>|<apiName>", case-insensitive on the whole key
        private static readonly Dictionary<string, AchievementMeta> _achievementMetaCache =
            new Dictionary<string, AchievementMeta>(StringComparer.OrdinalIgnoreCase);

        private static bool _metadataLoaded = false;
        private static readonly object _metadataLock = new object();

        private static string BuildKey(Guid gameId, string apiName)
        {
            return gameId.ToString("N") + "|" + apiName;
        }

        /// <summary>
        /// Load all per-game SuccessStory JSON into the in-memory cache.
        /// One-shot per process.
        /// </summary>
        private static void EnsureAchievementMetadataLoaded(IPlayniteAPI api, ILogger logger)
        {
            if (_metadataLoaded)
            {
                return;
            }

            lock (_metadataLock)
            {
                if (_metadataLoaded)
                {
                    return;
                }

                try
                {
                    LoadAchievementMetadataFromSuccessStoryInternal(api, logger);
                }
                catch (Exception ex)
                {
                    logger.Debug($"SuccessStoryIntegration: error while loading metadata: {ex.Message}");
                }

                _metadataLoaded = true;
            }
        }

        /// <summary>
        /// INTERNAL: actually walks the SuccessStory folder and fills the cache.
        /// </summary>
        private static void LoadAchievementMetadataFromSuccessStoryInternal(
            IPlayniteAPI api,
            ILogger logger)
        {
            var root = Path.Combine(api.Paths.ExtensionsDataPath, SuccessStoryDataId, "SuccessStory");
            if (!Directory.Exists(root))
            {
                logger.Debug($"SuccessStoryIntegration: SuccessStory folder not found: {root}");
                return;
            }

            var files = Directory.GetFiles(root, "*.json", SearchOption.TopDirectoryOnly);
            if (files.Length == 0)
            {
                logger.Debug(ResourceProvider.GetString("Debug_SuccessStory_NoGameFiles"));
                return;
            }

            var added = 0;

            foreach (var file in files)
            {
                try
                {
                    var json = File.ReadAllText(file);

                    if (!Serialization.TryFromJson(json, out SuccessStoryGameFile gameFile) || gameFile == null)
                    {
                        continue;
                    }

                    var gameId = gameFile.Id;
                    if (gameId == Guid.Empty || gameFile.Items == null)
                    {
                        continue;
                    }

                    foreach (var a in gameFile.Items)
                    {
                        if (a == null || string.IsNullOrWhiteSpace(a.ApiName))
                        {
                            continue;
                        }

                        var key = BuildKey(gameId, a.ApiName);

                        // First writer wins; we don't really expect duplicates but it's harmless.
                        if (!_achievementMetaCache.ContainsKey(key))
                        {
                            _achievementMetaCache[key] = new AchievementMeta
                            {
                                Name = a.Name,
                                Description = a.Description,
                                IconUnlocked = a.UrlUnlocked,
                                IconLocked = a.UrlLocked
                            };
                            added++;
                        }
                    }
                }
                catch (Exception exFile)
                {
                    logger.Debug(string.Format(ResourceProvider.GetString("Debug_SuccessStory_FileReadError"), file, exFile.Message));
                }
            }

            logger.Debug(string.Format(ResourceProvider.GetString("Debug_SuccessStory_LoadedCount"), added));
        }

        /// <summary>
        /// Public entry point:
        /// Try to get metadata for (gameId, apiName) from SuccessStory's local DB.
        /// Returns false if SuccessStory isn't installed, no file, or no matching item.
        /// </summary>
        public static bool TryGetAchievementMeta(
            IPlayniteAPI api,
            ILogger logger,
            Guid gameId,
            string apiName,
            out AchievementMeta meta)
        {
            meta = null;

            if (gameId == Guid.Empty || string.IsNullOrWhiteSpace(apiName) || api == null)
            {
                return false;
            }

            EnsureAchievementMetadataLoaded(api, logger);

            var key = BuildKey(gameId, apiName);
            return _achievementMetaCache.TryGetValue(key, out meta) && meta != null;
        }
    }
}
