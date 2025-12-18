using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading.Tasks;
using FriendsAchievementFeed.Models;
using Playnite.SDK;

namespace FriendsAchievementFeed.Services
{
    public class CachedAchievementData
    {
        public DateTime LastUpdated { get; set; }
        public List<FeedEntry> Entries { get; set; } = new List<FeedEntry>();
    }

    public class ProgressReport
    {
        public string Message { get; set; }
        public int CurrentStep { get; set; }
        public int TotalSteps { get; set; }
        public double PercentComplete => TotalSteps > 0 ? (double)CurrentStep / TotalSteps * 100 : 0;
    }

    public class CacheService
    {
        private readonly IPlayniteAPI _api;
        private readonly ILogger _logger;
        private readonly string _cacheFilePath;
        private CachedAchievementData _cache;
        private readonly object _cacheLock = new object();

        public event EventHandler CacheChanged;

        private void OnCacheChanged()
        {
            try
            {
                CacheChanged?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception e)
            {
                _logger.Error(e, "Error while notifying CacheChanged subscribers.");
            }
        }

        public CacheService(IPlayniteAPI api, ILogger logger)
        {
            _api = api;
            _logger = logger;

            var cacheDir = Path.Combine(_api.Paths.ExtensionsDataPath, "FriendsAchievementFeed");
            if (!Directory.Exists(cacheDir))
            {
                Directory.CreateDirectory(cacheDir);
            }

            _cacheFilePath = Path.Combine(cacheDir, "achievement_cache.json");
        }

        public bool IsCacheValid()
        {
            lock (_cacheLock)
            {
                if (_cache == null)
                {
                    LoadCache();
                }

                // Cache is valid if it exists and has data
                return _cache != null && _cache.Entries.Any();
            }
        }

        public DateTime? GetCacheLastUpdated()
        {
            lock (_cacheLock)
            {
                if (_cache == null)
                {
                    LoadCache();
                }
                return _cache?.LastUpdated;
            }
        }

        public List<FeedEntry> GetCachedEntries()
        {
            lock (_cacheLock)
            {
                if (_cache == null)
                {
                    LoadCache();
                }
                return _cache?.Entries?
                    .OrderByDescending(e => e.UnlockTime)
                    .ToList() ?? new List<FeedEntry>();
            }
        }

        public List<FeedEntry> GetRecentEntries(int count = 50)
        {
            return GetCachedEntries().Take(count).ToList();
        }

        public void UpdateCache(List<FeedEntry> entries)
        {
            lock (_cacheLock)
            {
                _cache = new CachedAchievementData
                {
                    LastUpdated = DateTime.UtcNow,
                    Entries = entries?.ToList() ?? new List<FeedEntry>()
                };
                SaveCache();
            }

            OnCacheChanged();
        }

        /// <summary>
        /// Merge new entries into the existing cache, avoiding duplicates by Id,
        /// and update the LastUpdated timestamp.
        /// </summary>
        public void MergeUpdateCache(List<FeedEntry> newEntries)
        {
            lock (_cacheLock)
            {
                if (_cache == null)
                {
                    LoadCache();
                }

                var existing = _cache?.Entries?.ToList() ?? new List<FeedEntry>();

                // Combine and deduplicate by Id
                var combined = existing
                    .Concat(newEntries ?? Enumerable.Empty<FeedEntry>())
                    .GroupBy(e => e.Id)
                    .Select(g => g.OrderByDescending(x => x.UnlockTime).First())
                    .OrderByDescending(e => e.UnlockTime)
                    .ToList();

                _cache = new CachedAchievementData
                {
                    LastUpdated = DateTime.UtcNow,
                    Entries = combined
                };

                SaveCache();
            }
            OnCacheChanged();
        }

        private void LoadCache()
        {
            try
            {
                if (File.Exists(_cacheFilePath))
                {
                    using (var stream = File.OpenRead(_cacheFilePath))
                    {
                        var serializer = new DataContractJsonSerializer(typeof(CachedAchievementData));
                        _cache = (CachedAchievementData)serializer.ReadObject(stream);
                    }
                    _logger.Debug(string.Format(ResourceProvider.GetString("Debug_CacheLoaded"), _cache?.Entries?.Count ?? 0, _cacheFilePath));
                }
                else
                {
                    _logger.Debug(ResourceProvider.GetString("Debug_NoCacheFile"));
                    _cache = null;
                }
            }
            catch (Exception e)
            {
                _logger.Error(e, "Failed to load cache from disk.");
                _cache = null;
            }
        }

        private void SaveCache()
        {
            try
            {
                using (var stream = new FileStream(_cacheFilePath, FileMode.Create))
                {
                    var serializer = new DataContractJsonSerializer(typeof(CachedAchievementData));
                    serializer.WriteObject(stream, _cache);
                }
                _logger.Debug(string.Format(ResourceProvider.GetString("Debug_CacheSaved"), _cache?.Entries?.Count ?? 0, _cacheFilePath));
            }
            catch (Exception e)
            {
                _logger.Error(e, "Failed to save cache to disk.");
            }
        }

        public void ClearCache()
        {
            lock (_cacheLock)
            {
                _cache = null;
                try
                {
                    if (File.Exists(_cacheFilePath))
                    {
                        File.Delete(_cacheFilePath);
                        _logger.Debug(ResourceProvider.GetString("Debug_CacheDeleted"));
                    }
                }
                catch (Exception e)
                {
                    _logger.Error(e, "Failed to delete cache file.");
                }
            }
            OnCacheChanged();
        }
    }
}
