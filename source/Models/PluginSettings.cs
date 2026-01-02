using Playnite.SDK;
using Playnite.SDK.Plugins;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using Playnite.SDK.Data;
using System.IO;
using Common;
using System.Linq;

namespace FriendsAchievementFeed.Models
{
    public class FriendsAchievementFeedSettings : ObservableObjectPlus, ISettings
    {
        private readonly FriendsAchievementFeedPlugin _plugin;

        private FriendsAchievementFeedSettings _editingClone;

        private string _steamUserId;
        private string _steamApiKey;
        private string _steamLanguage = "english";
        private int _maxFeedItems = 100;
        private bool _enablePeriodicUpdates = true;
        private int _periodicUpdateHours = 6;
        private bool _enableNotifications = true;
        private bool _notifyPeriodicUpdates = true;
        private bool _notifyOnRebuild = true;
        private int _friendAvatarSize = 32;
        private int _achievementIconSize = 40;
        private int _gameFeedTabHeight = 5000;
        private int _quickScanRecentFriendsCount = 5;
        private int _quickScanRecentGamesPerFriend = 5;
        private bool _hideAchievementsLockedForSelf = false;
        private bool _includeSelfUnlockTime = false;
        private bool _hasGameFeedGroups;
        // Expose paths to cache locations instead of storing full feed entries
        private string _exposedGlobalFeedPath = string.Empty;
        private Dictionary<string, string> _exposedGameFeeds = new Dictionary<string, string>();
        public ObservableCollection<FriendSlot> FriendSlots { get; set; } = new ObservableCollection<FriendSlot>();
        public string SteamUserId
        {
            get => _steamUserId;
            set => SetValue(ref _steamUserId, value);
        }

        /// <summary>
        /// Optional Steam Web API key used for owned games, friends and player summaries.
        /// </summary>
        public string SteamApiKey
        {
            get => _steamApiKey;
            set => SetValue(ref _steamApiKey, value ?? string.Empty);
        }

        /// <summary>
        /// Preferred Steam language code for schema/achievement text (e.g. "english", "spanish").
        /// </summary>
        public string SteamLanguage
        {
            get => _steamLanguage;
            set => SetValue(ref _steamLanguage, value);
        }

        public int MaxFeedItems
        {
            get => _maxFeedItems;
            set => SetValue(ref _maxFeedItems, value);
        }

        /// <summary>
        /// Size (pixels) of friend avatar images in the feed.
        /// </summary>
        public int FriendAvatarSize
        {
            get => _friendAvatarSize;
            set => SetValue(ref _friendAvatarSize, value);
        }

        /// <summary>
        /// Size (pixels) of achievement icon thumbnails in the feed.
        /// </summary>
        public int AchievementIconSize
        {
            get => _achievementIconSize;
            set => SetValue(ref _achievementIconSize, value);
        }

        /// <summary>
        /// Height (pixels) of the Game view's feed tab when applied to the Game view.
        /// </summary>
        public int GameFeedTabHeight
        {
            get => _gameFeedTabHeight;
            set => SetValue(ref _gameFeedTabHeight, value);
        }

        /// <summary>
        /// Maximum recent friends to scan when using Quick Scan.
        /// </summary>
        public int QuickScanRecentFriendsCount
        {
            get => _quickScanRecentFriendsCount;
            set => SetValue(ref _quickScanRecentFriendsCount, Math.Max(0, value));
        }

        /// <summary>
        /// Maximum recent games per friend to scan when using Quick Scan.
        /// </summary>
        public int QuickScanRecentGamesPerFriend
        {
            get => _quickScanRecentGamesPerFriend;
            set => SetValue(ref _quickScanRecentGamesPerFriend, Math.Max(0, value));
        }

        

        /// <summary>
        /// Enable the background periodic updates.
        /// </summary>
        public bool EnablePeriodicUpdates
        {
            get => _enablePeriodicUpdates;
            set => SetValue(ref _enablePeriodicUpdates, value);
        }

        /// <summary>
        /// Hours between periodic background updates.
        /// </summary>
        public int PeriodicUpdateHours
        {
            get => _periodicUpdateHours;
            set => SetValue(ref _periodicUpdateHours, value);
        }

        /// <summary>
        /// Enable non-modal notifications (toasts) from the plugin.
        /// </summary>
        public bool EnableNotifications
        {
            get => _enableNotifications;
            set => SetValue(ref _enableNotifications, value);
        }

        /// <summary>
        /// Show lightweight toast when periodic background updates complete.
        /// </summary>
        public bool NotifyPeriodicUpdates
        {
            get => _notifyPeriodicUpdates;
            set => SetValue(ref _notifyPeriodicUpdates, value);
        }

        /// <summary>
        /// Show a toast when a manual or managed rebuild completes or fails.
        /// </summary>
        public bool NotifyOnRebuild
        {
            get => _notifyOnRebuild;
            set => SetValue(ref _notifyOnRebuild, value);
        }

        /// <summary>
        /// When true, achievements not unlocked by the current account will show a locked icon
        /// and their description will be hidden in the feed.
        /// </summary>
        public bool HideAchievementsLockedForSelf
        {
            get => _hideAchievementsLockedForSelf;
            set => SetValue(ref _hideAchievementsLockedForSelf, value);
        }
        /// <summary>
        /// When true, the feed entries will include the time when the account unlocked the achievement (if unlocked).
        /// </summary>
        public bool IncludeSelfUnlockTime
        {
            get => _includeSelfUnlockTime;
            set => SetValue(ref _includeSelfUnlockTime, value);
        }

        /// <summary>
        /// Runtime-only flag: does the currently selected game have any feed groups?
        /// Used by themes to hide/show the FusionX tab.
        /// </summary>
        [DontSerialize] // avoid persisting this to config.json
        public bool HasGameFeedGroups
        {
            get => _hasGameFeedGroups;
            set => SetValue(ref _hasGameFeedGroups, value);
        }

        /// <summary>
        /// Exposed global feed entries for theme usage. Persisted to plugin settings
        /// so themes (including Fullscreen) can build their own UI.
        /// </summary>
        /// <summary>
        /// Path to the global cache directory for theme usage. Persisted to plugin settings
        /// so themes (including Fullscreen) can read per-game cache files directly.
        /// </summary>
        public string ExposedGlobalFeedPath
        {
            get => _exposedGlobalFeedPath;
            set => SetValue(ref _exposedGlobalFeedPath, value ?? string.Empty);
        }

        /// <summary>
        /// Exposed per-game feed entries keyed by PlayniteGameId (string) or AppId string fallback.
        /// Persisted to plugin settings for theme consumption.
        /// </summary>
        /// <summary>
        /// Exposed per-game feed cache file paths keyed by PlayniteGameId (string) or AppId string fallback.
        /// Persisted to plugin settings for theme consumption.
        /// </summary>
        public Dictionary<string, string> ExposedGameFeeds
        {
            get => _exposedGameFeeds;
            set => SetValue(ref _exposedGameFeeds, value ?? new Dictionary<string, string>());
        }

        /// <summary>
        /// Returns distinct, non-empty friend Steam IDs configured for family sharing.
        /// </summary>
        public IEnumerable<string> GetConfiguredFriendIds()
        {
            return (FriendSlots ?? Enumerable.Empty<FriendSlot>())
                .Select(s => s?.SteamId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Update a friend slot and keep legacy properties in sync for compatibility.
        /// </summary>
        public void SetFriendSlot(int index, string name, string steamId)
        {
            if (index < 0)
            {
                return;
            }

            FriendSlots ??= new ObservableCollection<FriendSlot>();
            EnsureFriendSlotsCapacity(index + 1);

            FriendSlots[index] = new FriendSlot
            {
                Name = name ?? string.Empty,
                SteamId = steamId ?? string.Empty
            };

            // Notify bindings so the UI refreshes cleared/updated slots.
            OnPropertyChanged(nameof(FriendSlots));
        }

        public FriendSlot GetFriendSlot(int index)
        {
            if (index < 0)
            {
                return new FriendSlot();
            }

            FriendSlots ??= new ObservableCollection<FriendSlot>();
            EnsureFriendSlotsCapacity(index + 1);

            return FriendSlots[index] ?? new FriendSlot();
        }

        private void EnsureFriendSlotsCapacity(int size)
        {
            while (FriendSlots.Count < size)
            {
                FriendSlots.Add(new FriendSlot());
            }
        }

        private ObservableCollection<FriendSlot> BuildFriendSlots(FriendsAchievementFeedSettings saved)
        {
            if (saved?.FriendSlots != null && saved.FriendSlots.Any())
            {
                return CloneSlots(saved.FriendSlots);
            }

            return new ObservableCollection<FriendSlot>();
        }

        private ObservableCollection<FriendSlot> CloneSlots(IEnumerable<FriendSlot> source)
        {
            return source != null
                ? new ObservableCollection<FriendSlot>(source.Select(s => s?.Clone() ?? new FriendSlot()))
                : new ObservableCollection<FriendSlot>();
        }

        // Parameterless ctor for deserialization
        public FriendsAchievementFeedSettings()
        {
        }

        public List<int> ForcedScanAppIds { get; set; } = new List<int>();

        public FriendsAchievementFeedSettings(FriendsAchievementFeedPlugin plugin)
        {
            _plugin = plugin;

            var saved = _plugin.LoadPluginSettings<FriendsAchievementFeedSettings>();
            if (saved != null)
            {
                SteamUserId = saved.SteamUserId;
                SteamApiKey = saved.SteamApiKey ?? string.Empty;
                SteamLanguage = string.IsNullOrWhiteSpace(saved.SteamLanguage) ? SteamLanguage : saved.SteamLanguage;
                MaxFeedItems = saved.MaxFeedItems;
                EnablePeriodicUpdates = saved.EnablePeriodicUpdates;
                PeriodicUpdateHours = saved.PeriodicUpdateHours;
                FriendAvatarSize = saved.FriendAvatarSize;
                AchievementIconSize = saved.AchievementIconSize;
                GameFeedTabHeight = saved.GameFeedTabHeight;
                QuickScanRecentFriendsCount = saved.QuickScanRecentFriendsCount;
                QuickScanRecentGamesPerFriend = saved.QuickScanRecentGamesPerFriend;
                HideAchievementsLockedForSelf = saved.HideAchievementsLockedForSelf;
                IncludeSelfUnlockTime = saved.IncludeSelfUnlockTime;
                EnableNotifications = saved.EnableNotifications;
                NotifyPeriodicUpdates = saved.NotifyPeriodicUpdates;
                NotifyOnRebuild = saved.NotifyOnRebuild;
                ExposedGlobalFeedPath = saved.ExposedGlobalFeedPath ?? string.Empty;
                ExposedGameFeeds = saved.ExposedGameFeeds ?? new Dictionary<string, string>();

                FriendSlots = BuildFriendSlots(saved);
                EnsureFriendSlotsCapacity(5);
            }
            else
            {
                EnsureFriendSlotsCapacity(5);
            }
        }

        public void BeginEdit()
        {
            _editingClone = (FriendsAchievementFeedSettings)MemberwiseClone();
            _editingClone.FriendSlots = CloneSlots(FriendSlots);
        }

        public void CancelEdit()
        {
            if (_editingClone != null)
            {
                SteamUserId = _editingClone.SteamUserId;
                SteamApiKey = _editingClone.SteamApiKey;
                SteamLanguage = _editingClone.SteamLanguage;
                MaxFeedItems = _editingClone.MaxFeedItems;
                EnablePeriodicUpdates = _editingClone.EnablePeriodicUpdates;
                PeriodicUpdateHours = _editingClone.PeriodicUpdateHours;
                FriendAvatarSize = _editingClone.FriendAvatarSize;
                AchievementIconSize = _editingClone.AchievementIconSize;
                GameFeedTabHeight = _editingClone.GameFeedTabHeight;
                QuickScanRecentFriendsCount = _editingClone.QuickScanRecentFriendsCount;
                QuickScanRecentGamesPerFriend = _editingClone.QuickScanRecentGamesPerFriend;
                HideAchievementsLockedForSelf = _editingClone.HideAchievementsLockedForSelf;
                IncludeSelfUnlockTime = _editingClone.IncludeSelfUnlockTime;
                EnableNotifications = _editingClone.EnableNotifications;
                NotifyPeriodicUpdates = _editingClone.NotifyPeriodicUpdates;
                NotifyOnRebuild = _editingClone.NotifyOnRebuild;

                FriendSlots = CloneSlots(_editingClone.FriendSlots);
                EnsureFriendSlotsCapacity(5);
            }
        }

        public void EndEdit()
        {
            _plugin.SavePluginSettings(this);
        }

        public bool VerifySettings(out List<string> errors)
        {
            errors = new List<string>();

            if (string.IsNullOrWhiteSpace(SteamUserId))
            {
                errors.Add(ResourceProvider.GetString("LOCFriendsAchFeed_Error_MissingSteamUserId"));
            }

            if (MaxFeedItems <= 0)
            {
                errors.Add(ResourceProvider.GetString("LOCFriendsAchFeed_Error_InvalidMaxFeedItems"));
            }

            // return true if there are no errors
            return errors.Count == 0;
        }
    }
}
