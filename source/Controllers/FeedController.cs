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
using System.Windows.Threading;
using Common;
using FriendsAchievementFeed.Services;
using FriendsAchievementFeed.Models;
using FriendsAchievementFeed.Views.Helpers;
using FriendsAchievementFeed.Views.Shared;
using Playnite.SDK;
using StringResources = FriendsAchievementFeed.Services.StringResources;

namespace FriendsAchievementFeed.Controllers
{
    /// <summary>
    /// Controller that manages the feed display logic and coordinates between the UI and services.
    /// Extracted from the original large FeedControlLogic class for better organization.
    /// </summary>
    public class FeedController : INotifyPropertyChanged, IDisposable
    {
        private readonly IPlayniteAPI _api;
        private readonly FriendsAchievementFeedSettings _settings;
        private readonly ILogger _logger;
        private readonly FeedManager _feedService;

        private readonly AsyncCommand _triggerRebuildCmd;
        private readonly AsyncCommand _triggerIncrementalScanCmd;
        private readonly AsyncCommand _refreshCmd;
        private readonly AsyncCommand _cancelRebuildCmd;
        private readonly SemaphoreSlim _refreshLock = new SemaphoreSlim(1, 1);
        private volatile bool _refreshRequested;

        // Helpers for auth and progress
        private readonly SteamAuthValidator _authValidator;
        private readonly RebuildProgressHandler _progressHandler;
        
        // Hydrator for converting cached entries to UI entries
        private readonly FeedEntryHydrator _hydrator;
        private readonly FeedEntryFactory _entryFactory = new FeedEntryFactory();

        // per-game command lives here (organization)
        private AsyncCommand _singleGameScanCmd;
        private AsyncCommand _quickScanCmd;

        // One-shot dialog throttling (avoid popup spam)
        private DateTime _lastAuthDialogUtc = DateTime.MinValue;
        private static readonly TimeSpan AuthDialogCooldown = TimeSpan.FromSeconds(5);

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

        public FeedController(IPlayniteAPI api, FriendsAchievementFeedSettings settings, FeedManager feedService)
        {
            _api = api ?? throw new ArgumentNullException(nameof(api));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _feedService = feedService ?? throw new ArgumentNullException(nameof(feedService));
            _logger = LogManager.GetLogger(nameof(FeedController));

            _authValidator = new SteamAuthValidator(settings, feedService);
            _progressHandler = new RebuildProgressHandler(feedService, _logger);
            _hydrator = new FeedEntryHydrator(feedService.Cache, _entryFactory);

            // Initialize commands
            _triggerRebuildCmd = new AsyncCommand(
                async _ => await TriggerFullRebuildAsync(default).ConfigureAwait(false),
                _ => CanTriggerFullRebuild());

            _triggerIncrementalScanCmd = new AsyncCommand(
                async _ => await TriggerIncrementalScanAsync(default).ConfigureAwait(false),
                _ => CanTriggerIncrementalScan());

            _refreshCmd = new AsyncCommand(
                async _ => await RefreshFeedAsync(default).ConfigureAwait(false),
                _ => CanRefresh());

            _cancelRebuildCmd = new AsyncCommand(
                async _ => await CancelRebuildAsync(default).ConfigureAwait(false),
                _ => CanCancelRebuild());

            InitializePerGame();
            InitializeQuickScan();

            // Subscribe to feed service events
            _feedService.CacheChanged += OnCacheChanged;
            _feedService.RebuildProgress += OnRebuildProgress;
        }

        #region Properties

        /// <summary>
        /// Provider for getting the current game name for filtering
        /// </summary>
        public Func<string> GameNameProvider { get; set; }
        public Func<Guid?> GameIdProvider { get; set; }
        
        /// <summary>
        /// Provider for the view model to update with loaded data
        /// </summary>
        public Func<ViewModels.FeedViewModel> ViewModelProvider { get; set; }

        #endregion

        #region Commands

        public ICommand TriggerRebuildCommand => _triggerRebuildCmd;
        public ICommand TriggerIncrementalScanCommand => _triggerIncrementalScanCmd;
        public ICommand RefreshCommand => _refreshCmd;
        public ICommand CancelRebuildCommand => _cancelRebuildCmd;

        // MUST be assignable because initialized in InitializePerGame()
        public ICommand RefreshCurrentGameCommand { get; private set; }
        public ICommand QuickScanCommand { get; private set; }

        #endregion

        #region Command Implementation

        private async Task TriggerFullRebuildAsync(CancellationToken cancellationToken)
        {
            try
            {
                _logger?.Info("Triggering full cache rebuild from controller");
                await _feedService.StartManagedRebuildAsync(null).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Failed to trigger full rebuild");
                throw;
            }
        }

        private async Task TriggerIncrementalScanAsync(CancellationToken cancellationToken)
        {
            try
            {
                _logger?.Info("Triggering incremental scan from controller");
                
                var viewModel = ViewModelProvider?.Invoke();
                if (viewModel != null)
                {
                    // Use the settings from the view model for incremental scan
                    await _feedService.StartManagedIncrementalScanAsync(
                        viewModel.IncrementalRecentFriendsCount,
                        viewModel.IncrementalRecentGamesPerFriend).ConfigureAwait(false);
                }
                else
                {
                    // Fallback to defaults if no view model
                    await _feedService.StartManagedIncrementalScanAsync().ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Failed to trigger incremental scan");
                throw;
            }
        }

        public async Task RefreshFeedAsync(CancellationToken cancellationToken = default)
        {
            _refreshRequested = true;

            await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                while (_refreshRequested)
                {
                    _refreshRequested = false;
                    cancellationToken.ThrowIfCancellationRequested();

                    _logger?.Info("Refreshing feed from controller");
                    
                    var viewModel = ViewModelProvider?.Invoke();
                    if (viewModel == null)
                    {
                        _logger?.Warn("No view model available to refresh");
                        return;
                    }
                    
                    // Load from cache and hydrate for UI
                    var entries = await Task.Run(() =>
                    {
                        var gameName = GetGameNameForFilter();
                        var rawEntries = LoadRawFriendEntriesFromCache(gameName);
                        var hydrated = _hydrator.HydrateForUi(rawEntries, cancellationToken);
                        
                        // Sort by unlock time
                        return hydrated
                            .Where(e => e != null)
                            .OrderByDescending(e => e.FriendUnlockTime)
                            .ThenByDescending(e => e.Id, StringComparer.Ordinal)
                            .ToList();
                    }, cancellationToken).ConfigureAwait(true);
                    
                    // Compute default visible IDs based on MaxFeedItems setting
                    var maxItems = (_settings?.MaxFeedItems > 0) ? _settings.MaxFeedItems : 25;
                    HashSet<string> defaultVisibleIds = null;
                    if (entries.Count > maxItems)
                    {
                        defaultVisibleIds = new HashSet<string>(
                            entries.Take(maxItems)
                                .Select(x => x?.Id)
                                .Where(id => !string.IsNullOrEmpty(id)),
                            StringComparer.Ordinal);
                    }
                    
                    // Update the view model on UI thread
                    await _api.MainView.UIDispatcher.InvokeAsync(() =>
                    {
                        // Apply the HideAchievementsLockedForSelf setting to entries
                        var hideLocked = _settings?.HideAchievementsLockedForSelf ?? false;
                        foreach (var entry in entries)
                        {
                            if (entry != null)
                            {
                                entry.HideAchievementsLockedForSelf = hideLocked;
                            }
                        }
                        
                        viewModel.UpdateCacheData(entries, defaultVisibleIds);
                        
                        // Update cache last updated time
                        var updated = _feedService.Cache.GetFriendFeedLastUpdatedUtc();
                        viewModel.CacheLastUpdatedText = updated?.ToLocalTime().ToString("g") 
                            ?? StringResources.GetString("LOCFriendsAchFeed_Status_Never");
                    }, DispatcherPriority.Background);
                    
                    _logger?.Info($"Loaded {entries.Count} entries from cache");
                }
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Failed to refresh feed");
                throw;
            }
            finally
            {
                _refreshLock.Release();
            }
        }
        
        private List<FeedEntry> LoadRawFriendEntriesFromCache(string gameName = null)
        {
            var all = _feedService.Cache.GetCachedFriendEntries() ?? new List<FeedEntry>();

            if (string.IsNullOrWhiteSpace(gameName))
            {
                return all.OrderByDescending(e => e.FriendUnlockTimeUtc).ToList();
            }

            return all
                .Where(e => string.Equals(e.GameName, gameName, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(e => e.FriendUnlockTimeUtc)
                .ToList();
        }

        private async Task CancelRebuildAsync(CancellationToken cancellationToken)
        {
            try
            {
                _logger?.Info("Canceling rebuild from controller");
                _feedService.CancelActiveRebuild();
                await Task.CompletedTask;
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Failed to cancel rebuild");
                throw;
            }
        }

        #endregion

        #region Command State

        private bool CanTriggerFullRebuild()
        {
            // Implementation would check if a full rebuild can be triggered
            return !(_feedService?.IsRebuilding ?? false);
        }

        private bool CanTriggerIncrementalScan()
        {
            // Implementation would check if an incremental scan can be triggered
            return !(_feedService?.IsRebuilding ?? false);
        }

        private bool CanRefresh()
        {
            // Implementation would check if refresh can be performed
            return true;
        }

        private bool CanCancelRebuild()
        {
            // Implementation would check if there's something to cancel
            return _feedService?.IsRebuilding ?? false;
        }

        #endregion

        #region Event Handlers

        private async void OnCacheChanged(object sender, EventArgs e)
        {
            // Reload feed data when cache changes (after any rebuild)
            try
            {
                await RefreshFeedAsync(default).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Failed to refresh feed after cache change");
            }
            
            NotifyCommandsChanged();
        }

        private void OnRebuildProgress(object sender, ProgressReport report)
        {
            try
            {
                var viewModel = ViewModelProvider?.Invoke();
                if (viewModel == null) return;

                var pct = report?.PercentComplete ?? 0;
                if ((pct <= 0 || double.IsNaN(pct)) && report != null && report.TotalSteps > 0)
                {
                    pct = Math.Max(0, Math.Min(100, (report.CurrentStep * 100.0) / report.TotalSteps));
                }

                _api.MainView.UIDispatcher.InvokeIfNeeded(() =>
                {
                    viewModel.ProgressPercent = pct;
                    
                    // Update status message during rebuild, or if there's a meaningful completion message
                    var hasMessage = !string.IsNullOrWhiteSpace(report?.Message);
                    if (_feedService.IsRebuilding || hasMessage)
                    {
                        viewModel.StatusMessage = report?.Message ?? _feedService.GetLastRebuildStatus() ?? string.Empty;
                    }
                    
                    viewModel.ShowProgress = _feedService.IsRebuilding;
                    viewModel.IsLoading = _feedService.IsRebuilding;

                    NotifyCommandsChanged();
                }, DispatcherPriority.Background);
            }
            catch (Exception ex)
            {
                _logger?.Debug($"OnRebuildProgress error: {ex.Message}");
            }
        }

        public void NotifyCommandsChanged()
        {
            _triggerRebuildCmd?.RaiseCanExecuteChanged();
            _triggerIncrementalScanCmd?.RaiseCanExecuteChanged();
            _refreshCmd?.RaiseCanExecuteChanged();
            _cancelRebuildCmd?.RaiseCanExecuteChanged();
            _singleGameScanCmd?.RaiseCanExecuteChanged();
            _quickScanCmd?.RaiseCanExecuteChanged();
        }

        #endregion

        #region Per-Game Command Initialization

        private void InitializePerGame()
        {
            _singleGameScanCmd = new AsyncCommand(
                async _ => await TriggerSingleGameScanAsync(default).ConfigureAwait(false),
                _ => CanTriggerSingleGameScan());

            RefreshCurrentGameCommand = _singleGameScanCmd;
        }

        private void InitializeQuickScan()
        {
            _quickScanCmd = new AsyncCommand(
                async _ => await TriggerIncrementalScanAsync(default).ConfigureAwait(false),
                _ => CanTriggerQuickScan());

            QuickScanCommand = _quickScanCmd;
        }

        private async Task TriggerSingleGameScanAsync(CancellationToken cancellationToken)
        {
            try
            {
                var gameId = GameIdProvider?.Invoke();
                if (!gameId.HasValue)
                {
                    _logger?.Warn("No current game ID available for single game scan");
                    return;
                }

                _logger?.Info($"Triggering single game scan for game {gameId}");
                // For single game scan, start a managed rebuild with null options
                await _feedService.StartManagedRebuildAsync(null).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Failed to trigger single game scan");
                throw;
            }
        }

        private bool CanTriggerSingleGameScan()
        {
            return !(_feedService?.IsRebuilding ?? false) && GameIdProvider?.Invoke() != null;
        }

        private bool CanTriggerQuickScan()
        {
            return !(_feedService?.IsRebuilding ?? false);
        }

        #endregion

        protected virtual string GetGameNameForFilter() => GameNameProvider?.Invoke();

        public void Dispose()
        {
            try
            {
                _feedService.CacheChanged -= OnCacheChanged;
                _feedService.RebuildProgress -= OnRebuildProgress;
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Error unsubscribing from feed service.");
            }

            try
            {
                _authValidator?.Dispose();
                _progressHandler?.Dispose();
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Error disposing helpers.");
            }
        }
    }
}