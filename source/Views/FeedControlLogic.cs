using System;
using System.ComponentModel;
using System.Threading.Tasks;
using FriendsAchievementFeed.Controllers;
using FriendsAchievementFeed.ViewModels;
using FriendsAchievementFeed.Services;
using FriendsAchievementFeed.Models;
using Playnite.SDK;

namespace FriendsAchievementFeed.Views
{
    /// <summary>
    /// Simplified feed control logic that delegates to controller and view model.
    /// The original large implementation has been split into FeedController and FeedViewModel.
    /// </summary>
    public class FeedControlLogic : INotifyPropertyChanged, IDisposable
    {
        private readonly FeedController _controller;
        private readonly FeedViewModel _viewModel;
        private readonly FriendsAchievementFeedSettings _settings;
        private readonly ILogger _logger;

        public event PropertyChangedEventHandler PropertyChanged;

        public FeedControlLogic(IPlayniteAPI api, FriendsAchievementFeedSettings settings, FeedManager feedService)
        {
            _logger = LogManager.GetLogger(nameof(FeedControlLogic));
            _settings = settings;
            
            // Initialize the controller and view model
            _controller = new FeedController(api, settings, feedService);
            _viewModel = new FeedViewModel();
            
            // Wire up the controller's game providers if needed
            _controller.GameNameProvider = GetGameNameForFilter;
            _controller.GameIdProvider = GetGameIdForFilter;
            _controller.ViewModelProvider = () => _viewModel;

            // Forward PropertyChanged events from view model
            _viewModel.PropertyChanged += (s, e) => PropertyChanged?.Invoke(this, e);
            
            // Hook up settings changes
            if (_settings != null)
            {
                _settings.PropertyChanged += OnSettingsPropertyChanged;
            }
            
            // Initialize the feed with cached data if cache is valid
            if (feedService?.Cache?.IsCacheValid() == true)
            {
                System.Threading.Tasks.Task.Run(async () =>
                {
                    try
                    {
                        await _controller.RefreshFeedAsync(default);
                    }
                    catch (Exception ex)
                    {
                        _logger?.Error(ex, "Error initializing feed from cache");
                    }
                });
            }
        }

        #region Public Properties (Delegated to ViewModel)

        public System.Collections.ObjectModel.ObservableCollection<FriendsAchievementFeed.Models.FeedEntry> AllEntries => _viewModel.AllEntries;
        public object EntriesView => _viewModel.EntriesView;
        public System.Collections.ObjectModel.ObservableCollection<FriendsAchievementFeed.Models.FeedGroup> GroupedEntries => _viewModel.GroupedEntries;
        public bool HasAnyEntries => _viewModel.HasAnyEntries;

        public System.Collections.ObjectModel.ObservableCollection<string> FriendFilters => _viewModel.FriendFilters;
        public System.Collections.ObjectModel.ObservableCollection<string> GameFilters => _viewModel.GameFilters;

        public string CacheLastUpdatedText
        {
            get => _viewModel.CacheLastUpdatedText;
            set => _viewModel.CacheLastUpdatedText = value;
        }

        public string StatusMessage
        {
            get => _viewModel.StatusMessage;
            set => _viewModel.StatusMessage = value;
        }

        public bool IsLoading
        {
            get => _viewModel.IsLoading;
            set => _viewModel.IsLoading = value;
        }

        public bool ShowProgress
        {
            get => _viewModel.ShowProgress;
            set => _viewModel.ShowProgress = value;
        }

        public double ProgressPercent
        {
            get => _viewModel.ProgressPercent;
            set => _viewModel.ProgressPercent = value;
        }

        public string ProgressDetail
        {
            get => _viewModel.ProgressDetail;
            set => _viewModel.ProgressDetail = value;
        }

        public string FriendSearchText
        {
            get => _viewModel.FriendSearchText;
            set => _viewModel.FriendSearchText = value;
        }

        public string GameSearchText
        {
            get => _viewModel.GameSearchText;
            set => _viewModel.GameSearchText = value;
        }

        public string AchievementSearchText
        {
            get => _viewModel.AchievementSearchText;
            set => _viewModel.AchievementSearchText = value;
        }

        public int IncrementalRecentFriendsCount
        {
            get => _viewModel.IncrementalRecentFriendsCount;
            set => _viewModel.IncrementalRecentFriendsCount = value;
        }

        public int IncrementalRecentGamesPerFriend
        {
            get => _viewModel.IncrementalRecentGamesPerFriend;
            set => _viewModel.IncrementalRecentGamesPerFriend = value;
        }

        #endregion

        #region Settings Properties

        public int FriendAvatarSize => _settings?.FriendAvatarSize ?? 32;
        public int AchievementIconSize => _settings?.AchievementIconSize ?? 40;
        public bool ShowSelfUnlockTime => _settings?.IncludeSelfUnlockTime ?? false;
        public bool HideAchievementsLockedForSelf => _settings?.HideAchievementsLockedForSelf ?? false;

        #endregion

        #region Commands (Delegated to Controller)

        public System.Windows.Input.ICommand TriggerRebuildCommand => _controller.TriggerRebuildCommand;
        public System.Windows.Input.ICommand TriggerIncrementalScanCommand => _controller.TriggerIncrementalScanCommand;
        public System.Windows.Input.ICommand RefreshCommand => _controller.RefreshCommand;
        public System.Windows.Input.ICommand CancelRebuildCommand => _controller.CancelRebuildCommand;
        public System.Windows.Input.ICommand RefreshCurrentGameCommand => _controller.RefreshCurrentGameCommand;
        public System.Windows.Input.ICommand QuickScanCommand => _controller.QuickScanCommand;

        #endregion

        #region Game Provider Functions

        /// <summary>
        /// Override this to provide the current game name for filtering
        /// </summary>
        public Func<string> GameNameProvider { get; set; }

        /// <summary>
        /// Override this to provide the current game ID for filtering
        /// </summary>
        public Func<Guid?> GameIdProvider { get; set; }

        protected virtual string GetGameNameForFilter() => GameNameProvider?.Invoke();
        protected virtual Guid? GetGameIdForFilter() => GameIdProvider?.Invoke();

        #endregion

        #region Settings Change Handler

        private void OnSettingsPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(_settings.FriendAvatarSize):
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FriendAvatarSize)));
                    break;

                case nameof(_settings.AchievementIconSize):
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AchievementIconSize)));
                    break;

                case nameof(_settings.IncludeSelfUnlockTime):
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowSelfUnlockTime)));
                    break;

                case nameof(_settings.HideAchievementsLockedForSelf):
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HideAchievementsLockedForSelf)));
                    
                    // When this setting changes, reapply it to all entries
                    ApplyHideLockedSettingToViewEntries();
                    _viewModel.RefreshView();
                    break;
            }
        }

        private void ApplyHideLockedSettingToViewEntries()
        {
            var hideLocked = HideAchievementsLockedForSelf;
            
            foreach (var entry in _viewModel.AllEntries)
            {
                if (entry != null)
                {
                    entry.HideAchievementsLockedForSelf = hideLocked;
                }
            }
        }

        #endregion

        #region Public Methods

        public void UpdateCacheData(System.Collections.Generic.List<FriendsAchievementFeed.Models.FeedEntry> entries)
        {
            _viewModel.UpdateCacheData(entries);
        }

        public void ResetAllReveals()
        {
            _viewModel.ResetAllReveals();
        }

        public void ToggleReveal(FeedEntry entry)
        {
            // Delegate to view model
            _viewModel.ToggleReveal(entry);
        }

        public async Task RefreshAsync()
        {
            // Delegate to controller
            await _controller.RefreshFeedAsync(default);
        }

        public void NotifyCommandsChanged()
        {
            // Delegate to controller 
            _controller.NotifyCommandsChanged();
        }

        #endregion

        public void Dispose()
        {
            try
            {
                _controller?.Dispose();
                _viewModel?.Dispose();
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Error disposing FeedControlLogic");
            }
        }
    }
}