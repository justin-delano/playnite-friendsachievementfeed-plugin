using Playnite.SDK;
using Playnite.SDK.Plugins;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using Playnite.SDK.Data;
using System.IO;
using Common;

namespace FriendsAchievementFeed.Models
{
    public class FriendsAchievementFeedSettings : ObservableObjectPlus, ISettings
    {
        private readonly FriendsAchievementFeedPlugin _plugin;

        private FriendsAchievementFeedSettings _editingClone;

        private string _steamUserId;
        private string _steamApiKey = string.Empty;
        private string _steamLanguage = "english";
        private int _maxFeedItems = 100;        
        private bool _enablePeriodicUpdates = true;
        private int _periodicUpdateHours = 6;
        private bool _enableNotifications = true;
        private bool _notifyPeriodicUpdates = true;
        private bool _notifyOnRebuild = true;
        private int _friendAvatarSize = 32;
        private int _achievementIconSize = 40;
        private int _gameFeedTabHeight = 1000;
        private bool _hideAchievementsLockedForYou = false;
        private bool _includeMyUnlockTime = false;
        private bool _searchAllMyGames = false;
        private bool _hasGameFeedGroups;
        // Expose paths to cache locations instead of storing full feed entries
        private string _exposedGlobalFeedPath = string.Empty;
        private Dictionary<string, string> _exposedGameFeeds = new Dictionary<string, string>();
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
        /// When true, achievements that you have not unlocked yourself will show a locked icon
        /// and their description will be hidden in the feed.
        /// </summary>
        public bool HideAchievementsLockedForYou
        {
            get => _hideAchievementsLockedForYou;
            set => SetValue(ref _hideAchievementsLockedForYou, value);
        }
        /// <summary>
        /// When true, the feed entries will include the time when your account unlocked the achievement (if you did).
        /// </summary>
        public bool IncludeMyUnlockTime
        {
            get => _includeMyUnlockTime;
            set => SetValue(ref _includeMyUnlockTime, value);
        }

        /// <summary>
        /// When true, searches for friend achievements across all games you own (not only mutual games).
        /// </summary>
        public bool SearchAllMyGames
        {
            get => _searchAllMyGames;
            set => SetValue(ref _searchAllMyGames, value);
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

        // Parameterless ctor for deserialization
        public FriendsAchievementFeedSettings()
        {
        }

        public List<int> ForcedScanAppIds { get; set; } = new List<int>();

        // How often we will re-check forced-scan games per friend (because no playtime delta)
        public int ForcedScanIntervalHours { get; set; } = 24;

        public bool IsForcedScanEnabled(int appId)
            => appId > 0 && ForcedScanAppIds != null && ForcedScanAppIds.Contains(appId);

        public void ToggleForcedScan(int appId)
        {
            if (appId <= 0) return;
            ForcedScanAppIds ??= new List<int>();

            if (ForcedScanAppIds.Contains(appId))
            {
                ForcedScanAppIds.Remove(appId);
            }
            else
            {
                ForcedScanAppIds.Add(appId);
            }
        }

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
                HideAchievementsLockedForYou = saved.HideAchievementsLockedForYou;
                IncludeMyUnlockTime = saved.IncludeMyUnlockTime;
                SearchAllMyGames = saved.SearchAllMyGames;
                EnableNotifications = saved.EnableNotifications;
                NotifyPeriodicUpdates = saved.NotifyPeriodicUpdates;
                NotifyOnRebuild = saved.NotifyOnRebuild;
                ExposedGlobalFeedPath = saved.ExposedGlobalFeedPath ?? string.Empty;
                ExposedGameFeeds = saved.ExposedGameFeeds ?? new Dictionary<string, string>();
            }
        }

        public void BeginEdit()
        {
            _editingClone = (FriendsAchievementFeedSettings)MemberwiseClone();
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
                HideAchievementsLockedForYou = _editingClone.HideAchievementsLockedForYou;
                IncludeMyUnlockTime = _editingClone.IncludeMyUnlockTime;
                SearchAllMyGames = _editingClone.SearchAllMyGames;
                EnableNotifications = _editingClone.EnableNotifications;
                NotifyPeriodicUpdates = _editingClone.NotifyPeriodicUpdates;
                NotifyOnRebuild = _editingClone.NotifyOnRebuild;
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
