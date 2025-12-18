using Playnite.SDK;
using Playnite.SDK.Plugins;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using Playnite.SDK.Data;
using Common;

namespace FriendsAchievementFeed.Models
{
    public class FriendsAchievementFeedSettings : ObservableObjectPlus, ISettings
    {
        private readonly FriendsAchievementFeedPlugin _plugin;

        private FriendsAchievementFeedSettings _editingClone;

        private string _steamApiKey;
        private string _steamUserId;
        private int _maxFeedItems = 100;
        private int _rebuildParallelism = 4;
        private bool _enablePeriodicUpdates = true;
        private int _periodicUpdateHours = 4;
        private bool _enableNotifications = true;
        private bool _notifyPeriodicUpdates = true;
        private bool _notifyOnRebuild = true;
        private int _friendAvatarSize = 32;
        private int _achievementIconSize = 40;
        private int _gameFeedTabHeight = 1000;
        private bool _hideAchievementsLockedForYou = false;
        private bool _includeMyUnlockTime = false;
        private bool _hasGameFeedGroups;
        public string SteamApiKey
        {
            get => _steamApiKey;
            set => SetValue(ref _steamApiKey, value);
        }

        public string SteamUserId
        {
            get => _steamUserId;
            set => SetValue(ref _steamUserId, value);
        }

        public int MaxFeedItems
        {
            get => _maxFeedItems;
            set => SetValue(ref _maxFeedItems, value);
        }

        /// <summary>
        /// Degree of parallelism used when rebuilding the cache.
        /// </summary>
        public int RebuildParallelism
        {
            get => _rebuildParallelism;
            set => SetValue(ref _rebuildParallelism, value);
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
        /// Enable the background periodic incremental updates.
        /// </summary>
        public bool EnablePeriodicUpdates
        {
            get => _enablePeriodicUpdates;
            set => SetValue(ref _enablePeriodicUpdates, value);
        }

        /// <summary>
        /// Hours between periodic background incremental updates.
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
        /// Runtime-only flag: does the currently selected game have any feed groups?
        /// Used by themes to hide/show the FusionX tab.
        /// </summary>
        [DontSerialize] // avoid persisting this to config.json
        public bool HasGameFeedGroups
        {
            get => _hasGameFeedGroups;
            set => SetValue(ref _hasGameFeedGroups, value);
        }

        // Parameterless ctor for deserialization
        public FriendsAchievementFeedSettings()
        {
        }

        public FriendsAchievementFeedSettings(FriendsAchievementFeedPlugin plugin)
        {
            _plugin = plugin;

            var saved = _plugin.LoadPluginSettings<FriendsAchievementFeedSettings>();
            if (saved != null)
            {
                SteamApiKey = saved.SteamApiKey;
                SteamUserId = saved.SteamUserId;
                MaxFeedItems = saved.MaxFeedItems;
                RebuildParallelism = saved.RebuildParallelism;
                EnablePeriodicUpdates = saved.EnablePeriodicUpdates;
                PeriodicUpdateHours = saved.PeriodicUpdateHours;
                FriendAvatarSize = saved.FriendAvatarSize;
                AchievementIconSize = saved.AchievementIconSize;
                GameFeedTabHeight = saved.GameFeedTabHeight;
                HideAchievementsLockedForYou = saved.HideAchievementsLockedForYou;
                IncludeMyUnlockTime = saved.IncludeMyUnlockTime;
                EnableNotifications = saved.EnableNotifications;
                NotifyPeriodicUpdates = saved.NotifyPeriodicUpdates;
                NotifyOnRebuild = saved.NotifyOnRebuild;
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
                SteamApiKey = _editingClone.SteamApiKey;
                SteamUserId = _editingClone.SteamUserId;
                MaxFeedItems = _editingClone.MaxFeedItems;
                RebuildParallelism = _editingClone.RebuildParallelism;
                EnablePeriodicUpdates = _editingClone.EnablePeriodicUpdates;
                PeriodicUpdateHours = _editingClone.PeriodicUpdateHours;
                FriendAvatarSize = _editingClone.FriendAvatarSize;
                AchievementIconSize = _editingClone.AchievementIconSize;
                GameFeedTabHeight = _editingClone.GameFeedTabHeight;
                HideAchievementsLockedForYou = _editingClone.HideAchievementsLockedForYou;
                IncludeMyUnlockTime = _editingClone.IncludeMyUnlockTime;
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

            if (string.IsNullOrWhiteSpace(SteamApiKey))
            {
                errors.Add(ResourceProvider.GetString("LOCFriendsAchFeed_Error_MissingSteamApiKey"));
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
