using System;
using System.Collections.Generic;
using Common;
using Playnite.SDK.Data;
namespace FriendsAchievementFeed.Models
{
    public class FeedEntry : ObservableObjectPlus
    {
        // Default user achievement icon: prefer copied output file, fall back to compiled pack URI
        private static string DefaultSelfAchIconPackUri
        {
            get
            {
                try
                {
                    var outPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "HiddenAchIcon.png");
                    if (System.IO.File.Exists(outPath))
                    {
                        return new Uri(outPath).AbsoluteUri; // file:/// URI
                    }
                }
                catch
                {
                    // ignore and fall back to pack URI
                }

                return "pack://application:,,,/FriendsAchievementFeed;component/Resources/HiddenAchIcon.png";
            }
        }

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

        // pushed in by FeedControlLogic for each cloned entry
        private bool _hideAchievementsLockedForSelf;
        public bool HideAchievementsLockedForSelf
        {
            get => _hideAchievementsLockedForSelf;
            set
            {
                SetValue(ref _hideAchievementsLockedForSelf, value);
                RaiseSpoilerUiChanged();
            }
        }

        private string _selfAchievementIcon;
        [DontSerialize]
        public string SelfAchievementIcon
        {
            get => _selfAchievementIcon;
            set
            {
                SetValue(ref _selfAchievementIcon, value);
                RaiseSpoilerUiChanged();
            }
        }

        private string _friendAchievementIcon;
        public string FriendAchievementIcon
        {
            get => _friendAchievementIcon;
            set
            {
                SetValue(ref _friendAchievementIcon, value);
                RaiseSpoilerUiChanged();
            }
        }

        public string SelfAchievementIconResolved =>
            !string.IsNullOrWhiteSpace(SelfAchievementIcon)
                ? SelfAchievementIcon
                : DefaultSelfAchIconPackUri;

        // reveal allowed only when setting ON and not unlocked
        public bool CanReveal => HideAchievementsLockedForSelf && SelfUnlockTime == null;

        // single binding for icon
        public string DisplayIcon
        {
            get
            {
                // If not revealable, always show real icon
                if (!CanReveal)
                {
                    return !string.IsNullOrWhiteSpace(FriendAchievementIcon)
                        ? FriendAchievementIcon
                        : SelfAchievementIconResolved;
                }

                // Revealable: show user/hidden icon unless revealed
                if (!IsRevealed)
                {
                    return SelfAchievementIconResolved;
                }

                // Revealed: show real icon
                return !string.IsNullOrWhiteSpace(FriendAchievementIcon)
                    ? FriendAchievementIcon
                    : SelfAchievementIconResolved;
            }
        }

        // Treat this as UTC internally
        private DateTime? _SelfUnlockTime;
        [DontSerialize]
        public DateTime? SelfUnlockTime
        {
            get => _SelfUnlockTime;
            set
            {
                DateTime? utc = null;
                if (value.HasValue)
                {
                    var v = value.Value;
                    if (v.Kind == DateTimeKind.Utc) utc = v;
                    else if (v.Kind == DateTimeKind.Local) utc = v.ToUniversalTime();
                    else utc = DateTime.SpecifyKind(v, DateTimeKind.Utc);
                }

                SetValue(ref _SelfUnlockTime, utc);
                OnPropertyChanged(nameof(SelfUnlockTimeLocal));
                RaiseSpoilerUiChanged();
            }
        }

        public DateTime? SelfUnlockTimeLocal => _SelfUnlockTime?.ToLocalTime();

        private DateTime _friendUnlockTime;
        public DateTime FriendUnlockTime
        {
            get => DateTime.SpecifyKind(_friendUnlockTime, DateTimeKind.Utc);
            set
            {
                var v = value;
                if (v.Kind == DateTimeKind.Local) v = v.ToUniversalTime();
                else if (v.Kind == DateTimeKind.Unspecified) v = DateTime.SpecifyKind(v, DateTimeKind.Utc);

                SetValue(ref _friendUnlockTime, v);
                OnPropertyChanged(nameof(FriendUnlockTimeLocal));
            }
        }

        public DateTime FriendUnlockTimeLocal => FriendUnlockTime.ToLocalTime();

        // Expose explicit UTC property for persisted cache shape.
        public DateTime FriendUnlockTimeUtc
        {
            get => DateTime.SpecifyKind(_friendUnlockTime, DateTimeKind.Utc);
            set
            {
                var v = value;
                if (v.Kind == DateTimeKind.Local) v = v.ToUniversalTime();
                else if (v.Kind == DateTimeKind.Unspecified) v = DateTime.SpecifyKind(v, DateTimeKind.Utc);

                SetValue(ref _friendUnlockTime, v);
                OnPropertyChanged(nameof(FriendUnlockTimeLocal));
            }
        }

        private bool _isRevealed = false;
        public bool IsRevealed
        {
            get => _isRevealed;
            set
            {
                SetValue(ref _isRevealed, value);
                RaiseSpoilerUiChanged();
            }
        }

        private void RaiseSpoilerUiChanged()
        {
            OnPropertyChanged(nameof(CanReveal));
            OnPropertyChanged(nameof(SelfAchievementIconResolved));
            OnPropertyChanged(nameof(DisplayIcon));
        }
}


    public class SteamFriend
    {
        public string SteamId { get; set; }
        public string PersonaName { get; set; }
        public string AvatarMediumUrl { get; set; }
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
    public class ProgressReport
    {
        public string Message { get; set; }
        public int CurrentStep { get; set; }
        public int TotalSteps { get; set; }
        public double PercentComplete => TotalSteps > 0 ? (double)CurrentStep / TotalSteps * 100 : 0;
        // Indicates whether this report represents a cancellation outcome.
        public bool IsCanceled { get; set; } = false;
    }
}

