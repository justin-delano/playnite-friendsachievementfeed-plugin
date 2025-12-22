using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Controls;
using System.Windows.Media;
using FriendsAchievementFeed.Models;
using FriendsAchievementFeed.Services;
using FriendsAchievementFeed.Views;
using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using Common;

namespace FriendsAchievementFeed
{
    public class FriendsAchievementFeedPlugin : GenericPlugin
    {
        private static readonly ILogger Logger = LogManager.GetLogger(nameof(FriendsAchievementFeedPlugin));

        public const string SourceName = "FriendsAchievementFeed";

        private readonly FriendsAchievementFeedSettings _settings;
        private readonly AchievementFeedService _feedService;
        private System.Threading.CancellationTokenSource _backgroundCts;

        public override Guid Id { get; } =
            Guid.Parse("10f90193-72aa-4cdb-b16d-3e6b1f0feb17");

        public FriendsAchievementFeedSettings Settings => _settings;
        public AchievementFeedService FeedService => _feedService;

        public FriendsAchievementFeedPlugin(IPlayniteAPI api) : base(api)
        {
            _settings = new FriendsAchievementFeedSettings(this);

            Properties = new GenericPluginProperties
            {
                HasSettings = true
            };

            _feedService = new AchievementFeedService(api, _settings, Logger, this);

            // Theme integration
            AddCustomElementSupport(new AddCustomElementSupportArgs
            {
                SourceName = SourceName,
                ElementList = new List<string> { "GameFeedTab" }
            });

            AddSettingsSupport(new AddSettingsSupportArgs
            {
                SourceName = SourceName,
                SettingsRoot = nameof(Settings)
            });
        }

        public override Control GetGameViewControl(GetGameViewControlArgs args)
        {
            if (args.Name == "GameFeedTab")
            {
                var control = new GameFeedControl(PlayniteApi, _settings, Logger, _feedService);
                control.ApplyGameViewLayout();
                return control;
            }

            return null;
        }

        public override ISettings GetSettings(bool firstRunSettings)
        {
            return _settings;
        }

        public override UserControl GetSettingsView(bool firstRunView)
        {
            return new SettingsControl(this);
        }

        // === Menus ===

        public override IEnumerable<MainMenuItem> GetMainMenuItems(GetMainMenuItemsArgs args)
        {
            // Global menu entry removed; everything is via sidebar/game tab now.
            yield break;
        }

        public override IEnumerable<GameMenuItem> GetGameMenuItems(GetGameMenuItemsArgs args)
        {
            if (args?.Games == null || !args.Games.Any())
            {
                yield break;
            }

            var game = args.Games.FirstOrDefault();
            if (game == null)
            {
                yield break;
            }

            // Put everything under this submenu:
            var section = ResourceProvider.GetString("LOCFriendsAchFeed_Title_PluginName"); // e.g. "Friends Achievement Feed"

            // 1) Open feed
            yield return new GameMenuItem
            {
                MenuSection = section,
                Description = "Open Feed",
                Action = _ =>
                {
                    try { ShowGameFeedWindow(game); }
                    catch (Exception ex) { Logger.Error(ex, "Failed to open game feed window."); }
                }
            };

            // Only add the forced-scan toggle for Steam games
            if (!int.TryParse(game.GameId, out var appId) || appId <= 0)
            {
                yield break;
            }

            var enabled = _settings?.IsForcedScanEnabled(appId) == true;

            // 2) Forced scan toggle
            yield return new GameMenuItem
            {
                MenuSection = section,
                Description = enabled ? "Disable forced scan" : "Enable forced scan",
                Action = __ =>
                {
                    try
                    {
                        _settings.BeginEdit();
                        _settings.ToggleForcedScan(appId);
                        _settings.EndEdit();
                    }
                    catch (Exception ex)
                    {
                        Logger.Error(ex, "Failed to toggle forced scan setting.");
                        try { _settings.CancelEdit(); } catch { }
                        return;
                    }

                    _ = System.Threading.Tasks.Task.Run(async () =>
                    {
                        try
                        {
                            await _feedService.StartManagedRebuildAsync().ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            Logger.Error(ex, "Failed to rebuild after forced scan toggle.");
                        }
                    });
                }
            };
        }


        // === Windows ===

        private void ShowGameFeedWindow(Game game)
        {
            var view = new GameFeedControl(PlayniteApi, _settings, Logger, _feedService, game);

            var windowOptions = new WindowOptions
            {
                ShowMinimizeButton = false,
                ShowMaximizeButton = true,
                ShowCloseButton = true,
                CanBeResizable = true,
                Width = 800,
                Height = 600
            };

            var host = new UserControl { Content = view };

            var titleFormat = ResourceProvider.GetString("LOCFriendsAchFeed_WindowTitle_GameFeedFor");
            var title = string.Format(titleFormat, game.Name);

            var window = PlayniteUiHelper.CreateExtensionWindow(title, host, windowOptions);
            window.Show();
        }

        // === Sidebar ===

        public override IEnumerable<SidebarItem> GetSidebarItems()
        {
            yield return new SidebarItem
            {
                Title = ResourceProvider.GetString("LOCFriendsAchFeed_Title_PluginName"),
                Type = SiderbarItemType.View,
                Icon = GetSidebarIcon(),
                Opened = () => new ContentControl
                {
                    Content = new GlobalFeedControl(PlayniteApi, _settings, Logger, _feedService)
                }
            };
        }

        private TextBlock GetSidebarIcon()
        {
            var tb = new TextBlock
            {
                Text = char.ConvertFromUtf32(0xed0d),
                FontSize = 18
            };

            var font = ResourceProvider.GetResource("FontIcoFont") as FontFamily;
            tb.FontFamily = font ?? new FontFamily("Segoe UI Symbol");
            tb.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");

            return tb;
        }

        // === Lifecycle ===

        public override void OnApplicationStarted(OnApplicationStartedEventArgs args)
        {
            InitializeHasGameFeedGroups();
            StartBackgroundUpdateLoop();
            SubscribeToLibraryChanges();
        }

        public override void OnApplicationStopped(OnApplicationStoppedEventArgs args)
        {
            StopBackgroundUpdateLoop();
            UnsubscribeFromLibraryChanges();
        }

        private void InitializeHasGameFeedGroups()
        {
            try
            {
                var initialGame = PlayniteApi.MainView?.SelectedGames?.FirstOrDefault();
                UpdateGameFeedGroupsFlag(initialGame);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to initialize HasGameFeedGroups on startup.");
            }
        }

        private void StartBackgroundUpdateLoop()
        {
            _backgroundCts = new System.Threading.CancellationTokenSource();
            var token = _backgroundCts.Token;

            System.Threading.Tasks.Task.Run(async () =>
            {
                await PeriodicUpdateLoop(token).ConfigureAwait(false);
            }, token);
        }

        private async System.Threading.Tasks.Task PeriodicUpdateLoop(System.Threading.CancellationToken token)
        {
            var interval = TimeSpan.FromHours(Math.Max(1, _settings.PeriodicUpdateHours));

            while (!token.IsCancellationRequested)
            {
                try
                {
                    await PerformUpdateIfNeeded(interval, token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "[PeriodicUpdate] Unexpected error in periodic update loop");
                }

                await DelayNextUpdate(interval, token).ConfigureAwait(false);
            }
        }

        private async System.Threading.Tasks.Task PerformUpdateIfNeeded(TimeSpan interval, System.Threading.CancellationToken token)
        {
            if (ShouldPerformUpdate(interval))
            {
                await ExecuteDeltaUpdate(token).ConfigureAwait(false);
            }
            else
            {
                Logger.Debug("[PeriodicUpdate] Cache is recent; skipping update.");
            }
        }

        private bool ShouldPerformUpdate(TimeSpan interval)
        {
            var cacheLast = _feedService.GetCacheLastUpdated();
            Logger.Debug($"[PeriodicUpdate] Cache valid={_feedService.IsCacheValid()}, lastUpdatedUtc={cacheLast?.ToString() ?? "(none)"}");

            return _settings.EnablePeriodicUpdates &&
                   (!_feedService.IsCacheValid() ||
                    !cacheLast.HasValue ||
                    DateTime.UtcNow - cacheLast.Value >= interval);
        }

        /// <summary>
        /// Delta-only cache update (works for first build too).
        /// </summary>
        private async System.Threading.Tasks.Task ExecuteDeltaUpdate(System.Threading.CancellationToken token)
        {
            Logger.Debug("[PeriodicUpdate] Triggering delta cache update...");

            try
            {
                EventHandler<ProgressReport> progressHandler = (s, report) =>
                {
                    if (report != null)
                    {
                        Logger.Debug($"[PeriodicUpdate] {report.Message} ({report.PercentComplete}%)");
                    }
                };

                try
                {
                    _feedService.RebuildProgress += progressHandler;

                    // Updated: mode-less, delta-only
                    await _feedService.StartManagedRebuildAsync().ConfigureAwait(false);

                    Logger.Debug("[PeriodicUpdate] Delta cache update completed.");
                    HandleUpdateCompletion();
                }
                finally
                {
                    _feedService.RebuildProgress -= progressHandler;
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // Graceful shutdown
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "[PeriodicUpdate] Failed to perform delta update");
            }
        }

        private void HandleUpdateCompletion()
        {
            ShowPeriodicUpdateNotification();
            UpdateGameFeedGroupsForCurrentGame();
        }

        private void ShowPeriodicUpdateNotification()
        {
            if (_settings?.EnableNotifications == true && _settings.NotifyPeriodicUpdates)
            {
                var lastStatus = _feedService.GetLastRebuildStatus() ?? ResourceProvider.GetString("LOCFriendsAchFeed_Rebuild_Completed");
                var message = $"{ResourceProvider.GetString("LOCFriendsAchFeed_Title_PluginName")}\n{lastStatus}";

                try
                {
                    PlayniteApi.Notifications.Add(new NotificationMessage(
                        $"FriendsAchievementFeed-Periodic-{Guid.NewGuid()}",
                        message,
                        NotificationType.Info));
                }
                catch (Exception ex)
                {
                    Logger.Debug(ex, "Failed to show periodic notification.");
                }
            }
        }

        private void UpdateGameFeedGroupsForCurrentGame()
        {
            try
            {
                var currentGame = PlayniteApi.MainView?.SelectedGames?.FirstOrDefault();
                UpdateGameFeedGroupsFlag(currentGame);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "[PeriodicUpdate] Failed to update HasGameFeedGroups after cache rebuild.");
            }
        }

        private async System.Threading.Tasks.Task DelayNextUpdate(TimeSpan interval, System.Threading.CancellationToken token)
        {
            try
            {
                await System.Threading.Tasks.Task.Delay(interval, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // Loop will terminate
            }
        }

        private void StopBackgroundUpdateLoop()
        {
            try
            {
                _backgroundCts?.Cancel();
                _feedService?.CancelActiveRebuild();
                _backgroundCts?.Dispose();
                _backgroundCts = null;
            }
            catch
            {
                // ignore shutdown errors
            }
        }

        private void SubscribeToLibraryChanges()
        {
            try
            {
                if (PlayniteApi.Database.Games is System.Collections.Specialized.INotifyCollectionChanged incc)
                {
                    incc.CollectionChanged += Database_Games_CollectionChanged;
                }
                else
                {
                    Logger.Debug("Database.Games does not implement INotifyCollectionChanged; skipping subscription.");
                }
            }
            catch (Exception ex)
            {
                Logger.Debug(ex, "Failed to subscribe to database collection changed events.");
            }
        }

        private void UnsubscribeFromLibraryChanges()
        {
            try
            {
                if (PlayniteApi.Database.Games is System.Collections.Specialized.INotifyCollectionChanged incc)
                {
                    incc.CollectionChanged -= Database_Games_CollectionChanged;
                }
            }
            catch
            {
                // ignore
            }
        }

        private void Database_Games_CollectionChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            var appIds = new HashSet<int>();

            ProcessGameItems(e.NewItems, appIds);
            ProcessGameItems(e.OldItems, appIds);

            if (!appIds.Any())
            {
                return;
            }

            TriggerTargetedCacheUpdate(appIds.ToList());
        }

        private void ProcessGameItems(System.Collections.IList items, HashSet<int> appIds)
        {
            if (items == null) return;

            foreach (var item in items)
            {
                if (item is Game game && int.TryParse(game.GameId, out var appId) && appId != 0)
                {
                    appIds.Add(appId);
                }
            }
        }

        private void TriggerTargetedCacheUpdate(List<int> appIds)
        {
            System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    await _feedService.UpdateCacheForAppIdsAsync(appIds).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "Error during targeted cache update after library change.");
                }
            });
        }

        // === Game selection / theme flag wiring ===

        public override void OnGameSelected(OnGameSelectedEventArgs args)
        {
            var game = args.NewValue?.FirstOrDefault();
            UpdateGameFeedGroupsFlag(game);
        }

        private void UpdateGameFeedGroupsFlag(Game game)
        {
            var hasGroups = false;

            try
            {
                if (game != null)
                {
                    hasGroups = _feedService.GameHasFeedEntries(game.Name);
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, $"Failed to evaluate feed groups for game {game?.Name ?? "(null)"}.");
            }

            _settings.HasGameFeedGroups = hasGroups;
        }
    }
}
