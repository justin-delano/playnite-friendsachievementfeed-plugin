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
using FriendsAchievementFeed.Common;
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


        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

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
        public ICommand RefreshCurrentGameWithOwnersCommand { get; private set; }

        // MUST be assignable because initialized in InitializeQuickScan()
        public ICommand QuickScanCommand { get; private set; }

        #endregion

        #region Command Implementation

        private async Task TriggerFullRebuildAsync(CancellationToken cancellationToken)
        {
            _logger?.Info("Triggering full cache rebuild from controller");
            await _feedService.StartManagedRebuildAsync(null).ConfigureAwait(false);
        }

        private async Task TriggerIncrementalScanAsync(CancellationToken cancellationToken)
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

        public async Task RefreshFeedAsync(CancellationToken cancellationToken = default)
        {
            _refreshRequested = true;

            if (_refreshLock.CurrentCount == 0) return;

            await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                while (_refreshRequested)
                {
                    _refreshRequested = false;
                    cancellationToken.ThrowIfCancellationRequested();

                    var viewModel = ViewModelProvider?.Invoke();
                    if (viewModel == null) return;
                    
                    var gameId = GameIdProvider?.Invoke();
                    var game = gameId.HasValue ? _api.Database.Games.Get(gameId.Value) : null;
                    
                    var entries = game != null
                        ? await _feedService.BuildGameFeedAsync(game, cancellationToken).ConfigureAwait(false)
                        : await _feedService.BuildGlobalFeedAsync(cancellationToken).ConfigureAwait(false);

                    var maxItems = (_settings?.MaxFeedItems > 0) ? _settings.MaxFeedItems : 25;
                    var defaultVisibleIds = new HashSet<string>(
                        entries.Take(maxItems)
                            .Select(x => x?.Id)
                            .Where(id => !string.IsNullOrEmpty(id)),
                        StringComparer.Ordinal);
                    
                    await _api.MainView.UIDispatcher.InvokeAsync(() =>
                    {
                        viewModel.UpdateCacheData(entries, defaultVisibleIds);
                        
                        var updated = _feedService.Cache.GetFriendFeedLastUpdatedUtc();
                        viewModel.CacheLastUpdatedText = updated?.ToLocalTime().ToString("g") 
                            ?? StringResources.GetString("LOCFriendsAchFeed_Status_Never");
                    }, DispatcherPriority.DataBind);

                    if (_refreshRequested) await Task.Delay(50, cancellationToken);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Failed to refresh feed");
            }
            finally
            {
                _refreshLock.Release();
            }
        }
        
        private async Task CancelRebuildAsync(CancellationToken cancellationToken)
        {
            _logger?.Info("Canceling rebuild from controller");
            _feedService.CancelActiveRebuild();
            await Task.CompletedTask;
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

            // Ensure command state updates happen on UI thread
            _api.MainView.UIDispatcher.InvokeIfNeeded(NotifyCommandsChanged, DispatcherPriority.Background);
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
            (RefreshCurrentGameWithOwnersCommand as AsyncCommand)?.RaiseCanExecuteChanged();
            _quickScanCmd?.RaiseCanExecuteChanged();
        }

        #endregion

        #region Per-Game Command Initialization

        private void InitializePerGame()
        {
            _singleGameScanCmd = new AsyncCommand(
                async _ => await TriggerSingleGameScanAsync(default, false).ConfigureAwait(false),
                _ => CanTriggerSingleGameScan());

            var singleGameScanWithOwnersCmd = new AsyncCommand(
                async _ => await TriggerSingleGameScanAsync(default, true).ConfigureAwait(false),
                _ => CanTriggerSingleGameScan());

            RefreshCurrentGameCommand = _singleGameScanCmd;
            RefreshCurrentGameWithOwnersCommand = singleGameScanWithOwnersCmd;
        }

        private void InitializeQuickScan()
        {
            _quickScanCmd = new AsyncCommand(
                async _ => await TriggerIncrementalScanAsync(default).ConfigureAwait(false),
                _ => CanTriggerQuickScan());

            QuickScanCommand = _quickScanCmd;
        }

        private async Task TriggerSingleGameScanAsync(CancellationToken cancellationToken, bool includeOwners)
        {
            try
            {
                var gameId = GameIdProvider?.Invoke();
                if (!gameId.HasValue)
                {
                    _logger?.Warn("No current game ID available for single game scan");
                    return;
                }

                _logger?.Info($"Triggering single game scan for game {gameId} (withOwnersOnly: {includeOwners})");
                await _feedService.StartManagedSingleGameScanAsync(gameId.Value, includeOwners, cancellationToken).ConfigureAwait(false);
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