using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using FriendsAchievementFeed.Models;
using FriendsAchievementFeed.Services;
using Playnite.SDK;

namespace FriendsAchievementFeed.Views
{
    public class FeedControlLogic : INotifyPropertyChanged, IDisposable
    {
        private readonly IPlayniteAPI _api;
        private readonly FriendsAchievementFeedSettings _settings;
        private readonly ILogger _logger;
        private readonly AchievementFeedService _feedService;

        private readonly AsyncCommand _triggerRebuildCmd;
        private readonly AsyncCommand _refreshCmd;
        private readonly AsyncCommand _cancelRebuildCmd;

        // All cached entries for this view in memory (from JSON cache)
        public ObservableCollection<FeedEntry> AllEntries { get; } = new ObservableCollection<FeedEntry>();

        // View over AllEntries used for filtering
        public ICollectionView EntriesView { get; }

        // Grouped view (friend + day cards)
        public ObservableCollection<FeedGroup> GroupedEntries { get; } = new ObservableCollection<FeedGroup>();

        public bool HasAnyEntries => GroupedEntries != null && GroupedEntries.Count > 0;

        public ObservableCollection<string> FriendFilters { get; } = new ObservableCollection<string>();
        public ObservableCollection<string> GameFilters { get; } = new ObservableCollection<string>();

        public ILogger Logger => _logger;

        public int FriendAvatarSize => _settings?.FriendAvatarSize ?? 32;

        public int AchievementIconSize => _settings?.AchievementIconSize ?? 40;

        private bool _isLoading;
        public bool IsLoading
        {
            get => _isLoading;
            set
            {
                if (!SetField(ref _isLoading, value))
                {
                    return;
                }

                try
                {
                    _triggerRebuildCmd?.RaiseCanExecuteChanged();
                    _refreshCmd?.RaiseCanExecuteChanged();
                }
                catch
                {
                    // ignore command requery errors
                }
            }
        }

        private string _statusMessage;
        public string StatusMessage
        {
            get => _statusMessage;
            set => SetField(ref _statusMessage, value);
        }

        private double _progressPercent;
        public double ProgressPercent
        {
            get => _progressPercent;
            set => SetField(ref _progressPercent, value);
        }

        private bool _showProgress;
        public bool ShowProgress
        {
            get => _showProgress;
            set => SetField(ref _showProgress, value);
        }

        private string _cacheLastUpdatedText = "";
        public string CacheLastUpdatedText
        {
            get => _cacheLastUpdatedText;
            set => SetField(ref _cacheLastUpdatedText, value);
        }

        private string _friendSearchText = "";
        public string FriendSearchText
        {
            get => _friendSearchText;
            set
            {
                if (!SetField(ref _friendSearchText, value))
                {
                    return;
                }
                QueueRefreshView();
            }
        }

        private string _gameSearchText = "";
        public string GameSearchText
        {
            get => _gameSearchText;
            set
            {
                if (!SetField(ref _gameSearchText, value))
                {
                    return;
                }
                QueueRefreshView();
            }
        }

        private string _achievementSearchText = "";
        public string AchievementSearchText
        {
            get => _achievementSearchText;
            set
            {
                if (!SetField(ref _achievementSearchText, value))
                {
                    return;
                }
                QueueRefreshView();
            }
        }

        // When no text filters are active and defaultVisibleIds != null,
        // only entries whose Id is in this set are shown.
        private HashSet<string> _defaultVisibleIds;

        // Debounce token for filter refresh
        private CancellationTokenSource _filterCts;

        // Track only entries that have been temporarily revealed to avoid iterating the whole feed.
        // (We still clear this set when resetting, but ResetAllReveals now re-hides all entries.)
        private readonly HashSet<string> _revealedEntryIds = new HashSet<string>(StringComparer.Ordinal);
        private readonly object _revealedLock = new object();

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        private bool SetField<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return false;
            }

            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        /// <summary>
        /// Optional provider that returns the current Game.Name for per-game views.
        /// Null = global view.
        /// </summary>
        public Func<string> GameNameProvider { get; set; }

        public FeedControlLogic(
            IPlayniteAPI api,
            FriendsAchievementFeedSettings settings,
            ILogger logger,
            AchievementFeedService feedService)
        {
            _api = api;
            _settings = settings;
            _logger = logger;
            _feedService = feedService;

            _feedService.CacheChanged += FeedService_CacheChanged;
            _feedService.RebuildProgress += Service_RebuildProgress;

            _triggerRebuildCmd = new AsyncCommand(async p =>
            {
                if (p is CacheRebuildMode mode)
                {
                    await TriggerRebuild(mode, CancellationToken.None).ConfigureAwait(false);
                }
            }, _ => !IsLoading);

            _refreshCmd = new AsyncCommand(
                async _ => await RefreshAsync().ConfigureAwait(false),
                _ => !IsLoading);

            _cancelRebuildCmd = new AsyncCommand(async _ =>
            {
                CancelRebuild();
                await Task.CompletedTask;
            }, _ => _feedService.IsRebuilding);

            TriggerRebuildCommand = _triggerRebuildCmd;
            RefreshCommand = _refreshCmd;
            CancelRebuildCommand = _cancelRebuildCmd;

            EntriesView = CollectionViewSource.GetDefaultView(AllEntries);
            EntriesView.Filter = o => FilterEntry(o as FeedEntry);

            GroupedEntries.CollectionChanged += (s, e) =>
            {
                OnPropertyChanged(nameof(HasAnyEntries));
            };

            try
            {
                // Reflect settings-driven flags
                if (_settings != null)
                {
                    _settings.PropertyChanged += (s, e) =>
                    {
                        // propagate settings changes to UI bindings
                        switch (e.PropertyName)
                        {
                            case nameof(_settings.FriendAvatarSize):
                                OnPropertyChanged(nameof(FriendAvatarSize));
                                break;
                            case nameof(_settings.AchievementIconSize):
                                OnPropertyChanged(nameof(AchievementIconSize));
                                break;
                            case nameof(_settings.IncludeMyUnlockTime):
                                        OnPropertyChanged(nameof(ShowMyUnlockTime));
                                        Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                                        {
                                            try { EntriesView.Refresh(); RebuildFilterLists(); RefreshViewAndGroups(); } catch { }
                                        }));
                                break;
                        }
                    };
                }

                var last = _feedService.GetLastRebuildProgress();
                var lastStatus = _feedService.GetLastRebuildStatus();
                if (_feedService.IsRebuilding)
                {
                    if (last != null)
                    {
                        Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            ProgressPercent = last.PercentComplete;
                            StatusMessage = !string.IsNullOrWhiteSpace(last.Message) ? last.Message : (lastStatus ?? string.Empty);
                            ShowProgress = last.TotalSteps > 0;
                            // keep simple: show percent and status
                            _cancelRebuildCmd?.RaiseCanExecuteChanged();
                            IsLoading = true;
                            _triggerRebuildCmd?.RaiseCanExecuteChanged();
                            _refreshCmd?.RaiseCanExecuteChanged();
                        }));
                    }
                    else
                    {
                        Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            ShowProgress = true;
                            StatusMessage = lastStatus ?? string.Empty;
                            _cancelRebuildCmd?.RaiseCanExecuteChanged();
                            IsLoading = true;
                            _triggerRebuildCmd?.RaiseCanExecuteChanged();
                            _refreshCmd?.RaiseCanExecuteChanged();
                        }));
                    }
                }
                else if (!string.IsNullOrWhiteSpace(lastStatus))
                {
                    if (_feedService.IsCacheValid())
                    {
                        ReloadFromCacheToUi(fromRebuild: false);
                    }
                    else
                    {
                        Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            StatusMessage = lastStatus;
                            ShowProgress = false;
                        }));
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Error initializing FeedControlLogic.");
            }
        }

        public bool ShowMyUnlockTime => _settings?.IncludeMyUnlockTime ?? false;

        private async void FeedService_CacheChanged(object sender, EventArgs e)
        {
            try
            {
                await RefreshAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, ResourceProvider.GetString("LOCFriendsAchFeed_Error_RefreshFeedAfterCacheChange"));
            }
        }

        public ICommand TriggerRebuildCommand { get; }
        public ICommand RefreshCommand { get; }
        public ICommand CancelRebuildCommand { get; }

        protected virtual bool FilterEntry(FeedEntry e)
        {
            if (e == null)
            {
                return false;
            }

            var hasFriend = !string.IsNullOrWhiteSpace(FriendSearchText);
            var hasGame = !string.IsNullOrWhiteSpace(GameSearchText);
            var hasAch = !string.IsNullOrWhiteSpace(AchievementSearchText);

            var hasAnySearch = hasFriend || hasGame || hasAch;

            // When no text filters are active, enforce the "top N" set if present
            if (!hasAnySearch && _defaultVisibleIds != null)
            {
                if (string.IsNullOrEmpty(e.Id) || !_defaultVisibleIds.Contains(e.Id))
                {
                    return false;
                }
            }

            if (hasFriend)
            {
                if (string.IsNullOrWhiteSpace(e.FriendPersonaName) ||
                    e.FriendPersonaName.IndexOf(FriendSearchText, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    return false;
                }
            }

            if (hasGame)
            {
                if (string.IsNullOrWhiteSpace(e.GameName) ||
                    e.GameName.IndexOf(GameSearchText, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    return false;
                }
            }

            if (hasAch)
            {
                var nameMatch = !string.IsNullOrWhiteSpace(e.AchievementDisplayName) &&
                                e.AchievementDisplayName.IndexOf(AchievementSearchText, StringComparison.OrdinalIgnoreCase) >= 0;

                var descMatch = !string.IsNullOrWhiteSpace(e.AchievementDescription) &&
                                e.AchievementDescription.IndexOf(AchievementSearchText, StringComparison.OrdinalIgnoreCase) >= 0;

                if (!(nameMatch || descMatch))
                {
                    return false;
                }
            }

            return true;
        }

        protected virtual string GetGameNameForFilter() => GameNameProvider?.Invoke();

        public async Task RefreshAsync(CancellationToken token = default)
        {
            try
            {
                IsLoading = true;

                if (_feedService.IsCacheValid())
                {
                    StatusMessage = ResourceProvider.GetString("LOCFriendsAchFeed_Status_LoadingFromCache");
                    ReloadFromCacheToUi(fromRebuild: false);
                }
                else
                {
                    StatusMessage = ResourceProvider.GetString("LOCFriendsAchFeed_Status_NoCache_Building");
                    try
                    {
                        await TriggerRebuild(CacheRebuildMode.Full, token).ConfigureAwait(false);
                    }
                    catch (Exception e)
                    {
                        _logger.Error(e, "Failed to run modal rebuild (full).");
                        StatusMessage = ResourceProvider.GetString("LOCFriendsAchFeed_Error_FailedRebuild");
                    }
                }
            }
            catch (Exception e)
            {
                _logger.Error(e, "Failed to refresh feed.");
                        StatusMessage = ResourceProvider.GetString("LOCFriendsAchFeed_Error_FailedLoadFeed");
            }
            finally
            {
                IsLoading = false;
            }
        }

        public async Task TriggerRebuild(CacheRebuildMode mode, CancellationToken externalToken = default)
        {
            CancellationTokenRegistration? extReg = null;
            try
            {
                IsLoading = true;

                if (externalToken != default)
                {
                    extReg = externalToken.Register(() => _feedService.CancelActiveRebuild());
                }

                _cancelRebuildCmd?.RaiseCanExecuteChanged();

                ShowProgress = true;

                await _feedService.StartManagedRebuildAsync(mode).ConfigureAwait(false);

                // Show a non-modal toast for rebuild completion/failure (if enabled)
                if (_settings != null && _settings.EnableNotifications && _settings.NotifyOnRebuild)
                {
                    try
                    {
                        var lastStatus = _feedService.GetLastRebuildStatus() ?? string.Empty;
                        var isFailure = lastStatus.IndexOf("fail", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                        lastStatus.IndexOf("failed", StringComparison.OrdinalIgnoreCase) >= 0;
                        var type = isFailure ? NotificationType.Error : NotificationType.Info;

                        var title = ResourceProvider.GetString("LOCFriendsAchFeed_Title_PluginName");
                        var resultMsg = string.IsNullOrWhiteSpace(lastStatus)
                            ? (isFailure ? ResourceProvider.GetString("LOCFriendsAchFeed_Rebuild_Failed") : ResourceProvider.GetString("LOCFriendsAchFeed_Rebuild_Completed"))
                            : lastStatus;

                        try
                        {
                            _api.Notifications.Add(new NotificationMessage(
                                $"FriendsAchievementFeed-Rebuild-{Guid.NewGuid()}",
                                $"{title}\n{resultMsg}",
                                type));
                        }
                        catch (Exception ex)
                        {
                            _logger?.Debug($"Failed to show rebuild notification: {ex.Message}");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.Debug($"Error while preparing rebuild notification: {ex.Message}");
                    }
                }

                // Reload UI after rebuild
                ReloadFromCacheToUi(fromRebuild: true);
            }
            catch (OperationCanceledException)
            {
                StatusMessage = ResourceProvider.GetString("LOCFriendsAchFeed_Rebuild_Canceled");
                _logger?.Debug(ResourceProvider.GetString("LOCFriendsAchFeed_Debug_RebuildCanceledByUser"));
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Failed to run managed rebuild.");
                StatusMessage = ResourceProvider.GetString("LOCFriendsAchFeed_Error_FailedRebuild");
            }
            finally
            {
                try { extReg?.Dispose(); } catch { }
                IsLoading = false;
                ShowProgress = false;
                _cancelRebuildCmd?.RaiseCanExecuteChanged();
                _triggerRebuildCmd?.RaiseCanExecuteChanged();
                _refreshCmd?.RaiseCanExecuteChanged();
            }
        }

        /// <summary>
        /// Reload from cache (optionally filtered by current game) and apply to UI collections.
        /// </summary>
        private void ReloadFromCacheToUi(bool fromRebuild)
        {
            var gameName = GetGameNameForFilter();
            var entries = LoadEntriesFromCache(gameName);

            Application.Current.Dispatcher.Invoke(() =>
            {
                PopulateAllEntries(entries);
                // Clear any temporary reveals when reloading UI
                ResetAllReveals();
                RebuildFilterLists();
                RefreshViewAndGroups();

                var updated = _feedService.GetCacheLastUpdated();
                CacheLastUpdatedText = updated?.ToLocalTime().ToString("g") ?? ResourceProvider.GetString("LOCFriendsAchFeed_Status_Never");

                if (fromRebuild)
                {
                    var count = EntriesView?.Cast<object>().Count() ?? 0;
                    StatusMessage = string.Format(ResourceProvider.GetString("LOCFriendsAchFeed_Status_CacheRebuilt"), count);
                }
            });
        }

        /// <summary>
        /// Clear all ephemeral reveal flags for the entries currently in this view.
        /// Called when reloading from cache and when the view is disposed.
        /// </summary>
        private void ResetAllReveals()
        {
            try
            {
                foreach (var e in AllEntries)
                {
                    try
                    {
                        e.IsRevealed = false;
                    }
                    catch
                    {
                        // ignore per-entry errors
                    }
                }

                // Also clear the in-memory tracking set.
                lock (_revealedLock)
                {
                    _revealedEntryIds.Clear();
                }
            }
            catch
            {
                // ignore
            }
        }

        /// <summary>
        /// Mark an entry id as revealed in-memory.
        /// </summary>
        public void RegisterReveal(string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                return;
            }
            lock (_revealedLock)
            {
                _revealedEntryIds.Add(id);
            }
        }

        /// <summary>
        /// Unmark an entry id as revealed.
        /// </summary>
        public void UnregisterReveal(string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                return;
            }
            lock (_revealedLock)
            {
                _revealedEntryIds.Remove(id);
            }
        }

        /// <summary>
        /// Toggle reveal state for a given entry and keep the revealed set in sync.
        /// Centralizes fallback behavior so click handlers don't duplicate logic.
        /// </summary>
        public void ToggleReveal(FeedEntry entry)
        {
            if (entry == null)
            {
                return;
            }

            try
            {
                // Make sure we always have something to show as the "unlocked" icon.
                if (string.IsNullOrWhiteSpace(entry.AchievementIconUnlockedUrl))
                {
                    entry.AchievementIconUnlockedUrl = entry.AchievementIconUrl;
                }

                // Flip reveal flag
                entry.IsRevealed = !entry.IsRevealed;

                if (entry.IsRevealed)
                {
                    RegisterReveal(entry.Id);
                }
                else
                {
                    UnregisterReveal(entry.Id);
                }
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Error toggling achievement reveal.");
            }
        }


        /// <summary>
        /// Debounced view refresh used while typing.
        /// </summary>
        private void QueueRefreshView()
        {
            _filterCts?.Cancel();
            _filterCts = new CancellationTokenSource();
            var token = _filterCts.Token;

            Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(50, token);
                    token.ThrowIfCancellationRequested();

                    Application.Current.Dispatcher.Invoke(RefreshViewAndGroups);
                }
                catch (OperationCanceledException)
                {
                    // ignored; newer keystroke arrived
                }
                catch (Exception ex)
                {
                    _logger?.Error(ex, "Error refreshing filtered view.");
                }
            });
        }

        private void RefreshViewAndGroups()
        {
            EntriesView.Refresh();
            RebuildGroupsFromView();
            UpdateStatusCount();
        }

        private void RebuildGroupsFromView()
        {
            GroupedEntries.Clear();

            FeedGroup current = null;
            bool showGameInHeader = GameNameProvider == null; // global vs per-game

            foreach (var obj in EntriesView)
            {
                if (obj is not FeedEntry e)
                {
                    continue;
                }

                var day = e.UnlockTime.Date;

                if (current == null ||
                    current.FriendSteamId != e.FriendSteamId ||
                    current.Date != day ||
                    !string.Equals(current.GameName, e.GameName, StringComparison.OrdinalIgnoreCase))
                {
                    current = new FeedGroup
                    {
                        FriendSteamId = e.FriendSteamId,
                        FriendPersonaName = e.FriendPersonaName,
                        FriendAvatarUrl = e.FriendAvatarUrl,
                        Date = day,
                        GameName = e.GameName,
                        ShowGameName = showGameInHeader && !string.IsNullOrWhiteSpace(e.GameName),
                        SubheaderText = day.ToString("D")
                    };

                    GroupedEntries.Add(current);
                }

                current.Achievements.Add(e);
            }
        }

        private void UpdateStatusCount()
        {
            try
            {
                var count = EntriesView?.Cast<object>().Count() ?? 0;
                if (_feedService != null && _feedService.IsRebuilding)
                {
                    var last = _feedService.GetLastRebuildStatus();
                    if (!string.IsNullOrWhiteSpace(last))
                    {
                        StatusMessage = last;
                        return;
                    }
                    StatusMessage = ResourceProvider.GetString("LOCFriendsAchFeed_Status_UpdatingCache");
                    return;
                }

                StatusMessage = string.Format(ResourceProvider.GetString("LOCFriendsAchFeed_Status_Entries"), count);
            }
            catch
            {
                // ignore
            }
        }

        /// <summary>
        /// Replace AllEntries and compute the defaultVisibleIds used when no filters are active.
        /// Assumes entries are already ordered descending by UnlockTime.
        /// </summary>
        private void PopulateAllEntries(List<FeedEntry> sortedEntries)
        {
            AllEntries.Clear();
            _defaultVisibleIds = null;

            if (sortedEntries == null || sortedEntries.Count == 0)
            {
                return;
            }

            foreach (var e in sortedEntries)
            {
                AllEntries.Add(e);
            }

            var maxItems = (_settings?.MaxFeedItems > 0) ? _settings.MaxFeedItems : 25;

            if (sortedEntries.Count > maxItems)
            {
                _defaultVisibleIds = new HashSet<string>(
                    sortedEntries
                        .Take(maxItems)
                        .Select(entry => entry.Id)
                        .Where(id => !string.IsNullOrEmpty(id)),
                    StringComparer.Ordinal);
            }
            else
            {
                _defaultVisibleIds = null;
            }
        }

        private void RebuildFilterLists()
        {
            var entries = AllEntries.ToList();

            FriendFilters.Clear();
            foreach (var f in BuildFriendFilterList(entries))
            {
                FriendFilters.Add(f);
            }

            GameFilters.Clear();
            foreach (var g in BuildGameFilterList(entries))
            {
                GameFilters.Add(g);
            }
        }

        private List<FeedEntry> LoadEntriesFromCache(string gameName = null)
        {
            var all = _feedService.GetAllCachedEntries() ?? new List<FeedEntry>();

            if (string.IsNullOrWhiteSpace(gameName))
            {
                return all.OrderByDescending(e => e.UnlockTime).ToList();
            }

            return all
                .Where(e => string.Equals(e.GameName, gameName, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(e => e.UnlockTime)
                .ToList();
        }

        private static List<string> BuildFriendFilterList(IEnumerable<FeedEntry> entries)
        {
            return entries?
                    .Select(e => e.FriendPersonaName)
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(n => n)
                    .ToList()
                ?? new List<string>();
        }

        private static List<string> BuildGameFilterList(IEnumerable<FeedEntry> entries)
        {
            return entries?
                    .Select(e => e.GameName)
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(n => n)
                    .ToList()
                ?? new List<string>();
        }

        public void Dispose()
        {
            try
            {
                // Rehide any temporarily revealed entries when the view is torn down.
                var app = Application.Current;
                if (app != null)
                {
                    if (app.Dispatcher.CheckAccess())
                    {
                        ResetAllReveals();
                    }
                    else
                    {
                        app.Dispatcher.Invoke(ResetAllReveals);
                    }
                }
                else
                {
                    ResetAllReveals();
                }
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Error resetting reveals on dispose.");
            }

            try
            {
                _feedService.CacheChanged -= FeedService_CacheChanged;
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Error unsubscribing from feed service.");
            }

            try
            {
                _filterCts?.Cancel();
                _filterCts?.Dispose();
                _filterCts = null;
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Error disposing filter cancellation token.");
            }
        }

        public void CancelRebuild()
        {
            try
            {
                // Cancel the service-managed rebuild so it continues independent of view lifecycle
                if (_feedService != null && _feedService.IsRebuilding)
                {
                    _feedService.CancelActiveRebuild();
                    StatusMessage = ResourceProvider.GetString("LOCFriendsAchFeed_Rebuild_Canceled");
                }
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Error cancelling rebuild.");
            }
            finally
            {
                 ShowProgress = false;
                 ProgressPercent = 0;
                 _cancelRebuildCmd?.RaiseCanExecuteChanged();
            }
        }

        private void Service_RebuildProgress(object sender, ProgressReport report)
        {
            try
            {
                Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        ProgressPercent = report?.PercentComplete ?? 0;
                        StatusMessage = report?.Message ?? _feedService.GetLastRebuildStatus() ?? string.Empty;

                        ShowProgress = _feedService.IsRebuilding;
                        IsLoading = _feedService.IsRebuilding;
                        _cancelRebuildCmd?.RaiseCanExecuteChanged();
                        _triggerRebuildCmd?.RaiseCanExecuteChanged();
                        _refreshCmd?.RaiseCanExecuteChanged();
                    }
                    catch (Exception ex)
                    {
                        _logger?.Debug($"Service_RebuildProgress UI update error: {ex.Message}");
                    }
                }));
            }
            catch (Exception ex)
            {
                _logger?.Debug($"Service_RebuildProgress dispatch failed: {ex.Message}");
            }
        }
    }

    // Local AsyncCommand
    public class AsyncCommand : ICommand
    {
        private readonly Func<object, Task> _executeAsync;
        private readonly Predicate<object> _canExecute;
        private bool _isExecuting;

        public AsyncCommand(Func<object, Task> executeAsync, Predicate<object> canExecute = null)
        {
            _executeAsync = executeAsync ?? throw new ArgumentNullException(nameof(executeAsync));
            _canExecute = canExecute;
        }

        public bool CanExecute(object parameter) =>
            !_isExecuting && (_canExecute?.Invoke(parameter) ?? true);

        public async void Execute(object parameter)
        {
            if (!CanExecute(parameter))
            {
                return;
            }

            try
            {
                _isExecuting = true;
                RaiseCanExecuteChanged();
                await _executeAsync(parameter).ConfigureAwait(false);
            }
            finally
            {
                _isExecuting = false;
                RaiseCanExecuteChanged();
            }
        }

        public event EventHandler CanExecuteChanged;

        public void RaiseCanExecuteChanged()
        {
            try
            {
                var handler = CanExecuteChanged;
                if (handler == null)
                {
                    return;
                }

                // If we have a WPF dispatcher, marshal to UI thread to avoid cross-thread access.
                var app = System.Windows.Application.Current;
                if (app != null && !app.Dispatcher.CheckAccess())
                {
                    app.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        try { handler.Invoke(this, EventArgs.Empty); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"CanExecute handler threw: {ex.Message}"); }
                    }));
                }
                else
                {
                    handler.Invoke(this, EventArgs.Empty);
                }
            }
            catch
            {
                // swallow — raising CanExecute shouldn't crash background tasks
            }
        }
    }
}
