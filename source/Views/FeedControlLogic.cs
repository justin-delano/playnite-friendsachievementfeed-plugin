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

        private readonly FeedEntryFilter _filter = new FeedEntryFilter();

        // Raw entries (friend-only) snapshot from cache. Not bound to UI.
        private List<FeedEntry> _rawEntries = new List<FeedEntry>();

        // All decorated entries for this view in memory (what UI binds to)
        public ObservableCollection<FeedEntry> AllEntries { get; } = new ObservableCollection<FeedEntry>();

        public ICollectionView EntriesView { get; }

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
                if (!SetField(ref _isLoading, value)) return;

                try
                {
                    _triggerRebuildCmd?.RaiseCanExecuteChanged();
                    _refreshCmd?.RaiseCanExecuteChanged();
                    _cancelRebuildCmd?.RaiseCanExecuteChanged();
                }
                catch { }
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

        // Debounce token for filter refresh
        private CancellationTokenSource _filterCts;

        // Debounce token for re-decoration (settings toggles)
        private CancellationTokenSource _decorateCts;

        // Track only entries that have been temporarily revealed
        private readonly HashSet<string> _revealedEntryIds = new HashSet<string>(StringComparer.Ordinal);
        private readonly object _revealedLock = new object();

        // Fast lookup for reveal reset without scanning AllEntries
        private readonly Dictionary<string, FeedEntry> _entryById = new Dictionary<string, FeedEntry>(StringComparer.Ordinal);

        // Batch populate to avoid UI freeze on big lists
        private const int PopulateBatchSize = 250;
        private int _populateGeneration = 0;

        // Throttle progress UI updates
        private readonly object _progressUiLock = new object();
        private DateTime _lastProgressUiUpdateUtc = DateTime.MinValue;
        private static readonly TimeSpan ProgressUiMinInterval = TimeSpan.FromMilliseconds(100);
        private bool _userRequestedCancel = false;

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

        /// <summary>
        /// Optional provider that returns the current Game.Name for per-game views.
        /// Null = global view.
        /// </summary>
        public Func<string> GameNameProvider { get; set; }

        private static DateTime AsLocalFromUtc(DateTime dt)
        {
            if (dt.Kind == DateTimeKind.Local) return dt;
            if (dt.Kind == DateTimeKind.Utc) return dt.ToLocalTime();
            return DateTime.SpecifyKind(dt, DateTimeKind.Utc).ToLocalTime();
        }

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

            // Delta-only rebuild: no mode, no parameters.
            _triggerRebuildCmd = new AsyncCommand(async _ =>
                {
                    await TriggerRebuild(CancellationToken.None).ConfigureAwait(false);
                }, _ => !IsLoading && !_feedService.IsRebuilding);

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
            EntriesView.Filter = o => _filter.Matches(o as FeedEntry);

            GroupedEntries.CollectionChanged += (s, e) =>
            {
                OnPropertyChanged(nameof(HasAnyEntries));
            };

            HookSettingsChanges();

            // Restore UI state if rebuild already running / last status exists
            TryInitializeFromServiceState();
        }

        public bool ShowMyUnlockTime => _settings?.IncludeMyUnlockTime ?? false;

        public ICommand TriggerRebuildCommand { get; }
        public ICommand RefreshCommand { get; }
        public ICommand CancelRebuildCommand { get; }

        private void HookSettingsChanges()
        {
            if (_settings == null) return;

            _settings.PropertyChanged += (s, e) =>
            {
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
                        QueueReDecorateFromRaw();
                        break;

                    case nameof(_settings.HideAchievementsLockedForYou):
                        QueueReDecorateFromRaw();
                        break;

                    case nameof(_settings.MaxFeedItems):
                        QueueReDecorateFromRaw();
                        break;
                }
            };
        }

        private void TryInitializeFromServiceState()
        {
            try
            {
                var last = _feedService.GetLastRebuildProgress();
                var lastStatus = _feedService.GetLastRebuildStatus();

                if (_feedService.IsRebuilding)
                {
                    RunOnUi(() =>
                    {
                        ProgressPercent = last?.PercentComplete ?? 0;
                        StatusMessage = !string.IsNullOrWhiteSpace(last?.Message) ? last.Message : (lastStatus ?? string.Empty);
                        ShowProgress = true;
                        IsLoading = true;
                        _cancelRebuildCmd?.RaiseCanExecuteChanged();
                        _triggerRebuildCmd?.RaiseCanExecuteChanged();
                        _refreshCmd?.RaiseCanExecuteChanged();
                    });
                }
                else if (!string.IsNullOrWhiteSpace(lastStatus))
                {
                    if (_feedService.IsCacheValid())
                    {
                        _ = ReloadFromCacheToUiAsync(fromRebuild: false, CancellationToken.None);
                    }
                    else
                    {
                        RunOnUi(() =>
                        {
                            StatusMessage = lastStatus;
                            ShowProgress = false;
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Error initializing FeedControlLogic.");
            }
        }

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

        private void RunOnUi(Action action, DispatcherPriority priority = DispatcherPriority.Normal)
        {
            try
            {
                var disp = Application.Current?.Dispatcher;
                if (disp != null && !disp.CheckAccess())
                {
                    disp.BeginInvoke(action, priority);
                }
                else
                {
                    action();
                }
            }
            catch { }
        }

        protected virtual string GetGameNameForFilter() => GameNameProvider?.Invoke();

        public async Task RefreshAsync(CancellationToken token = default)
        {
            RunOnUi(() => IsLoading = true);

            await Task.Yield();

            try
            {
                if (_feedService.IsCacheValid())
                {
                    RunOnUi(() => StatusMessage = ResourceProvider.GetString("LOCFriendsAchFeed_Status_LoadingFromCache"));
                    await ReloadFromCacheToUiAsync(fromRebuild: false, token).ConfigureAwait(false);
                }
                else
                {
                    RunOnUi(() => StatusMessage = ResourceProvider.GetString("LOCFriendsAchFeed_Status_NoCache_Building"));
                    try
                    {
                        await TriggerRebuild(token).ConfigureAwait(false);
                    }
                    catch (Exception e)
                    {
                        _logger.Error(e, "Failed to run rebuild.");
                        RunOnUi(() => StatusMessage = ResourceProvider.GetString("LOCFriendsAchFeed_Error_FailedRebuild"));
                    }
                }
            }
            catch (Exception e)
            {
                _logger.Error(e, "Failed to refresh feed.");
                RunOnUi(() => StatusMessage = ResourceProvider.GetString("LOCFriendsAchFeed_Error_FailedLoadFeed"));
            }
            finally
            {
                RunOnUi(() => IsLoading = false);
            }
        }

        /// <summary>
        /// Delta-only rebuild trigger. No modes. Works for first build automatically.
        /// </summary>
        public async Task TriggerRebuild(CancellationToken externalToken = default)
        {
            CancellationTokenRegistration extReg = default;
            var hasReg = false;

            if (_feedService.IsRebuilding)
            {
                var last = _feedService.GetLastRebuildProgress();
                var lastStatus = _feedService.GetLastRebuildStatus();
                RunOnUi(() =>
                {
                    ProgressPercent = last?.PercentComplete ?? 0;
                    StatusMessage = !string.IsNullOrWhiteSpace(last?.Message) ? last.Message : (lastStatus ?? string.Empty);
                    ShowProgress = true;
                    IsLoading = true;
                    _cancelRebuildCmd?.RaiseCanExecuteChanged();
                    _triggerRebuildCmd?.RaiseCanExecuteChanged();
                    _refreshCmd?.RaiseCanExecuteChanged();
                });

                return;
            }

            RunOnUi(() =>
            {
                IsLoading = true;
                ShowProgress = true;
                ProgressPercent = 0;
                StatusMessage = string.Empty;
                _cancelRebuildCmd?.RaiseCanExecuteChanged();
                _triggerRebuildCmd?.RaiseCanExecuteChanged();
                _refreshCmd?.RaiseCanExecuteChanged();
            });

            await Task.Yield();

            try
            {
                if (externalToken != default)
                {
                    extReg = externalToken.Register(() => _feedService.CancelActiveRebuild());
                    hasReg = true;
                }

                // Updated service API: delta-only rebuild
                await _feedService.StartManagedRebuildAsync().ConfigureAwait(false);

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
                            ? (isFailure ? ResourceProvider.GetString("LOCFriendsAchFeed_Rebuild_Failed")
                                         : ResourceProvider.GetString("LOCFriendsAchFeed_Rebuild_Completed"))
                            : lastStatus;

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

                if (!_feedService.IsRebuilding)
                {
                    await ReloadFromCacheToUiAsync(fromRebuild: true, CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                RunOnUi(() => StatusMessage = ResourceProvider.GetString("LOCFriendsAchFeed_Rebuild_Canceled"));
                _logger?.Debug(ResourceProvider.GetString("LOCFriendsAchFeed_Debug_RebuildCanceledByUser"));
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Failed to run managed rebuild.");
                RunOnUi(() => StatusMessage = ResourceProvider.GetString("LOCFriendsAchFeed_Error_FailedRebuild"));
            }
            finally
            {
                try { if (hasReg) extReg.Dispose(); } catch { }

                RunOnUi(() =>
                {
                    var rebuilding = _feedService?.IsRebuilding ?? false;
                    IsLoading = rebuilding;
                    ShowProgress = rebuilding;
                    _cancelRebuildCmd?.RaiseCanExecuteChanged();
                    _triggerRebuildCmd?.RaiseCanExecuteChanged();
                    _refreshCmd?.RaiseCanExecuteChanged();
                });
            }
        }

        private void QueueReDecorateFromRaw()
        {
            _decorateCts?.Cancel();
            _decorateCts = new CancellationTokenSource();
            var token = _decorateCts.Token;

            Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(50, token);
                    token.ThrowIfCancellationRequested();

                    var raw = _rawEntries ?? new List<FeedEntry>();
                    var composed = await ComposeViewEntriesAsync(raw, token).ConfigureAwait(false);

                    await ApplyEntriesToUiAsync(composed, fromRebuild: false, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    _logger?.Error(ex, "Error re-decorating view entries.");
                }
            });
        }

        private async Task ReloadFromCacheToUiAsync(bool fromRebuild, CancellationToken token)
        {
            var gameName = GetGameNameForFilter();

            var raw = await Task.Run(() => LoadRawEntriesFromCache(gameName), token).ConfigureAwait(false);

            _rawEntries = raw ?? new List<FeedEntry>();

            var composed = await ComposeViewEntriesAsync(_rawEntries, token).ConfigureAwait(false);

            await ApplyEntriesToUiAsync(composed, fromRebuild, token).ConfigureAwait(false);
        }

        private async Task<List<FeedEntry>> ComposeViewEntriesAsync(List<FeedEntry> raw, CancellationToken token)
        {
            try
            {
                return await _feedService.DecorateForViewAsync(raw ?? Enumerable.Empty<FeedEntry>(), token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger?.Debug($"ComposeViewEntriesAsync failed; falling back to raw entries: {ex.Message}");
                return raw ?? new List<FeedEntry>();
            }
        }

        private async Task ApplyEntriesToUiAsync(List<FeedEntry> entries, bool fromRebuild, CancellationToken token)
        {
            entries ??= new List<FeedEntry>();

            var gen = Interlocked.Increment(ref _populateGeneration);

            var maxItems = (_settings?.MaxFeedItems > 0) ? _settings.MaxFeedItems : 25;
            _filter.DefaultVisibleIds = (entries.Count > maxItems)
                ? new HashSet<string>(
                    entries.Take(maxItems)
                           .Select(x => x?.Id)
                           .Where(id => !string.IsNullOrEmpty(id)),
                    StringComparer.Ordinal)
                : null;

            await Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                AllEntries.Clear();
                _entryById.Clear();

                ResetAllRevealsFast();

                try
                {
                    StatusMessage = ResourceProvider.GetString("LOCFriendsAchFeed_Status_LoadingFromCache");
                }
                catch
                {
                    StatusMessage = "Loading…";
                }

                BeginBatchPopulate(gen, entries, fromRebuild);
            }), DispatcherPriority.Background);

            await Task.CompletedTask;
        }

        private void BeginBatchPopulate(int gen, List<FeedEntry> entries, bool fromRebuild)
        {
            int index = 0;

            Action addBatch = null;
            addBatch = () =>
            {
                if (gen != _populateGeneration) return;

                var end = Math.Min(entries.Count, index + PopulateBatchSize);

                for (; index < end; index++)
                {
                    var e = entries[index];
                    AllEntries.Add(e);

                    if (!string.IsNullOrEmpty(e?.Id))
                    {
                        _entryById[e.Id] = e;
                    }
                }

                if (index < entries.Count)
                {
                    Application.Current.Dispatcher.BeginInvoke(addBatch, DispatcherPriority.Background);
                    return;
                }

                try
                {
                    RebuildFilterLists();
                    RefreshViewAndGroups();

                    var updated = _feedService.GetCacheLastUpdated();
                    CacheLastUpdatedText = updated?.ToLocalTime().ToString("g") ?? ResourceProvider.GetString("LOCFriendsAchFeed_Status_Never");

                    if (fromRebuild)
                    {
                        var count = EntriesView?.Cast<object>().Count() ?? 0;
                        StatusMessage = string.Format(ResourceProvider.GetString("LOCFriendsAchFeed_Status_CacheRebuilt"), count);
                    }
                    else
                    {
                        UpdateStatusCount();
                    }
                }
                catch { }
            };

            addBatch();
        }

        private void ResetAllRevealsFast()
        {
            HashSet<string> ids;
            lock (_revealedLock)
            {
                if (_revealedEntryIds.Count == 0) return;
                ids = new HashSet<string>(_revealedEntryIds, StringComparer.Ordinal);
                _revealedEntryIds.Clear();
            }

            foreach (var id in ids)
            {
                if (string.IsNullOrEmpty(id)) continue;

                if (_entryById.TryGetValue(id, out var e) && e != null)
                {
                    try { e.IsRevealed = false; } catch { }
                }
            }
        }

        public void RegisterReveal(string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            lock (_revealedLock) { _revealedEntryIds.Add(id); }
        }

        public void UnregisterReveal(string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            lock (_revealedLock) { _revealedEntryIds.Remove(id); }
        }

        public void ToggleReveal(FeedEntry entry)
        {
            if (entry == null) return;

            try
            {
                if (string.IsNullOrWhiteSpace(entry.AchievementIconUnlockedUrl))
                {
                    entry.AchievementIconUnlockedUrl = entry.AchievementIconUrl;
                }

                entry.IsRevealed = !entry.IsRevealed;

                if (entry.IsRevealed) RegisterReveal(entry.Id);
                else UnregisterReveal(entry.Id);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Error toggling achievement reveal.");
            }
        }

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

                    await Application.Current.Dispatcher.BeginInvoke(new Action(RefreshViewAndGroups), DispatcherPriority.Background);
                }
                catch (OperationCanceledException) { }
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

            var showGameInHeader = GameNameProvider == null;
            var filteredEntries = EntriesView.Cast<object>()
                                            .OfType<FeedEntry>()
                                            .ToList();

            var groups = FeedGroupingBuilder.BuildGroups(filteredEntries, showGameInHeader, AsLocalFromUtc);
            foreach (var g in groups)
            {
                GroupedEntries.Add(g);
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
            catch { }
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

        private List<FeedEntry> LoadRawEntriesFromCache(string gameName = null)
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

        public void CancelRebuild()
        {
            try
            {
                if (_feedService != null && _feedService.IsRebuilding)
                {
                    _userRequestedCancel = true;
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
                var now = DateTime.UtcNow;
                lock (_progressUiLock)
                {
                    var pct = report?.PercentComplete ?? 0;
                    var isFinalish = pct >= 100 || (report?.TotalSteps > 0 && report.CurrentStep >= report.TotalSteps);

                    if (!isFinalish && (now - _lastProgressUiUpdateUtc) < ProgressUiMinInterval)
                    {
                        return;
                    }

                    _lastProgressUiUpdateUtc = now;
                }

                Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        ProgressPercent = report?.PercentComplete ?? 0;
                        var msg = report?.Message ?? _feedService.GetLastRebuildStatus() ?? string.Empty;

                        if (report?.IsCanceled == true)
                        {
                            if (!_userRequestedCancel)
                            {
                                msg = _feedService.GetLastRebuildStatus() ?? string.Empty;
                            }
                            else
                            {
                                _userRequestedCancel = false;
                            }
                        }

                        StatusMessage = msg;

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
                }), DispatcherPriority.Background);
            }
            catch (Exception ex)
            {
                _logger?.Debug($"Service_RebuildProgress dispatch failed: {ex.Message}");
            }
        }

        public void Dispose()
        {
            try
            {
                var disp = Application.Current?.Dispatcher;
                if (disp != null)
                {
                    if (disp.CheckAccess()) ResetAllRevealsFast();
                    else disp.Invoke(new Action(ResetAllRevealsFast));
                }
                else
                {
                    ResetAllRevealsFast();
                }
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Error resetting reveals on dispose.");
            }

            try
            {
                _feedService.CacheChanged -= FeedService_CacheChanged;
                _feedService.RebuildProgress -= Service_RebuildProgress;
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

            try
            {
                _decorateCts?.Cancel();
                _decorateCts?.Dispose();
                _decorateCts = null;
            }
            catch { }
        }
    }
}
