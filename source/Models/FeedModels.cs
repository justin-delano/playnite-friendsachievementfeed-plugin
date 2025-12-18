using Playnite.SDK;
using Playnite.SDK.Plugins;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using Common;
namespace FriendsAchievementFeed.Models
{
    public class FeedEntry : ObservableObjectPlus
    {
        public string Id { get; set; }

        public string FriendSteamId { get; set; }
        public string FriendPersonaName { get; set; }
        public string FriendAvatarUrl { get; set; }

        public string GameName { get; set; }
        public Guid? PlayniteGameId { get; set; }
        public int AppId { get; set; }

        public string AchievementApiName { get; set; }
        public string AchievementDisplayName { get; set; }
        public string AchievementDescription { get; set; }

        private string _achievementIconUrl;
        public string AchievementIconUrl
        {
            get => _achievementIconUrl;
            set => SetValue(ref _achievementIconUrl, value);
        }

        // unlocked icon URL (original); used when revealing a locked achievement
        private string _achievementIconUnlockedUrl;
        public string AchievementIconUnlockedUrl
        {
            get => _achievementIconUnlockedUrl;
            set => SetValue(ref _achievementIconUnlockedUrl, value);
        }

        private bool _hideDescription;
        public bool HideDescription
        {
            get => _hideDescription;
            set => SetValue(ref _hideDescription, value);
        }

        private DateTime? _myUnlockTime;
        public DateTime? MyUnlockTime
        {
            get => _myUnlockTime;
            set => SetValue(ref _myUnlockTime, value);
        }

        public DateTime UnlockTime { get; set; }

        private bool _isRevealed = false;
        public bool IsRevealed
        {
            get => _isRevealed;
            set => SetValue(ref _isRevealed, value);
        }
    }

    public class SteamFriend
    {
        public string SteamId { get; set; }
        public string PersonaName { get; set; }
        public string AvatarMediumUrl { get; set; }
    }

    public class SteamAchievement
    {
        public string ApiName { get; set; }
        public bool Achieved { get; set; }
        public DateTime? UnlockTime { get; set; }
    }

    public class AchievementMeta
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public string IconUnlocked { get; set; }
        public string IconLocked { get; set; }
    }

    public class FeedGroup
    {
        public string FriendSteamId { get; set; }
        public string FriendPersonaName { get; set; }
        public string FriendAvatarUrl { get; set; }

        public DateTime Date { get; set; }
        public string GameName { get; set; }

        // Global view -> true; per-game view -> false
        public bool ShowGameName { get; set; }

        public string SubheaderText { get; set; }  // e.g. "Thursday, December 12, 2024"

        public List<FeedEntry> Achievements { get; } = new List<FeedEntry>();
    }
}

