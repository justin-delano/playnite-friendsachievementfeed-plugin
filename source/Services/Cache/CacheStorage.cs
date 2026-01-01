using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using FriendsAchievementFeed.Models;
using Playnite.SDK;

namespace FriendsAchievementFeed.Services
{
    // Centralizes disk paths and atomic JSON read/write for cache artifacts.
    public sealed class CacheStorage
    {
        private readonly ILogger _logger;

        public string BaseDir { get; }
        public string FriendPerGameDir { get; }
        public string FriendGlobalPath { get; }
        public string SelfCacheRootDir { get; }
        public string FamilySharingDir { get; }

        public CacheStorage(FriendsAchievementFeedPlugin plugin, ILogger logger)
        {
            _logger = logger;
            if (plugin == null) throw new ArgumentNullException(nameof(plugin));

            BaseDir = plugin.GetPluginUserDataPath();
            FriendPerGameDir = Path.Combine(BaseDir, "friend_achievement_cache");
            FriendGlobalPath = Path.Combine(BaseDir, "friend_achievement_cache.json");
            SelfCacheRootDir = Path.Combine(BaseDir, "self_achievement_cache");
            FamilySharingDir = Path.Combine(BaseDir, "family_sharing");

            EnsureDir(BaseDir);
            EnsureDir(FriendPerGameDir);
            EnsureDir(SelfCacheRootDir);
        }

        public bool FriendCacheExists() => File.Exists(FriendGlobalPath);

        public void EnsureDir(string dir)
        {
            if (string.IsNullOrWhiteSpace(dir)) return;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        }

        public IEnumerable<string> EnumeratePerGameFiles()
        {
            return Directory.Exists(FriendPerGameDir)
                ? Directory.EnumerateFiles(FriendPerGameDir, "*.json")
                : Enumerable.Empty<string>();
        }

        public IEnumerable<string> EnumerateFamilyFiles()
        {
            return Directory.Exists(FamilySharingDir)
                ? Directory.EnumerateFiles(FamilySharingDir, "*.json")
                : Enumerable.Empty<string>();
        }

        public string PerGamePath(string key) => Path.Combine(FriendPerGameDir, key + ".json");
        public string FamilyPath(string playniteGameId) => Path.Combine(FamilySharingDir, playniteGameId + ".json");
        public string SelfPath(string key) => Path.Combine(SelfCacheRootDir, key + ".json");

        public FeedData ReadFriendFeed() => AtomicJson.Read<FeedData>(FriendGlobalPath);
        public void WriteFriendFeed(FeedData data) => AtomicJson.WriteAtomic(FriendGlobalPath, data);
        public void WritePerGameFeed(string key, FeedData data)
        {
            EnsureDir(FriendPerGameDir);
            AtomicJson.WriteAtomic(PerGamePath(key), data);
        }

        public SelfAchievementGameData ReadSelf(string key) => AtomicJson.Read<SelfAchievementGameData>(SelfPath(key));
        public void WriteSelf(string key, SelfAchievementGameData data)
        {
            EnsureDir(SelfCacheRootDir);
            AtomicJson.WriteAtomic(SelfPath(key), data);
        }

        public FamilySharingScanResult ReadFamily(string path) => AtomicJson.Read<FamilySharingScanResult>(path);
        public void WriteFamily(string playniteGameId, FamilySharingScanResult data)
        {
            EnsureDir(FamilySharingDir);
            AtomicJson.WriteAtomic(FamilyPath(playniteGameId), data);
        }

        public void DeleteFileIfExists(string path)
        {
            if (File.Exists(path)) File.Delete(path);
        }

        public void DeleteDirectoryIfExists(string dir)
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }

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
    }
}
