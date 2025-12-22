using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using FriendsAchievementFeed;
using FriendsAchievementFeed.Models;
using Playnite.SDK;

namespace FriendsAchievementFeed.Services
{
    public class CachedAchievementData
    {
        public DateTime LastUpdated { get; set; }
        public List<FeedEntry> Entries { get; set; } = new List<FeedEntry>();
    }

    public class FriendPlaytimeCacheData
    {
        public DateTime LastUpdatedUtc { get; set; } = DateTime.MinValue;

        // friendSteamId -> (appId -> playtimeMinutes)
        public Dictionary<string, Dictionary<int, int>> FriendAppPlaytimeMinutes { get; set; }
            = new Dictionary<string, Dictionary<int, int>>(StringComparer.OrdinalIgnoreCase);
    }

    public class ForcedScanStateData
    {
        public DateTime LastUpdatedUtc { get; set; } = DateTime.MinValue;

        // friendSteamId -> (appId -> lastScanUtc)
        public Dictionary<string, Dictionary<int, DateTime>> LastScanUtcByFriendApp { get; set; }
            = new Dictionary<string, Dictionary<int, DateTime>>(StringComparer.OrdinalIgnoreCase);
    }

    public class CacheService
    {
        private readonly IPlayniteAPI _api;
        private readonly ILogger _logger;
        private readonly FriendsAchievementFeedPlugin _plugin;

        private readonly string _cacheDirPath;
        private readonly string _globalCachePath;

        private readonly string _friendPlaytimeCachePath;
        private FriendPlaytimeCacheData _friendPlaytimeCache;

        private readonly string _forcedScanStatePath;
        private ForcedScanStateData _forcedScanState;

        private Dictionary<string, CachedAchievementData> _cachePerGame;
        private CachedAchievementData _globalCache;

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
                _logger?.Error(e, string.Format(ResourceProvider.GetString("LOCFriendsAchFeed_Error_NotifySubscribers")));
            }
        }

        public CacheService(IPlayniteAPI api, ILogger logger, FriendsAchievementFeedPlugin plugin)
        {
            _api = api;
            _logger = logger;
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));

            var baseDir = _plugin.GetPluginUserDataPath();
            if (!Directory.Exists(baseDir))
            {
                Directory.CreateDirectory(baseDir);
            }

            _cacheDirPath = Path.Combine(baseDir, "achievement_cache");
            if (!Directory.Exists(_cacheDirPath))
            {
                Directory.CreateDirectory(_cacheDirPath);
            }

            _globalCachePath = Path.Combine(baseDir, "achievement_cache.json");

            _friendPlaytimeCachePath = Path.Combine(baseDir, "friend_playtime_cache.json");
            _forcedScanStatePath = Path.Combine(baseDir, "forced_scan_state.json");

            _cachePerGame = new Dictionary<string, CachedAchievementData>(StringComparer.OrdinalIgnoreCase);
        }

        // ---------------------------
        // JSON helpers (atomic)
        // ---------------------------

        private static DataContractJsonSerializer CreateJsonSerializer<T>()
        {
            return new DataContractJsonSerializer(typeof(T),
                new DataContractJsonSerializerSettings
                {
                    UseSimpleDictionaryFormat = true
                });
        }

        private static T ReadJsonFile<T>(string path) where T : class
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;

                using (var stream = File.OpenRead(path))
                {
                    var serializer = CreateJsonSerializer<T>();
                    return serializer.ReadObject(stream) as T;
                }
            }
            catch
            {
                return null;
            }
        }

        private static void WriteJsonFileAtomic<T>(string path, T data)
        {
            if (string.IsNullOrWhiteSpace(path)) return;

            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var tmp = path + ".tmp";

            try
            {
                var serializer = CreateJsonSerializer<T>();

                using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    serializer.WriteObject(stream, data);
                }

                if (File.Exists(path))
                {
                    try
                    {
                        // Best atomic replace on Windows
                        File.Replace(tmp, path, destinationBackupFileName: null);
                        return;
                    }
                    catch
                    {
                        // Fall back below
                    }

                    try { File.Delete(path); } catch { }
                }

                File.Move(tmp, path);
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }

        private static DateTime AsUtcKind(DateTime dt)
        {
            if (dt.Kind == DateTimeKind.Utc) return dt;
            if (dt.Kind == DateTimeKind.Local) return dt.ToUniversalTime();
            return DateTime.SpecifyKind(dt, DateTimeKind.Utc);
        }

        // ---------------------------
        // Friend playtime cache
        // ---------------------------

        private static FriendPlaytimeCacheData NormalizeFriendPlaytimeCache(FriendPlaytimeCacheData raw)
        {
            raw ??= new FriendPlaytimeCacheData();

            var normalized = new FriendPlaytimeCacheData
            {
                LastUpdatedUtc = AsUtcKind(raw.LastUpdatedUtc),
                FriendAppPlaytimeMinutes = new Dictionary<string, Dictionary<int, int>>(StringComparer.OrdinalIgnoreCase)
            };

            if (raw.FriendAppPlaytimeMinutes != null)
            {
                foreach (var kv in raw.FriendAppPlaytimeMinutes)
                {
                    if (string.IsNullOrWhiteSpace(kv.Key)) continue;

                    normalized.FriendAppPlaytimeMinutes[kv.Key] =
                        kv.Value != null ? new Dictionary<int, int>(kv.Value) : new Dictionary<int, int>();
                }
            }

            return normalized;
        }

        public Dictionary<string, Dictionary<int, int>> GetFriendPlaytimeCache()
        {
            lock (_cacheLock)
            {
                if (_friendPlaytimeCache == null)
                {
                    _friendPlaytimeCache = NormalizeFriendPlaytimeCache(
                        ReadJsonFile<FriendPlaytimeCacheData>(_friendPlaytimeCachePath));
                }

                // Defensive copy (callers shouldn't mutate internal instance)
                var copy = new Dictionary<string, Dictionary<int, int>>(StringComparer.OrdinalIgnoreCase);
                foreach (var fk in _friendPlaytimeCache.FriendAppPlaytimeMinutes)
                {
                    copy[fk.Key] = fk.Value != null
                        ? new Dictionary<int, int>(fk.Value)
                        : new Dictionary<int, int>();
                }
                return copy;
            }
        }

        public void UpdateFriendPlaytimeCache(Dictionary<string, Dictionary<int, int>> snapshot)
        {
            lock (_cacheLock)
            {
                var normalized = new Dictionary<string, Dictionary<int, int>>(StringComparer.OrdinalIgnoreCase);
                if (snapshot != null)
                {
                    foreach (var kv in snapshot)
                    {
                        if (string.IsNullOrWhiteSpace(kv.Key)) continue;

                        normalized[kv.Key] = kv.Value != null
                            ? new Dictionary<int, int>(kv.Value)
                            : new Dictionary<int, int>();
                    }
                }

                _friendPlaytimeCache = new FriendPlaytimeCacheData
                {
                    LastUpdatedUtc = DateTime.UtcNow,
                    FriendAppPlaytimeMinutes = normalized
                };

                WriteJsonFileAtomic(_friendPlaytimeCachePath, _friendPlaytimeCache);
            }
        }

        // ---------------------------
        // Forced scan state (due gate)
        // ---------------------------

        private static ForcedScanStateData NormalizeForcedScanState(ForcedScanStateData raw)
        {
            raw ??= new ForcedScanStateData();

            var normalized = new ForcedScanStateData
            {
                LastUpdatedUtc = AsUtcKind(raw.LastUpdatedUtc),
                LastScanUtcByFriendApp = new Dictionary<string, Dictionary<int, DateTime>>(StringComparer.OrdinalIgnoreCase)
            };

            if (raw.LastScanUtcByFriendApp != null)
            {
                foreach (var fk in raw.LastScanUtcByFriendApp)
                {
                    if (string.IsNullOrWhiteSpace(fk.Key)) continue;

                    var inner = new Dictionary<int, DateTime>();
                    if (fk.Value != null)
                    {
                        foreach (var ak in fk.Value)
                        {
                            if (ak.Key <= 0) continue;
                            inner[ak.Key] = AsUtcKind(ak.Value);
                        }
                    }

                    normalized.LastScanUtcByFriendApp[fk.Key] = inner;
                }
            }

            return normalized;
        }

        private void LoadForcedScanStateIfNeeded()
        {
            if (_forcedScanState != null) return;

            _forcedScanState = NormalizeForcedScanState(
                ReadJsonFile<ForcedScanStateData>(_forcedScanStatePath));
        }

        private void SaveForcedScanState()
        {
            if (_forcedScanState == null) return;
            WriteJsonFileAtomic(_forcedScanStatePath, _forcedScanState);
        }

        public bool IsForcedScanDue(string friendSteamId, int appId, TimeSpan interval, out DateTime lastScanUtc)
        {
            lastScanUtc = DateTime.MinValue;
            if (string.IsNullOrWhiteSpace(friendSteamId) || appId <= 0) return true;

            lock (_cacheLock)
            {
                LoadForcedScanStateIfNeeded();

                if (_forcedScanState.LastScanUtcByFriendApp.TryGetValue(friendSteamId, out var map) &&
                    map != null &&
                    map.TryGetValue(appId, out var dt))
                {
                    lastScanUtc = AsUtcKind(dt);
                    return (DateTime.UtcNow - lastScanUtc) >= interval;
                }

                return true;
            }
        }

        public void MarkForcedScanAttempt(string friendSteamId, int appId)
        {
            if (string.IsNullOrWhiteSpace(friendSteamId) || appId <= 0) return;

            lock (_cacheLock)
            {
                LoadForcedScanStateIfNeeded();

                if (!_forcedScanState.LastScanUtcByFriendApp.TryGetValue(friendSteamId, out var map) || map == null)
                {
                    map = new Dictionary<int, DateTime>();
                    _forcedScanState.LastScanUtcByFriendApp[friendSteamId] = map;
                }

                map[appId] = DateTime.UtcNow;
                _forcedScanState.LastUpdatedUtc = DateTime.UtcNow;

                SaveForcedScanState();
            }
        }

        // ---------------------------
        // Achievements cache
        // ---------------------------

        public bool IsCacheValid()
        {
            lock (_cacheLock)
            {
                if (!CacheFileExists())
                {
                    _logger?.Debug(ResourceProvider.GetString("Debug_NoCacheFile"));
                    _cachePerGame = new Dictionary<string, CachedAchievementData>(StringComparer.OrdinalIgnoreCase);
                    _globalCache = null;
                    return false;
                }

                if (_globalCache == null)
                {
                    LoadCache();
                }

                return _globalCache?.Entries != null && _globalCache.Entries.Any();
            }
        }

        public DateTime? GetCacheLastUpdated()
        {
            lock (_cacheLock)
            {
                if (_globalCache == null || !CacheFileExists())
                {
                    LoadCache();
                }

                return _globalCache?.LastUpdated;
            }
        }

        public bool CacheFileExists()
        {
            try
            {
                // Global cache is primary
                if (File.Exists(_globalCachePath))
                {
                    return true;
                }

                // Resilience: if global missing but per-game exists, still treat as cache
                if (!string.IsNullOrEmpty(_cacheDirPath) &&
                    Directory.Exists(_cacheDirPath) &&
                    Directory.EnumerateFiles(_cacheDirPath, "*.json").Any())
                {
                    return true;
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        public List<FeedEntry> GetCachedEntries()
        {
            lock (_cacheLock)
            {
                if (_globalCache == null || !CacheFileExists())
                {
                    LoadCache();
                }

                return _globalCache?.Entries?.OrderByDescending(e => e.UnlockTime).ToList()
                    ?? new List<FeedEntry>();
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
                _globalCache = new CachedAchievementData
                {
                    LastUpdated = DateTime.UtcNow,
                    Entries = (entries ?? new List<FeedEntry>())
                        .OrderByDescending(e => e.UnlockTime)
                        .ToList()
                };

                _cachePerGame = (_globalCache.Entries ?? new List<FeedEntry>())
                    .GroupBy(e => e.PlayniteGameId?.ToString() ?? e.AppId.ToString())
                    .ToDictionary(g => g.Key, g => new CachedAchievementData
                    {
                        LastUpdated = _globalCache.LastUpdated,
                        Entries = g.OrderByDescending(x => x.UnlockTime).ToList()
                    }, StringComparer.OrdinalIgnoreCase);

                SaveCache();
            }

            OnCacheChanged();
        }

        public void MergeUpdateCache(List<FeedEntry> newEntries)
        {
            lock (_cacheLock)
            {
                if (_globalCache == null)
                {
                    LoadCache();
                }

                var existing = _globalCache?.Entries?.ToList() ?? new List<FeedEntry>();

                var combined = existing
                    .Concat(newEntries ?? Enumerable.Empty<FeedEntry>())
                    .Where(e => e != null && !string.IsNullOrWhiteSpace(e.Id))
                    .GroupBy(e => e.Id, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.OrderByDescending(x => x.UnlockTime).First())
                    .OrderByDescending(e => e.UnlockTime)
                    .ToList();

                _globalCache = new CachedAchievementData
                {
                    LastUpdated = DateTime.UtcNow,
                    Entries = combined
                };

                _cachePerGame = (_globalCache.Entries ?? new List<FeedEntry>())
                    .GroupBy(e => e.PlayniteGameId?.ToString() ?? e.AppId.ToString())
                    .ToDictionary(g => g.Key, g => new CachedAchievementData
                    {
                        LastUpdated = _globalCache.LastUpdated,
                        Entries = g.OrderByDescending(x => x.UnlockTime).ToList()
                    }, StringComparer.OrdinalIgnoreCase);

                SaveCache();
            }

            OnCacheChanged();
        }

        private void LoadCache()
        {
            try
            {
                if (!CacheFileExists())
                {
                    _logger?.Debug(ResourceProvider.GetString("Debug_NoCacheFile"));
                    _cachePerGame = new Dictionary<string, CachedAchievementData>(StringComparer.OrdinalIgnoreCase);
                    _globalCache = null;
                    return;
                }

                _cachePerGame = new Dictionary<string, CachedAchievementData>(StringComparer.OrdinalIgnoreCase);

                // Prefer global file
                if (File.Exists(_globalCachePath))
                {
                    using (var stream = File.OpenRead(_globalCachePath))
                    {
                        var serializer = new DataContractJsonSerializer(typeof(CachedAchievementData));
                        _globalCache = (CachedAchievementData)serializer.ReadObject(stream) ?? new CachedAchievementData();
                    }

                    _cachePerGame = (_globalCache.Entries ?? new List<FeedEntry>())
                        .GroupBy(e => e.PlayniteGameId?.ToString() ?? e.AppId.ToString())
                        .ToDictionary(g => g.Key, g => new CachedAchievementData
                        {
                            LastUpdated = _globalCache.LastUpdated,
                            Entries = g.OrderByDescending(e => e.UnlockTime).ToList()
                        }, StringComparer.OrdinalIgnoreCase);

                    return;
                }

                // Resilience: rebuild global from per-game files if needed
                if (Directory.Exists(_cacheDirPath))
                {
                    foreach (var f in Directory.GetFiles(_cacheDirPath, "*.json"))
                    {
                        try
                        {
                            var fileName = Path.GetFileNameWithoutExtension(f);
                            using (var stream = File.OpenRead(f))
                            {
                                var serializer = new DataContractJsonSerializer(typeof(CachedAchievementData));
                                var data = (CachedAchievementData)serializer.ReadObject(stream);
                                _cachePerGame[fileName] = data ?? new CachedAchievementData();
                            }
                        }
                        catch (Exception inner)
                        {
                            _logger?.Error(inner, string.Format(ResourceProvider.GetString("LOCFriendsAchFeed_Error_FileOperationFailed")));
                        }
                    }

                    var allEntries = _cachePerGame.Values
                        .SelectMany(v => v.Entries ?? Enumerable.Empty<FeedEntry>())
                        .OrderByDescending(e => e.UnlockTime)
                        .ToList();

                    _globalCache = new CachedAchievementData
                    {
                        LastUpdated = _cachePerGame.Values.Select(v => v.LastUpdated).DefaultIfEmpty(DateTime.UtcNow).Max(),
                        Entries = allEntries
                    };

                    // Write global so future loads are fast/consistent
                    SaveCache();
                    return;
                }

                _globalCache = null;
            }
            catch (Exception e)
            {
                _logger?.Error(e, string.Format(ResourceProvider.GetString("LOCFriendsAchFeed_Error_FileOperationFailed")));
                _cachePerGame = new Dictionary<string, CachedAchievementData>(StringComparer.OrdinalIgnoreCase);
                _globalCache = null;
            }
        }

        private void SaveCache()
        {
            try
            {
                if (!Directory.Exists(_cacheDirPath))
                {
                    Directory.CreateDirectory(_cacheDirPath);
                }

                // Write global cache file
                if (_globalCache == null)
                {
                    _globalCache = new CachedAchievementData
                    {
                        LastUpdated = DateTime.UtcNow,
                        Entries = new List<FeedEntry>()
                    };
                }

                var serializer = new DataContractJsonSerializer(typeof(CachedAchievementData));

                using (var stream = new FileStream(_globalCachePath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    serializer.WriteObject(stream, _globalCache);
                }

                // Derive per-game files from global
                var perGame = (_globalCache.Entries ?? new List<FeedEntry>())
                    .GroupBy(e => e.PlayniteGameId?.ToString() ?? e.AppId.ToString())
                    .ToDictionary(g => g.Key, g => new CachedAchievementData
                    {
                        LastUpdated = _globalCache.LastUpdated,
                        Entries = g.OrderByDescending(e => e.UnlockTime).ToList()
                    }, StringComparer.OrdinalIgnoreCase);

                var existingFiles = Directory.Exists(_cacheDirPath)
                    ? new HashSet<string>(Directory.GetFiles(_cacheDirPath, "*.json"), StringComparer.OrdinalIgnoreCase)
                    : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                var writtenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var kv in perGame)
                {
                    var key = kv.Key;
                    var data = kv.Value ?? new CachedAchievementData { LastUpdated = DateTime.UtcNow, Entries = new List<FeedEntry>() };
                    var path = Path.Combine(_cacheDirPath, $"{key}.json");

                    using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        serializer.WriteObject(stream, data);
                    }

                    writtenFiles.Add(path);
                }

                foreach (var f in existingFiles.Except(writtenFiles))
                {
                    try { File.Delete(f); } catch { }
                }

                var total = perGame?.Values.Sum(v => v?.Entries?.Count ?? 0) ?? 0;
                _logger?.Debug(string.Format(ResourceProvider.GetString("Debug_CacheSaved"), total, _cacheDirPath));
            }
            catch (Exception e)
            {
                _logger?.Error(e, string.Format(ResourceProvider.GetString("LOCFriendsAchFeed_Error_FileOperationFailed")));
            }
        }

        public void ClearCache()
        {
            lock (_cacheLock)
            {
                _cachePerGame = new Dictionary<string, CachedAchievementData>(StringComparer.OrdinalIgnoreCase);
                _globalCache = null;

                try
                {
                    if (Directory.Exists(_cacheDirPath))
                    {
                        foreach (var f in Directory.GetFiles(_cacheDirPath, "*.json"))
                        {
                            try { File.Delete(f); } catch { }
                        }
                        _logger?.Debug(ResourceProvider.GetString("Debug_CacheDeleted"));
                    }
                }
                catch (Exception e)
                {
                    _logger?.Error(e, string.Format(ResourceProvider.GetString("LOCFriendsAchFeed_Error_FileOperationFailed")));
                }

                try
                {
                    if (File.Exists(_globalCachePath))
                    {
                        try { File.Delete(_globalCachePath); } catch { }
                    }
                }
                catch { }

                try
                {
                    if (File.Exists(_friendPlaytimeCachePath))
                    {
                        try { File.Delete(_friendPlaytimeCachePath); } catch { }
                    }
                    _friendPlaytimeCache = null;
                }
                catch { }

                try
                {
                    if (File.Exists(_forcedScanStatePath))
                    {
                        try { File.Delete(_forcedScanStatePath); } catch { }
                    }
                    _forcedScanState = null;
                }
                catch { }
            }

            OnCacheChanged();
        }
    }
}
