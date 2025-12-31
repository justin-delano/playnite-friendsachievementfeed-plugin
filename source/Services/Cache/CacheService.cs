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
    // New format: stored per Playnite game file (Guid filename), containing SteamID64s.
    public class FamilySharingScanResult
    {
        public DateTime LastUpdatedUtc { get; set; }
        public List<string> SteamIds { get; set; } = new List<string>();
    }

    public sealed class CacheService : ICacheService
    {
        private readonly ILogger _logger;
        private readonly string _baseDir;

        // Friend feed (global + per-game)
        private readonly string _friendGlobalPath;   // friend_achievement_cache.json
        private readonly string _friendPerGameDir;   // friend_achievement_cache/*.json

        // Self achievements
        private readonly string _selfCacheRootDir;

        // Family sharing
        private readonly string _familySharingDir;

        private readonly object _sync = new object();

        // In-memory state
        private FeedData _globalFriendFeed;
        private Dictionary<string, FeedData> _perGameFeed =
            new Dictionary<string, FeedData>(StringComparer.OrdinalIgnoreCase);


        // key = "{playniteGameId}" OR "app:{appId}"
        private Dictionary<string, SelfAchievementGameData> _selfAchievements =
            new Dictionary<string, SelfAchievementGameData>(StringComparer.OrdinalIgnoreCase);

        // playniteGameId -> steamId64s
        private Dictionary<string, List<string>> _familySharing =
            new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        private bool _familySharingLoaded;

        public event EventHandler CacheChanged;

        public CacheService(IPlayniteAPI api, ILogger logger, FriendsAchievementFeedPlugin plugin)
        {
            _logger = logger;
            if (plugin == null) throw new ArgumentNullException(nameof(plugin));

            _baseDir = plugin.GetPluginUserDataPath();
            EnsureDir(_baseDir);

            _friendPerGameDir = Path.Combine(_baseDir, "friend_achievement_cache");
            EnsureDir(_friendPerGameDir);

            _friendGlobalPath = Path.Combine(_baseDir, "friend_achievement_cache.json");

            _selfCacheRootDir = Path.Combine(_baseDir, "self_achievement_cache");
            EnsureDir(_selfCacheRootDir);

            _familySharingDir = Path.Combine(_baseDir, "family_sharing");
            // Optional; created on write.
        }

        // ---------------------------
        // Atomic JSON
        // ---------------------------

        private static class AtomicJson
        {
            private static DataContractJsonSerializer Create<T>()
                => new DataContractJsonSerializer(typeof(T),
                    new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true });

            public static T Read<T>(string path) where T : class
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
                    using (var s = File.OpenRead(path))
                    {
                        return Create<T>().ReadObject(s) as T;
                    }
                }
                catch
                {
                    return null;
                }
            }

            public static void WriteAtomic<T>(string path, T data)
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
                    using (var s = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        Create<T>().WriteObject(s, data);
                    }

                    if (File.Exists(path))
                    {
                        try
                        {
                            File.Replace(tmp, path, destinationBackupFileName: null);
                            return;
                        }
                        catch
                        {
                            File.Delete(path);
                        }
                    }

                    File.Move(tmp, path);
                }
                finally
                {
                    if (File.Exists(tmp)) File.Delete(tmp);
                }
            }
        }

        private static void EnsureDir(string dir)
        {
            if (string.IsNullOrWhiteSpace(dir)) return;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        }

        private static DateTime AsUtc(DateTime dt)
        {
            if (dt.Kind == DateTimeKind.Utc) return dt;
            if (dt.Kind == DateTimeKind.Local) return dt.ToUniversalTime();
            return DateTime.SpecifyKind(dt, DateTimeKind.Utc);
        }

        private void RaiseCacheChangedSafe()
        {
            try { CacheChanged?.Invoke(this, EventArgs.Empty); }
            catch (Exception e)
            {
                _logger?.Error(e, ResourceProvider.GetString("LOCFriendsAchFeed_Error_NotifySubscribers"));
            }
        }

        private void ClearAllMemory_NoEvent()
        {
            _globalFriendFeed = null;
            _perGameFeed = new Dictionary<string, FeedData>(StringComparer.OrdinalIgnoreCase);

            _selfAchievements = new Dictionary<string, SelfAchievementGameData>(StringComparer.OrdinalIgnoreCase);

            _familySharing = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            _familySharingLoaded = false;
        }

        // ---------------------------
        // Disk presence policy
        // ---------------------------

        private bool CoreArtifactsPresent()
        {
            try
            {
                return File.Exists(_friendGlobalPath);
            }
            catch
            {
                return false;
            }
        }

        private bool EnsureMemoryMatchesDisk_Locked()
        {
            if (!CoreArtifactsPresent())
            {
                ClearAllMemory_NoEvent();
                return true;
            }

            // Soft optional folders
            if (string.IsNullOrEmpty(_familySharingDir) || !Directory.Exists(_familySharingDir))
            {
                _familySharing = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                _familySharingLoaded = true;
            }

            if (string.IsNullOrEmpty(_selfCacheRootDir) || !Directory.Exists(_selfCacheRootDir))
            {
                EnsureDir(_selfCacheRootDir);
                _selfAchievements = new Dictionary<string, SelfAchievementGameData>(StringComparer.OrdinalIgnoreCase);
            }

            if (string.IsNullOrEmpty(_friendPerGameDir) || !Directory.Exists(_friendPerGameDir))
            {
                EnsureDir(_friendPerGameDir);
                _perGameFeed = new Dictionary<string, FeedData>(StringComparer.OrdinalIgnoreCase);
            }

            return false;
        }

        public void EnsureDiskCacheOrClearMemory()
        {
            bool clearedAll;
            lock (_sync)
            {
                clearedAll = EnsureMemoryMatchesDisk_Locked();
            }
            if (clearedAll) RaiseCacheChangedSafe();
        }

        public bool CacheFileExists() => CoreArtifactsPresent();


        // ---------------------------
        // Friend feed (global + per-game)
        // ---------------------------

        private static string PerGameKey(FeedEntry e)
        {
            if (e == null) return string.Empty;
            if (e.PlayniteGameId.HasValue) return e.PlayniteGameId.Value.ToString();
            if (e.AppId > 0) return "app_" + e.AppId;
            return "unknown";
        }

        private static Dictionary<string, FeedData> BuildPerGameIndex(IEnumerable<FeedEntry> entries, DateTime lastUpdatedUtc)
        {
            return (entries ?? Enumerable.Empty<FeedEntry>())
                .Where(e => e != null)
                .GroupBy(PerGameKey, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    g => g.Key,
                    g => new FeedData
                    {
                        LastUpdatedUtc = lastUpdatedUtc,
                        Entries = g.OrderByDescending(x => x.FriendUnlockTimeUtc).ToList()
                    },
                    StringComparer.OrdinalIgnoreCase);
        }

        private void EnsureFriendFeedLoaded_NoPolicy()
        {
            if (_globalFriendFeed != null) return;

            if (!File.Exists(_friendGlobalPath))
            {
                _globalFriendFeed = null;
                _perGameFeed = new Dictionary<string, FeedData>(StringComparer.OrdinalIgnoreCase);
                return;
            }

            var global = AtomicJson.Read<FeedData>(_friendGlobalPath) ?? new FeedData();
            global.LastUpdatedUtc = AsUtc(global.LastUpdatedUtc);
            global.Entries ??= new List<FeedEntry>();

            _globalFriendFeed = global;
            _perGameFeed = BuildPerGameIndex(_globalFriendFeed.Entries, _globalFriendFeed.LastUpdatedUtc);

            TrySyncPerGameFilesToMatchGlobal_NoPolicy();
        }

        private void TrySyncPerGameFilesToMatchGlobal_NoPolicy()
        {
            try
            {
                if (_globalFriendFeed == null) return;

                EnsureDir(_friendPerGameDir);

                var expected = BuildPerGameIndex(_globalFriendFeed.Entries, _globalFriendFeed.LastUpdatedUtc);

                var existingFiles = new HashSet<string>(
                    Directory.Exists(_friendPerGameDir)
                        ? Directory.EnumerateFiles(_friendPerGameDir, "*.json")
                        : Enumerable.Empty<string>(),
                    StringComparer.OrdinalIgnoreCase);

                var writtenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var kv in expected)
                {
                    var path = Path.Combine(_friendPerGameDir, kv.Key + ".json");
                    AtomicJson.WriteAtomic(path, kv.Value);
                    writtenFiles.Add(path);
                }

                foreach (var stale in existingFiles.Except(writtenFiles))
                {
                    File.Delete(stale);
                }

                _perGameFeed = expected;
            }
            catch (Exception e)
            {
                _logger?.Error(e, ResourceProvider.GetString("LOCFriendsAchFeed_Error_FileOperationFailed"));
            }
        }

        private void SaveFriendFeedToDisk_NoPolicy()
        {
            EnsureDir(_friendPerGameDir);

            _globalFriendFeed ??= new FeedData
            {
                LastUpdatedUtc = DateTime.UtcNow,
                Entries = new List<FeedEntry>()
            };

            _globalFriendFeed.LastUpdatedUtc = AsUtc(_globalFriendFeed.LastUpdatedUtc);
            _globalFriendFeed.Entries ??= new List<FeedEntry>();

            AtomicJson.WriteAtomic(_friendGlobalPath, _globalFriendFeed);

            _perGameFeed = BuildPerGameIndex(_globalFriendFeed.Entries, _globalFriendFeed.LastUpdatedUtc);
            TrySyncPerGameFilesToMatchGlobal_NoPolicy();
        }

        public bool IsCacheValid()
        {
            lock (_sync)
            {
                EnsureMemoryMatchesDisk_Locked();
                if (!CacheFileExists()) return false;

                EnsureFriendFeedLoaded_NoPolicy();
                return _globalFriendFeed?.Entries != null && _globalFriendFeed.Entries.Any();
            }
        }

        public DateTime? GetFriendFeedLastUpdatedUtc()
        {
            lock (_sync)
            {
                EnsureMemoryMatchesDisk_Locked();
                if (!CacheFileExists()) return null;

                EnsureFriendFeedLoaded_NoPolicy();
                return _globalFriendFeed?.LastUpdatedUtc;
            }
        }

        public List<FeedEntry> GetCachedFriendEntries()
        {
            lock (_sync)
            {
                EnsureMemoryMatchesDisk_Locked();
                if (!CacheFileExists()) return new List<FeedEntry>();

                EnsureFriendFeedLoaded_NoPolicy();

                return _globalFriendFeed?.Entries?
                           .OrderByDescending(e => e.FriendUnlockTimeUtc)
                           .ToList()
                       ?? new List<FeedEntry>();
            }
        }

        public List<FeedEntry> GetRecentFriendEntries(int count = 50)
        {
            return GetCachedFriendEntries().Take(count).ToList();
        }

        public void UpdateFriendFeed(List<FeedEntry> entries)
        {
            lock (_sync)
            {
                _globalFriendFeed = new FeedData
                {
                    LastUpdatedUtc = DateTime.UtcNow,
                    Entries = (entries ?? new List<FeedEntry>())
                        .Where(e => e != null)
                        .OrderByDescending(e => e.FriendUnlockTimeUtc)
                        .ToList()
                };

                SaveFriendFeedToDisk_NoPolicy();
            }

            RaiseCacheChangedSafe();
        }

        public void MergeUpdateFriendFeed(List<FeedEntry> newEntries)
        {
            lock (_sync)
            {
                EnsureMemoryMatchesDisk_Locked();
                EnsureFriendFeedLoaded_NoPolicy();
                var existing = _globalFriendFeed?.Entries?.ToList() ?? new List<FeedEntry>();

                var combined = existing
                    .Concat(newEntries ?? Enumerable.Empty<FeedEntry>())
                    .Where(e => e != null && !string.IsNullOrWhiteSpace(e.Id))
                    .GroupBy(e => e.Id, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.OrderByDescending(x => x.FriendUnlockTimeUtc).First())
                    .OrderByDescending(e => e.FriendUnlockTimeUtc)
                    .ToList();

                _globalFriendFeed = new FeedData
                {
                    LastUpdatedUtc = DateTime.UtcNow,
                    Entries = combined
                };

                SaveFriendFeedToDisk_NoPolicy();
            }

            RaiseCacheChangedSafe();
        }

        public void ClearCache()
        {
            lock (_sync)
            {
                ClearAllMemory_NoEvent();

                if (File.Exists(_friendGlobalPath)) File.Delete(_friendGlobalPath);

                if (Directory.Exists(_friendPerGameDir))
                {
                    foreach (var f in Directory.EnumerateFiles(_friendPerGameDir, "*.json"))
                    {
                        File.Delete(f);
                    }
                }

                if (Directory.Exists(_selfCacheRootDir))
                {
                    Directory.Delete(_selfCacheRootDir, true);
                }

                if (Directory.Exists(_familySharingDir))
                {
                    Directory.Delete(_familySharingDir, true);
                }
            }

            RaiseCacheChangedSafe();
        }

        // ---------------------------
        // Family sharing (folder optional)
        // New layout:
        //   family_sharing/{playniteGameId}.json  => { SteamIds: [ "steamId64", ... ] }
        // Perf improvement: skip disk writes if no changes; keep deterministic ordering.
        // ---------------------------

        private string FamilySharingPath(string playniteGameId) => Path.Combine(_familySharingDir, playniteGameId + ".json");

        private static List<string> NormalizeSteamIds(IEnumerable<string> ids)
        {
            return (ids ?? Enumerable.Empty<string>())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private void EnsureFamilySharingLoaded_NoPolicy()
        {
            if (_familySharingLoaded) return;

            var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            if (!Directory.Exists(_familySharingDir))
            {
                _familySharing = map;
                _familySharingLoaded = true;
                return;
            }

            foreach (var f in Directory.EnumerateFiles(_familySharingDir, "*.json"))
            {
                var key = Path.GetFileNameWithoutExtension(f);
                if (string.IsNullOrWhiteSpace(key)) continue;

                // Only accept GUID-named files (playnite game id).
                if (!Guid.TryParse(key, out _))
                    continue;

                var data = AtomicJson.Read<FamilySharingScanResult>(f);
                if (data?.SteamIds != null && data.SteamIds.Count > 0)
                {
                    var normalized = NormalizeSteamIds(data.SteamIds);
                    if (normalized.Count > 0)
                        map[key] = normalized;
                }
            }

            _familySharing = map;
            _familySharingLoaded = true;
        }

        public Dictionary<string, List<string>> LoadAllFamilySharingScanResults()
        {
            lock (_sync)
            {
                EnsureMemoryMatchesDisk_Locked();
                EnsureFamilySharingLoaded_NoPolicy();

                var copy = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in _familySharing)
                    copy[kv.Key] = kv.Value != null ? new List<string>(kv.Value) : new List<string>();

                return copy;
            }
        }

        public void MergeAndSaveFamilySharingScanResults(Dictionary<string, IEnumerable<string>> results)
        {
            if (results == null || results.Count == 0) return;

            lock (_sync)
            {
                EnsureDir(_familySharingDir);
                EnsureFamilySharingLoaded_NoPolicy();

                foreach (var kv in results)
                {
                    var playniteGameId = kv.Key;
                    if (string.IsNullOrWhiteSpace(playniteGameId)) continue;

                    if (!Guid.TryParse(playniteGameId, out _))
                        continue;

                    var incoming = NormalizeSteamIds(kv.Value);
                    if (incoming.Count == 0) continue;

                    _familySharing.TryGetValue(playniteGameId, out var existingList);
                    existingList ??= new List<string>();

                    // Merge
                    var mergedSet = new HashSet<string>(existingList, StringComparer.OrdinalIgnoreCase);
                    foreach (var sid in incoming) mergedSet.Add(sid);

                    var mergedList = mergedSet
                        .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    // PERF: skip write if unchanged
                    var unchanged = existingList.Count == mergedList.Count &&
                                    existingList.All(s => mergedSet.Contains(s));
                    if (unchanged)
                    {
                        _familySharing[playniteGameId] = existingList;
                        continue;
                    }

                    AtomicJson.WriteAtomic(FamilySharingPath(playniteGameId), new FamilySharingScanResult
                    {
                        LastUpdatedUtc = DateTime.UtcNow,
                        SteamIds = mergedList
                    });

                    _familySharing[playniteGameId] = mergedList;
                }
            }
        }

        // ---------------------------
        // Self achievements
        // ---------------------------

        private string SelfKey(string key) => key?.Trim() ?? string.Empty;
        private string SelfPath(string key) => Path.Combine(_selfCacheRootDir, SelfKey(key) + ".json");

        public SelfAchievementGameData LoadSelfAchievementData(string key)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(key)) return null;

                lock (_sync)
                {
                    EnsureMemoryMatchesDisk_Locked();

                    var k = SelfKey(key);
                    if (_selfAchievements.TryGetValue(k, out var cached) && cached != null)
                        return cached;

                    var path = SelfPath(k);
                    var disk = AtomicJson.Read<SelfAchievementGameData>(path);
                    if (disk != null)
                    {
                        disk.LastUpdatedUtc = AsUtc(disk.LastUpdatedUtc);
                        _selfAchievements[k] = disk;
                    }

                    return disk;
                }
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, ResourceProvider.GetString("LOCFriendsAchFeed_Error_FileOperationFailed"));
                return null;
            }
        }

        public void SaveSelfAchievementData(string key, SelfAchievementGameData data)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(key)) return;

                lock (_sync)
                {
                    EnsureDir(_selfCacheRootDir);

                    var toWrite = data ?? new SelfAchievementGameData();
                    toWrite.LastUpdatedUtc = AsUtc(toWrite.LastUpdatedUtc);

                    var k = SelfKey(key);
                    AtomicJson.WriteAtomic(SelfPath(k), toWrite);

                    _selfAchievements[k] = toWrite;
                    RaiseCacheChangedSafe();
                }
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, ResourceProvider.GetString("LOCFriendsAchFeed_Error_FileOperationFailed"));
            }
        }
    }
}
