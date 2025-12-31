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
using FriendsAchievementFeed.Services;
using FriendsAchievementFeed.Models;
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
        private readonly AsyncCommand _triggerIncrementalScanCmd;
        private readonly AsyncCommand _refreshCmd;
        private readonly AsyncCommand _cancelRebuildCmd;

        // Throttle progress UI updates
        private readonly object _progressUiLock = new object();
        private DateTime _lastProgressUiUpdateUtc = DateTime.MinValue;
        private static readonly TimeSpan ProgressUiMinInterval = TimeSpan.FromMilliseconds(50);
        private bool _userRequestedCancel = false;

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

        public ILogger Logger => _logger;

        public int FriendAvatarSize => _settings?.FriendAvatarSize ?? 32;
        public int AchievementIconSize => _settings?.AchievementIconSize ?? 40;

        public bool ShowSelfUnlockTime => _settings?.IncludeSelfUnlockTime ?? false;
        public bool HideAchievementsLockedForSelf => _settings?.HideAchievementsLockedForSelf ?? false;

        private bool _isLoading;
        public bool IsLoading
        {
            get => _isLoading;
            set
            {
                if (!SetField(ref _isLoading, value)) return;
                NotifyCommandsChanged();
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

        /// <summary>
        /// Optional provider that returns the current Game.Name for per-game views.
        /// Null = global view.
        /// </summary>
        public Func<string> GameNameProvider { get; set; }
        public Func<Guid?> GameIdProvider { get; set; }

        public ICommand TriggerRebuildCommand { get; }
        public ICommand TriggerIncrementalScanCommand { get; }
        public ICommand RefreshCommand { get; }
        public ICommand CancelRebuildCommand { get; }

        // MUST be assignable because initialized in InitializePerGame()
        public ICommand RefreshCurrentGameCommand { get; private set; }
        public ICommand QuickScanCommand { get; private set; }

        protected virtual string GetGameNameForFilter() => GameNameProvider?.Invoke();

        private readonly FeedEntryFilter _filter = new FeedEntryFilter();

        // Snapshot from cache (friend-only)
        private List<FeedEntry> _rawCachedEntries = new List<FeedEntry>();

        private readonly FeedEntryFactory _entryFactory = new FeedEntryFactory();
        private FeedEntryHydrator _hydrator;

        public ObservableCollection<FeedEntry> AllEntries { get; } = new ObservableCollection<FeedEntry>();
        public ICollectionView EntriesView { get; private set; }
        public ObservableCollection<FeedGroup> GroupedEntries { get; } = new ObservableCollection<FeedGroup>();
        public bool HasAnyEntries => GroupedEntries != null && GroupedEntries.Count > 0;

        public ObservableCollection<string> FriendFilters { get; } = new ObservableCollection<string>();
        public ObservableCollection<string> GameFilters { get; } = new ObservableCollection<string>();

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

        // ---- Incremental scan controls (UI-bindable) ----

        private int _incrementalRecentFriendsCount = 5;
        public int IncrementalRecentFriendsCount
        {
            get => _incrementalRecentFriendsCount;
            set
            {
                var v = Math.Max(0, value);
                if (!SetField(ref _incrementalRecentFriendsCount, v)) return;
                NotifyCommandsChanged();
            }
        }

        private int _incrementalRecentGamesPerFriend = 5;
        public int IncrementalRecentGamesPerFriend
        {
            get => _incrementalRecentGamesPerFriend;
            set
            {
                var v = Math.Max(0, value);
                if (!SetField(ref _incrementalRecentGamesPerFriend, v)) return;
                NotifyCommandsChanged();
            }
        }

        // Debounce token for filter refresh
        private CancellationTokenSource _filterCts;

        // Debounce token for cache reload (cache changed events can burst)
        private CancellationTokenSource _cacheReloadCts;

        // Debounce token for re-applying the current raw list (MaxFeedItems changes, etc.)
        private CancellationTokenSource _reapplyCts;

        // Debounce token for auth checks (cookie / API key validity)
        private CancellationTokenSource _authCheckCts;

        // Last known auth check result: true=valid, false=invalid, null=unknown/pending
        private bool? _authValid = null;
        private string _authMessage = null;

        // Track only entries that have been temporarily revealed
        private readonly HashSet<string> _revealedEntryIds = new HashSet<string>(StringComparer.Ordinal);
        private readonly object _revealedLock = new object();

        // Fast lookup for reveal reset without scanning AllEntries
        private readonly Dictionary<string, FeedEntry> _entryById = new Dictionary<string, FeedEntry>(StringComparer.Ordinal);

        // Batch populate to avoid UI freeze on big lists
        private const int PopulateBatchSize = 250;
        private int _populateGeneration = 0;

        // ---- Centralized auth state ----

        private bool IsSteamKeyConfigured =>
            _settings != null &&
            !string.IsNullOrWhiteSpace(_settings.SteamUserId) &&
            !string.IsNullOrWhiteSpace(_settings.SteamApiKey);

        private bool IsSteamAuthValid => _authValid == true;

        // Treat null (unknown/pending) as "not ready" to block actions until check completes.
        private bool IsSteamReady => IsSteamKeyConfigured && IsSteamAuthValid;

        // UI-bindable (optional, but useful for banners/panels)
        public bool SteamReady => IsSteamReady;
        public string SteamAuthMessage => _authMessage;

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

            InitializeView(); // collections, hydrator, view wiring

            _feedService.CacheChanged += FeedService_CacheChanged;

            _triggerRebuildCmd = new AsyncCommand(async _ =>
            {
                await TriggerRebuild(null, default).ConfigureAwait(false);
            }, _ => !IsLoading && !_feedService.IsRebuilding && IsSteamReady);

            _triggerIncrementalScanCmd = new AsyncCommand(async _ =>
            {
                await TriggerIncrementalScanAsync(default).ConfigureAwait(false);
            }, _ => !IsLoading && !_feedService.IsRebuilding && IsSteamReady);

            // Refresh stays allowed even without Steam configured (it can load cache),
            // but will block rebuild paths via EnsureSteamReadyAsync.
            _refreshCmd = new AsyncCommand(
                async _ => await RefreshAsync(default).ConfigureAwait(false),
                _ => !IsLoading);

            _cancelRebuildCmd = new AsyncCommand(async _ =>
            {
                CancelRebuild();
                await Task.CompletedTask;
            }, _ => _feedService.IsRebuilding);

            TriggerRebuildCommand = _triggerRebuildCmd;
            TriggerIncrementalScanCommand = _triggerIncrementalScanCmd;
            RefreshCommand = _refreshCmd;
            CancelRebuildCommand = _cancelRebuildCmd;

            InitializePerGame();   // per-game command wiring
            InitializeQuickScan(); // wire quick-scan command

            HookSettingsChanges();
            QueueValidateAuth();
            TryInitializeFromServiceState();

            _feedService.RebuildProgress += Service_RebuildProgress;
        }

        private static DateTime AsLocalFromUtc(DateTime dt)
        {
            if (dt.Kind == DateTimeKind.Local) return dt;
            if (dt.Kind == DateTimeKind.Utc) return dt.ToLocalTime();
            return DateTime.SpecifyKind(dt, DateTimeKind.Utc).ToLocalTime();
        }

        public void NotifyCommandsChanged()
        {
            _triggerRebuildCmd?.RaiseCanExecuteChanged();
            _triggerIncrementalScanCmd?.RaiseCanExecuteChanged();
            _refreshCmd?.RaiseCanExecuteChanged();
            _cancelRebuildCmd?.RaiseCanExecuteChanged();
            RaisePerGameCanExecuteChanged();
            RaiseQuickScanCanExecuteChanged();
        }

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

                    case nameof(_settings.IncludeSelfUnlockTime):
                        OnPropertyChanged(nameof(ShowSelfUnlockTime));
                        QueueRefreshView();
                        break;

                    case nameof(_settings.HideAchievementsLockedForSelf):
                        OnPropertyChanged(nameof(HideAchievementsLockedForSelf));

                        RunOnUi(() =>
                        {
                            ApplyHideLockedSettingToViewEntries();
                            ResetAllRevealsFast();
                            RefreshViewAndGroups();
                        });

                        break;

                    case nameof(_settings.MaxFeedItems):
                        QueueReapplyFromRaw();
                        break;

                    case nameof(_settings.SteamUserId):
                    case nameof(_settings.SteamApiKey):
                        QueueValidateAuth();
                        break;
                }
            };
        }

        private void QueueValidateAuth()
        {
            _authCheckCts?.Cancel();
            _authCheckCts?.Dispose();
            _authCheckCts = new CancellationTokenSource();
            var token = _authCheckCts.Token;

            Task.Run(async () =>
            {
                try
                {
                    // Small debounce to allow rapid typing/save
                    await Task.Delay(250, token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();

                    // If basic strings missing, mark auth invalid and update immediately on UI thread.
                    if (!IsSteamKeyConfigured)
                    {
                        _authValid = false;
                        _authMessage = ResourceProvider.GetString("LOCFriendsAchFeed_Error_SteamNotConfigured");
                        RunOnUi(() =>
                        {
                            OnPropertyChanged(nameof(SteamReady));
                            OnPropertyChanged(nameof(SteamAuthMessage));
                            UpdateStatusCount();
                            NotifyCommandsChanged();
                        });
                        return;
                    }

                    // Indicate we're checking
                    _authValid = null;
                    _authMessage = ResourceProvider.GetString("LOCFriendsAchFeed_Settings_CheckingSteamAuth") ?? "Checking Steam authentication...";
                    RunOnUi(() =>
                    {
                        OnPropertyChanged(nameof(SteamReady));
                        OnPropertyChanged(nameof(SteamAuthMessage));
                        UpdateStatusCount();
                        NotifyCommandsChanged();
                    });

                    // Validate Steam cookies/profile presence (and anything else your service checks)
                    var result = await _feedService.TestSteamAuthAsync().ConfigureAwait(false);

                    // Save result and refresh UI
                    _authValid = result.Success;
                    _authMessage = result.Message;

                    RunOnUi(() =>
                    {
                        OnPropertyChanged(nameof(SteamReady));
                        OnPropertyChanged(nameof(SteamAuthMessage));
                        UpdateStatusCount();
                        NotifyCommandsChanged();
                    });
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    _logger?.Error(ex, "Error validating Steam auth settings.");
                    _authValid = false;
                    _authMessage = ResourceProvider.GetString("LOCFriendsAchFeed_Error_SteamNotConfigured") ?? "Steam not configured.";
                    RunOnUi(() =>
                    {
                        OnPropertyChanged(nameof(SteamReady));
                        OnPropertyChanged(nameof(SteamAuthMessage));
                        UpdateStatusCount();
                        NotifyCommandsChanged();
                    });
                }
            }, token);
        }

        private void RunOnUi(Action action, DispatcherPriority priority = DispatcherPriority.Normal)
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

        private void InitializeView()
        {
            _hydrator = new FeedEntryHydrator(_feedService.Cache, _entryFactory);

            EntriesView = CollectionViewSource.GetDefaultView(AllEntries);
            EntriesView.Filter = o => _filter.Matches(o as FeedEntry);

            GroupedEntries.CollectionChanged += (s, e) =>
            {
                OnPropertyChanged(nameof(HasAnyEntries));
            };
        }

        private void ApplyHideLockedSettingToViewEntries()
        {
            var hideLocked = HideAchievementsLockedForSelf;

            foreach (var e in AllEntries)
            {
                if (e != null)
                {
                    e.HideAchievementsLockedForSelf = hideLocked;
                }
            }
        }

        private void QueueReapplyFromRaw()
        {
            _reapplyCts?.Cancel();
            _reapplyCts?.Dispose();
            _reapplyCts = new CancellationTokenSource();
            var token = _reapplyCts.Token;

            Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(50, token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();

                    var rawFriend = _rawCachedEntries ?? new List<FeedEntry>();

                    var hydrated = _hydrator.HydrateForUi(rawFriend, token) ?? new List<FeedEntry>();
                    hydrated = hydrated
                        .Where(e => e != null)
                        .OrderByDescending(e => e.FriendUnlockTime)
                        .ThenByDescending(e => e.Id, StringComparer.Ordinal)
                        .ToList();

                    await ApplyEntriesToUiAsync(hydrated, fromRebuild: false, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    _logger?.Error(ex, "Error re-applying view entries.");
                }
            });
        }

        private async Task ReloadFromCacheToUiAsync(bool fromRebuild, CancellationToken token)
        {
            var gameName = GetGameNameForFilter();

            var hydrated = await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();

                var rawFriend = LoadRawFriendEntriesFromCache(gameName);
                _rawCachedEntries = rawFriend ?? new List<FeedEntry>();

                token.ThrowIfCancellationRequested();

                var list = _hydrator.HydrateForUi(_rawCachedEntries, token) ?? new List<FeedEntry>();
                list = list
                    .Where(e => e != null)
                    .OrderByDescending(e => e.FriendUnlockTime)
                    .ThenByDescending(e => e.Id, StringComparer.Ordinal)
                    .ToList();

                return list;
            }, token).ConfigureAwait(false);

            await ApplyEntriesToUiAsync(hydrated, fromRebuild, token).ConfigureAwait(false);
        }

        private async Task ApplyEntriesToUiAsync(List<FeedEntry> entries, bool fromRebuild, CancellationToken token)
        {
            entries ??= new List<FeedEntry>();

            var viewEntries = DecorateForView(entries);

            var gen = Interlocked.Increment(ref _populateGeneration);
            var maxItems = (_settings?.MaxFeedItems > 0) ? _settings.MaxFeedItems : 25;

            _filter.DefaultVisibleIds = (viewEntries.Count > maxItems)
                ? new HashSet<string>(
                    viewEntries.Take(maxItems)
                        .Select(x => x?.Id)
                        .Where(id => !string.IsNullOrEmpty(id)),
                    StringComparer.Ordinal)
                : null;

            await Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                ResetAllRevealsFast();

                AllEntries.Clear();
                _entryById.Clear();

                BeginBatchPopulate(gen, viewEntries, fromRebuild);
            }), DispatcherPriority.Background);
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

                ApplyHideLockedSettingToViewEntries();
                RebuildFilterLists();
                RefreshViewAndGroups();

                var updated = _feedService.Cache.GetFriendFeedLastUpdatedUtc();
                CacheLastUpdatedText = updated?.ToLocalTime().ToString("g")
                    ?? ResourceProvider.GetString("LOCFriendsAchFeed_Status_Never");

                UpdateStatusCount();
            };

            addBatch();
        }

        private List<FeedEntry> DecorateForView(List<FeedEntry> raw)
        {
            raw ??= new List<FeedEntry>();
            var hideLocked = _settings?.HideAchievementsLockedForSelf ?? false;

            var list = new List<FeedEntry>(raw.Count);

            foreach (var src in raw)
            {
                if (src == null) continue;

                var e = CloneForView(src);

                if (e.SelfUnlockTime.HasValue && e.SelfUnlockTime.Value <= DateTime.MinValue.AddSeconds(1))
                {
                    e.SelfUnlockTime = null;
                }

                e.HideAchievementsLockedForSelf = hideLocked;

                var myUnlocked = e.SelfUnlockTime.HasValue;
                e.IsRevealed = !hideLocked || myUnlocked;

                list.Add(e);
            }

            return list;
        }

        private static FeedEntry CloneForView(FeedEntry src)
        {
            return new FeedEntry
            {
                Id = src.Id,
                FriendSteamId = src.FriendSteamId,
                FriendPersonaName = src.FriendPersonaName,
                FriendAvatarUrl = src.FriendAvatarUrl,

                GameName = src.GameName,
                PlayniteGameId = src.PlayniteGameId,
                AppId = src.AppId,

                AchievementApiName = src.AchievementApiName,
                AchievementDisplayName = src.AchievementDisplayName,
                AchievementDescription = src.AchievementDescription,

                FriendUnlockTime = src.FriendUnlockTime,

                SelfAchievementIcon = src.SelfAchievementIcon,
                FriendAchievementIcon = src.FriendAchievementIcon,

                SelfUnlockTime = src.SelfUnlockTime,

                IsRevealed = src.IsRevealed
            };
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
                    e.IsRevealed = false;
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
            if (!entry.CanReveal) return;

            entry.IsRevealed = !entry.IsRevealed;

            if (!string.IsNullOrEmpty(entry.Id))
            {
                if (entry.IsRevealed) RegisterReveal(entry.Id);
                else UnregisterReveal(entry.Id);
            }
        }

        private void QueueRefreshView()
        {
            _filterCts?.Cancel();
            _filterCts?.Dispose();
            _filterCts = new CancellationTokenSource();
            var token = _filterCts.Token;

            Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(50, token).ConfigureAwait(false);
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
                if (_feedService != null && _feedService.IsRebuilding)
                {
                    return;
                }

                // If Steam settings are not configured, show an explicit message.
                if (!IsSteamKeyConfigured)
                {
                    StatusMessage = ResourceProvider.GetString("LOCFriendsAchFeed_Error_SteamNotConfigured");
                    return;
                }

                // If we performed an auth check and it's invalid, show that message.
                if (_authValid == false)
                {
                    StatusMessage = !string.IsNullOrWhiteSpace(_authMessage)
                        ? _authMessage
                        : ResourceProvider.GetString("LOCFriendsAchFeed_Error_SteamNotConfigured");
                    return;
                }

                // If auth is still pending/unknown, prefer the checking message if present.
                if (_authValid == null && !string.IsNullOrWhiteSpace(_authMessage))
                {
                    StatusMessage = _authMessage;
                    return;
                }

                var count = EntriesView?.Cast<object>().Count() ?? 0;
                StatusMessage = string.Format(ResourceProvider.GetString("LOCFriendsAchFeed_Status_Entries"), count);
            }
            catch
            {
                // swallow
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
                        NotifyCommandsChanged();
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
            _cacheReloadCts?.Cancel();
            _cacheReloadCts?.Dispose();
            _cacheReloadCts = new CancellationTokenSource();
            var token = _cacheReloadCts.Token;

            try
            {
                await Task.Delay(75, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();

                await ReloadFromCacheToUiAsync(fromRebuild: false, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger?.Error(ex, ResourceProvider.GetString("LOCFriendsAchFeed_Error_RefreshFeedAfterCacheChange"));
            }
        }

        // ---- Auth gating helpers (block progress + alert user) ----

        private void ShowAuthDialogOnce(string message)
        {
            var now = DateTime.UtcNow;
            if ((now - _lastAuthDialogUtc) < AuthDialogCooldown) return;
            _lastAuthDialogUtc = now;

            RunOnUi(() =>
            {
                try
                {
                    _api?.Dialogs?.ShowErrorMessage(message, "Friends Achievement Feed");
                }
                catch
                {
                    // last resort fallback
                    try { MessageBox.Show(message, "Friends Achievement Feed", MessageBoxButton.OK, MessageBoxImage.Error); }
                    catch { /* swallow */ }
                }
            });
        }

        private async Task<bool> EnsureSteamReadyAsync(bool showDialog, CancellationToken token)
        {
            // Key/user missing: immediate fail (no network call)
            if (!IsSteamKeyConfigured)
            {
                _authValid = false;
                _authMessage = ResourceProvider.GetString("LOCFriendsAchFeed_Error_SteamNotConfigured")
                              ?? "Steam API key / user id not configured.";

                RunOnUi(() =>
                {
                    OnPropertyChanged(nameof(SteamReady));
                    OnPropertyChanged(nameof(SteamAuthMessage));
                    UpdateStatusCount();
                    NotifyCommandsChanged();
                });

                if (showDialog) ShowAuthDialogOnce(_authMessage);
                return false;
            }

            // If we already know it’s good, proceed
            if (_authValid == true)
                return true;

            // Unknown or previously bad: run a fresh test (cookies/web auth etc.)
            var result = await _feedService.TestSteamAuthAsync().ConfigureAwait(false);
            _authValid = result.Success;
            _authMessage = result.Message;

            RunOnUi(() =>
            {
                OnPropertyChanged(nameof(SteamReady));
                OnPropertyChanged(nameof(SteamAuthMessage));
                UpdateStatusCount();
                NotifyCommandsChanged();
            });

            if (!result.Success && showDialog)
            {
                ShowAuthDialogOnce(_authMessage ?? "Steam web authentication is not available.");
            }

            return result.Success;
        }

        public async Task RefreshAsync(CancellationToken token = default)
        {
            RunOnUi(() => IsLoading = true);
            await Task.Yield();

            try
            {
                _feedService.Cache.EnsureDiskCacheOrClearMemory();

                if (_feedService.IsCacheValid())
                {
                    RunOnUi(() =>
                    {
                        try { StatusMessage = ResourceProvider.GetString("LOCFriendsAchFeed_Status_LoadingFromCache"); }
                        catch { StatusMessage = "Loading…"; }
                    });

                    await ReloadFromCacheToUiAsync(fromRebuild: false, token).ConfigureAwait(false);
                }
                else
                {
                    // Cache invalid: must be Steam-ready to rebuild
                    if (!await EnsureSteamReadyAsync(showDialog: true, token).ConfigureAwait(false))
                    {
                        // keep UI consistent
                        RunOnUi(() =>
                        {
                            ShowProgress = false;
                            ProgressPercent = 0;
                        });
                        return;
                    }

                    RunOnUi(() =>
                    {
                        try { StatusMessage = ResourceProvider.GetString("LOCFriendsAchFeed_Status_NoCache_Building"); }
                        catch { StatusMessage = "Building cache…"; }
                    });

                    await TriggerRebuild(null, token).ConfigureAwait(false);
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

        public async Task TriggerRebuild(CacheRebuildOptions options = null, CancellationToken externalToken = default)
        {
            // Block progress if Steam API key or Steam web auth cookies are not set/valid.
            if (!await EnsureSteamReadyAsync(showDialog: true, externalToken).ConfigureAwait(false))
            {
                RunOnUi(() =>
                {
                    IsLoading = false;
                    ShowProgress = false;
                    ProgressPercent = 0;
                    NotifyCommandsChanged();
                });
                return;
            }

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
                    NotifyCommandsChanged();
                });

                return;
            }

            RunOnUi(() =>
            {
                IsLoading = true;
                ShowProgress = true;
                ProgressPercent = 0;
                StatusMessage = string.Empty;
                NotifyCommandsChanged();
            });

            await Task.Yield();

            try
            {
                if (externalToken != default)
                {
                    extReg = externalToken.Register(() => _feedService.CancelActiveRebuild());
                    hasReg = true;
                }

                await _feedService.StartManagedRebuildAsync(null).ConfigureAwait(false);
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
                if (hasReg) extReg.Dispose();

                RunOnUi(() =>
                {
                    var rebuilding = _feedService?.IsRebuilding ?? false;
                    IsLoading = rebuilding;
                    ShowProgress = rebuilding;
                    NotifyCommandsChanged();
                });
            }
        }

        // ---- Incremental scan trigger (new) ----

        private async Task TriggerIncrementalScanAsync(CancellationToken externalToken = default)
        {
            // Block progress if Steam API key or Steam web auth cookies are not set/valid.
            if (!await EnsureSteamReadyAsync(showDialog: true, externalToken).ConfigureAwait(false))
            {
                RunOnUi(() =>
                {
                    IsLoading = false;
                    ShowProgress = false;
                    ProgressPercent = 0;
                    NotifyCommandsChanged();
                });
                return;
            }

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
                    NotifyCommandsChanged();
                });

                return;
            }

            RunOnUi(() =>
            {
                IsLoading = true;
                ShowProgress = true;
                ProgressPercent = 0;
                StatusMessage = string.Empty;
                NotifyCommandsChanged();
            });

            await Task.Yield();

            try
            {
                if (externalToken != default)
                {
                    extReg = externalToken.Register(() => _feedService.CancelActiveRebuild());
                    hasReg = true;
                }

                var friends = Math.Max(0, IncrementalRecentFriendsCount);
                var games = Math.Max(0, IncrementalRecentGamesPerFriend);

                await _feedService.StartManagedIncrementalScanAsync(friends, games).ConfigureAwait(false);

                // Ensure we reflect results immediately even if cache-change events are delayed/debounced.
                await ReloadFromCacheToUiAsync(fromRebuild: false, CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                RunOnUi(() => StatusMessage = ResourceProvider.GetString("LOCFriendsAchFeed_Rebuild_Canceled"));
                _logger?.Debug(ResourceProvider.GetString("LOCFriendsAchFeed_Debug_RebuildCanceledByUser"));
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "[FAF] Incremental scan failed.");
                RunOnUi(() => StatusMessage = ResourceProvider.GetString("LOCFriendsAchFeed_Error_FailedRebuild"));
            }
            finally
            {
                if (hasReg) extReg.Dispose();

                RunOnUi(() =>
                {
                    var rebuilding = _feedService?.IsRebuilding ?? false;
                    IsLoading = rebuilding;
                    ShowProgress = rebuilding;
                    NotifyCommandsChanged();
                });
            }
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
                NotifyCommandsChanged();
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
                    if ((pct <= 0 || double.IsNaN(pct)) && report != null && report.TotalSteps > 0)
                    {
                        pct = Math.Max(0, Math.Min(100, (report.CurrentStep * 100.0) / report.TotalSteps));
                    }
                    ProgressPercent = pct;

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
                        var pct = report?.PercentComplete ?? 0;
                        if ((pct <= 0 || double.IsNaN(pct)) && report != null && report.TotalSteps > 0)
                        {
                            pct = Math.Max(0, Math.Min(100, (report.CurrentStep * 100.0) / report.TotalSteps));
                        }
                        ProgressPercent = pct;

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

                        NotifyCommandsChanged();
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

        // ---- Per-game operation (moved here only for organization) ----

        private bool CanTriggerSingleGameScan()
        {
            var id = GameIdProvider?.Invoke();
            if (!id.HasValue || id.Value == Guid.Empty)
                return false;

            return !IsLoading && !_feedService.IsRebuilding && IsSteamReady;
        }

        private bool CanTriggerQuickScan()
        {
            return !IsLoading && !_feedService.IsRebuilding && IsSteamReady;
        }

        private async Task TriggerSingleGameScanAsync(CancellationToken externalToken = default)
        {
            // Block progress if Steam API key or Steam web auth cookies are not set/valid.
            if (!await EnsureSteamReadyAsync(showDialog: true, externalToken).ConfigureAwait(false))
            {
                RunOnUi(() =>
                {
                    IsLoading = false;
                    ShowProgress = false;
                    ProgressPercent = 0;
                    NotifyCommandsChanged();
                });
                return;
            }

            CancellationTokenRegistration extReg = default;
            var hasReg = false;

            var id = GameIdProvider?.Invoke();
            if (!id.HasValue || id.Value == Guid.Empty)
                return;

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
                    NotifyCommandsChanged();
                });

                return;
            }

            RunOnUi(() =>
            {
                IsLoading = true;
                ShowProgress = true;
                ProgressPercent = 0;
                StatusMessage = string.Empty;
                NotifyCommandsChanged();
            });

            await Task.Yield();

            try
            {
                if (externalToken != default)
                {
                    extReg = externalToken.Register(() => _feedService.CancelActiveRebuild());
                    hasReg = true;
                }

                await _feedService.StartManagedSingleGameScanAsync(id.Value).ConfigureAwait(false);

                // One refresh pass is enough (your previous code did it twice).
                await ReloadFromCacheToUiAsync(fromRebuild: false, CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                RunOnUi(() => StatusMessage = ResourceProvider.GetString("LOCFriendsAchFeed_Rebuild_Canceled"));
                _logger?.Debug(ResourceProvider.GetString("LOCFriendsAchFeed_Debug_RebuildCanceledByUser"));
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "[FAF] Single-game scan failed.");
                RunOnUi(() => StatusMessage = ResourceProvider.GetString("LOCFriendsAchFeed_Error_FailedRebuild"));
            }
            finally
            {
                if (hasReg) extReg.Dispose();

                RunOnUi(() =>
                {
                    var rebuilding = _feedService?.IsRebuilding ?? false;
                    IsLoading = rebuilding;
                    ShowProgress = rebuilding;
                    NotifyCommandsChanged();
                });
            }
        }

        // per-game command wiring
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

        private void RaisePerGameCanExecuteChanged()
        {
            _singleGameScanCmd?.RaiseCanExecuteChanged();
        }

        private void RaiseQuickScanCanExecuteChanged()
        {
            _quickScanCmd?.RaiseCanExecuteChanged();
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

            try
            {
                _authCheckCts?.Cancel();
                _authCheckCts?.Dispose();
                _authCheckCts = null;
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Error disposing auth check cancellation token.");
            }
        }
    }
}
