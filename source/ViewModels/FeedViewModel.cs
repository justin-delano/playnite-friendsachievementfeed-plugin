using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Data;
using FriendsAchievementFeed.Models;
using FriendsAchievementFeed.Services;
using FriendsAchievementFeed.Views.Shared;
using Common;
using System.Collections.Generic;
using System.Threading;
using Playnite.SDK;

namespace FriendsAchievementFeed.ViewModels
{
    /// <summary>
    /// View model for the feed display, handling UI-specific concerns like filtering, grouping, and data binding.
    /// Extracted from the original FeedControlLogic class for better separation of concerns.
    /// </summary>
    public class FeedViewModel : INotifyPropertyChanged, IDisposable
    {
        private readonly ILogger _logger = LogManager.GetLogger(nameof(FeedViewModel));
        private readonly FeedEntryFilter _filter = new FeedEntryFilter();

        // Snapshot from cache (friend-only)
        private List<FeedEntry> _rawCachedEntries = new List<FeedEntry>();

        // Debounce token for filter refresh
        private CancellationTokenSource _filterCts;
        private CancellationTokenSource _cacheReloadCts;
        private CancellationTokenSource _reapplyCts;

        // Track revealed entries
        private readonly HashSet<string> _revealedEntryIds = new HashSet<string>(StringComparer.Ordinal);
        private readonly object _revealedLock = new object();

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        private bool SetField<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        #region Collections

        public ObservableCollection<FeedEntry> AllEntries { get; } = new ObservableCollection<FeedEntry>();
        public object EntriesView { get; private set; }
        public ObservableCollection<FeedGroup> GroupedEntries { get; } = new ObservableCollection<FeedGroup>();
        public bool HasAnyEntries => GroupedEntries?.Any() == true;

        public ObservableCollection<string> FriendFilters { get; } = new ObservableCollection<string>();
        public ObservableCollection<string> GameFilters { get; } = new ObservableCollection<string>();

        #endregion

        #region Status and Display Properties

        private string _cacheLastUpdatedText = "";
        public string CacheLastUpdatedText
        {
            get => _cacheLastUpdatedText;
            set => SetField(ref _cacheLastUpdatedText, value);
        }

        private string _statusMessage = "";
        public string StatusMessage
        {
            get => _statusMessage;
            set => SetField(ref _statusMessage, value);
        }
        
        private bool _isLoading;
        public bool IsLoading
        {
            get => _isLoading;
            set => SetField(ref _isLoading, value);
        }

        private bool _showProgress;
        public bool ShowProgress
        {
            get => _showProgress;
            set => SetField(ref _showProgress, value);
        }

        private double _progressPercent;
        public double ProgressPercent
        {
            get => _progressPercent;
            set => SetField(ref _progressPercent, value);
        }

        private string _progressDetail = "";
        public string ProgressDetail
        {
            get => _progressDetail;
            set => SetField(ref _progressDetail, value);
        }

        #endregion

        #region Search and Filter Properties

        private string _friendSearchText = "";
        public string FriendSearchText
        {
            get => _friendSearchText;
            set
            {
                if (!SetField(ref _friendSearchText, value)) return;
                _filter.FriendSearchText = value ?? "";
                QueueRefreshView();
            }
        }

        private string _gameSearchText = "";
        public string GameSearchText
        {
            get => _gameSearchText;
            set
            {
                if (!SetField(ref _gameSearchText, value)) return;
                _filter.GameSearchText = value ?? "";
                QueueRefreshView();
            }
        }

        private string _achievementSearchText = "";
        public string AchievementSearchText
        {
            get => _achievementSearchText;
            set
            {
                if (!SetField(ref _achievementSearchText, value)) return;
                _filter.AchievementSearchText = value ?? "";
                QueueRefreshView();
            }
        }

        #endregion

        #region Incremental Scan Properties

        private int _incrementalRecentFriendsCount = 5;
        public int IncrementalRecentFriendsCount
        {
            get => _incrementalRecentFriendsCount;
            set
            {
                var v = Math.Max(0, value);
                SetField(ref _incrementalRecentFriendsCount, v);
            }
        }

        private int _incrementalRecentGamesPerFriend = 5;
        public int IncrementalRecentGamesPerFriend
        {
            get => _incrementalRecentGamesPerFriend;
            set
            {
                var v = Math.Max(0, value);
                SetField(ref _incrementalRecentGamesPerFriend, v);
            }
        }

        #endregion

        #region Methods

        private void QueueRefreshView()
        {
            // Cancel any previous filter operation
            _filterCts?.Cancel();
            _filterCts?.Dispose();
            _filterCts = new CancellationTokenSource();

            // Debounce the filter refresh
            var token = _filterCts.Token;
            System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    await System.Threading.Tasks.Task.Delay(250, token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();

                    // Call RefreshView on the UI thread via Dispatcher
                    var dispatcher = System.Windows.Application.Current?.Dispatcher;
                    if (dispatcher != null)
                    {
                        _ = dispatcher.InvokeAsync(RefreshView, System.Windows.Threading.DispatcherPriority.Background);
                    }
                    else
                    {
                        RefreshView();
                    }
                }
                catch (OperationCanceledException)
                {
                    // Debounce was cancelled, that's fine
                }
                catch (Exception ex)
                {
                    _logger?.Error(ex, "Error during debounced filter refresh");
                }
            }, token);
        }

        public void RefreshView()
        {
            try
            {
                // Apply filter to AllEntries and rebuild groups
                var filtered = AllEntries.Where(e => _filter.Matches(e)).ToList();
                
                // Clear and rebuild GroupedEntries
                GroupedEntries.Clear();
                
                // Build groups using the same logic as the original
                var showGameInHeader = true; // For global view
                var groups = FeedGroupingBuilder.BuildGroups(filtered, showGameInHeader, DateTimeUtilities.AsLocalFromUtc);
                
                foreach (var g in groups)
                {
                    GroupedEntries.Add(g);
                }
                
                // Update status message with count
                var totalCount = AllEntries.Count;
                if (totalCount > 0)
                {
                    var countText = $"Showing {filtered.Count} of {totalCount}";
                    
                    // Preserve base status if it's meaningful (not empty, not "Idle", not already a count)
                    var baseStatus = StatusMessage?.Split('|')[0].Trim() ?? string.Empty;
                    var idleText = StringResources.GetString("LOCFriendsAchFeed_Status_Idle") ?? "Idle";
                    
                    if (!string.IsNullOrEmpty(baseStatus) && 
                        !baseStatus.Equals(idleText, StringComparison.OrdinalIgnoreCase) &&
                        !baseStatus.StartsWith("Showing ", StringComparison.OrdinalIgnoreCase))
                    {
                        StatusMessage = $"{baseStatus} | {countText}";
                    }
                    else
                    {
                        StatusMessage = countText;
                    }
                }
                
                OnPropertyChanged(nameof(HasAnyEntries));
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Error refreshing view");
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

        public void UpdateCacheData(List<FeedEntry> entries, HashSet<string> defaultVisibleIds = null)
        {
            _rawCachedEntries = entries ?? new List<FeedEntry>();
            
            // Set the default visible IDs filter
            _filter.DefaultVisibleIds = defaultVisibleIds;
            
            // Update AllEntries and apply filters
            AllEntries.Clear();
            foreach (var entry in _rawCachedEntries)
            {
                if (entry != null)
                {
                    AllEntries.Add(entry);
                }
            }
            
            // Rebuild filter lists when entries change
            RebuildFilterLists();
            
            // Refresh view on UI thread
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
            {
                dispatcher.Invoke(RefreshView);
            }
            else
            {
                RefreshView();
            }
        }

        public void ToggleReveal(FeedEntry entry)
        {
            if (entry == null) return;

            // Only allow toggling if entry itself says it's revealable
            if (!entry.CanReveal)
                return;

            entry.IsRevealed = !entry.IsRevealed;

            if (!string.IsNullOrEmpty(entry.Id))
            {
                if (entry.IsRevealed)
                    RegisterReveal(entry.Id);
                else
                    UnregisterReveal(entry.Id);
            }
        }

        public void ResetAllReveals()
        {
            lock (_revealedLock)
            {
                _revealedEntryIds.Clear();
            }

            foreach (var entry in AllEntries)
            {
                if (entry != null)
                {
                    entry.IsRevealed = false;
                }
            }
        }

        private void RegisterReveal(string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            lock (_revealedLock) { _revealedEntryIds.Add(id); }
        }

        private void UnregisterReveal(string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            lock (_revealedLock) { _revealedEntryIds.Remove(id); }
        }

        #endregion

        public void Dispose()
        {
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

            try
            {
                _cacheReloadCts?.Cancel();
                _cacheReloadCts?.Dispose();
                _cacheReloadCts = null;
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Error disposing cache reload cancellation token.");
            }

            try
            {
                _reapplyCts?.Cancel();
                _reapplyCts?.Dispose();
                _reapplyCts = null;
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Error disposing reapply cancellation token.");
            }
        }
    }
}