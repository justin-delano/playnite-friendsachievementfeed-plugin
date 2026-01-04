using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        private readonly CacheStorage _storage;

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
            _storage = new CacheStorage(plugin, logger);
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
                return _storage.FriendCacheExists();
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
            if (!Directory.Exists(_storage.FamilySharingDir))
            {
                _familySharing = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                _familySharingLoaded = true;
            }

            if (!Directory.Exists(_storage.SelfCacheRootDir))
            {
                _storage.EnsureDir(_storage.SelfCacheRootDir);
                _selfAchievements = new Dictionary<string, SelfAchievementGameData>(StringComparer.OrdinalIgnoreCase);
            }

            if (!Directory.Exists(_storage.FriendPerGameDir))
            {
                _storage.EnsureDir(_storage.FriendPerGameDir);
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

            if (!_storage.FriendCacheExists())
            {
                _globalFriendFeed = null;
                _perGameFeed = new Dictionary<string, FeedData>(StringComparer.OrdinalIgnoreCase);
                return;
            }

            var global = _storage.ReadFriendFeed() ?? new FeedData();
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

                _storage.EnsureDir(_storage.FriendPerGameDir);

                var expected = BuildPerGameIndex(_globalFriendFeed.Entries, _globalFriendFeed.LastUpdatedUtc);

                var existingFiles = new HashSet<string>(_storage.EnumeratePerGameFiles(), StringComparer.OrdinalIgnoreCase);

                var writtenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var kv in expected)
                {
                    var path = _storage.PerGamePath(kv.Key);
                    _storage.WritePerGameFeed(kv.Key, kv.Value);
                    writtenFiles.Add(path);
                }

                foreach (var stale in existingFiles.Except(writtenFiles))
                {
                    _storage.DeleteFileIfExists(stale);
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
            _storage.EnsureDir(_storage.FriendPerGameDir);

            _globalFriendFeed ??= new FeedData
            {
                LastUpdatedUtc = DateTime.UtcNow,
                Entries = new List<FeedEntry>()
            };

            _globalFriendFeed.LastUpdatedUtc = AsUtc(_globalFriendFeed.LastUpdatedUtc);
            _globalFriendFeed.Entries ??= new List<FeedEntry>();

            _storage.WriteFriendFeed(_globalFriendFeed);

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

                // Entries are already sorted by FriendUnlockTimeUtc in UpdateFriendFeed
                return _globalFriendFeed?.Entries??
                       new List<FeedEntry>();
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

                _storage.DeleteFileIfExists(_storage.FriendGlobalPath);
                _storage.DeleteDirectoryIfExists(_storage.FriendPerGameDir);
                _storage.DeleteDirectoryIfExists(_storage.SelfCacheRootDir);
                _storage.DeleteDirectoryIfExists(_storage.FamilySharingDir);
            }

            RaiseCacheChangedSafe();
        }

        // ---------------------------
        // Family sharing (folder optional)
        // New layout:
        //   family_sharing/{playniteGameId}.json  => { SteamIds: [ "steamId64", ... ] }
        // Perf improvement: skip disk writes if no changes; keep deterministic ordering.
        // ---------------------------

        private string FamilySharingPath(string playniteGameId) => _storage.FamilyPath(playniteGameId);

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

            var files = _storage.EnumerateFamilyFiles()?.ToList();
            if (files == null || files.Count == 0)
            {
                _familySharing = map;
                _familySharingLoaded = true;
                return;
            }

            foreach (var f in files)
            {
                var key = Path.GetFileNameWithoutExtension(f);
                if (string.IsNullOrWhiteSpace(key)) continue;

                // Only accept GUID-named files (playnite game id).
                if (!Guid.TryParse(key, out _))
                    continue;

                var data = _storage.ReadFamily(f);
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
                _storage.EnsureDir(_storage.FamilySharingDir);
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

                    _storage.WriteFamily(playniteGameId, new FamilySharingScanResult
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

                    var disk = _storage.ReadSelf(k);
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
                    var toWrite = data ?? new SelfAchievementGameData();
                    toWrite.LastUpdatedUtc = AsUtc(toWrite.LastUpdatedUtc);

                    var k = SelfKey(key);
                    _storage.WriteSelf(k, toWrite);

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
