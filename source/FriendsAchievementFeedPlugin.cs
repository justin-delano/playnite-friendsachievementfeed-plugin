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

            _feedService = new AchievementFeedService(api, _settings, Logger);

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

        public override ISettings GetSettings(bool firstRunSettings)
        {
            return _settings;
        }

        public override UserControl GetSettingsView(bool firstRunView)
        {
            return new SettingsControl();
        }

        // === Menus ===

        public override IEnumerable<MainMenuItem> GetMainMenuItems(GetMainMenuItemsArgs args)
        {
            // Global menu entry removed; everything is via sidebar/game tab now.
            yield break;
        }

        public override IEnumerable<GameMenuItem> GetGameMenuItems(GetGameMenuItemsArgs args)
        {
            if (args.Games == null || !args.Games.Any())
            {
                yield break;
            }

                yield return new GameMenuItem
                {
                    Description = ResourceProvider.GetString("LOCFriendsAchFeed_Title_PluginName"),
                Action = gameMenuArgs =>
                {
                    var game = gameMenuArgs.Games.FirstOrDefault();
                    if (game != null)
                    {
                        ShowGameFeedWindow(game);
                    }
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
            // Single sidebar entry: Friends feed (global view)
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
            try
            {
                var initialGame = PlayniteApi.MainView?.SelectedGames?.FirstOrDefault();
                UpdateGameFeedGroupsFlag(initialGame);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to initialize HasGameFeedGroups on startup.");
            }

            // Background periodic update loop
            _backgroundCts = new System.Threading.CancellationTokenSource();
            var token = _backgroundCts.Token;

            System.Threading.Tasks.Task.Run(async () =>
            {
                var initialDelay = TimeSpan.FromSeconds(20);
                var interval = TimeSpan.FromHours(Math.Max(1, _settings.PeriodicUpdateHours));

                try
                {
                    await System.Threading.Tasks.Task.Delay(initialDelay, token);

                    while (!token.IsCancellationRequested)
                    {
                        try
                        {
                            var threshold = interval;
                            var cacheLast = _feedService.GetCacheLastUpdated();
                            Logger.Debug($"[PeriodicUpdate] Cache valid={_feedService.IsCacheValid()}, lastUpdatedUtc={cacheLast?.ToString() ?? "(none)"}");

                            if (!_settings.EnablePeriodicUpdates)
                            {
                                Logger.Debug("[PeriodicUpdate] Periodic updates disabled via settings; sleeping.");
                            }

                            if (_settings.EnablePeriodicUpdates &&
                                (!_feedService.IsCacheValid() ||
                                 !cacheLast.HasValue ||
                                 DateTime.UtcNow - cacheLast.Value >= threshold))
                            {
                                Logger.Debug("[PeriodicUpdate] Triggering incremental cache update...");

                                try
                                {
                                    // Subscribe to service progress for logging
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
                                        await _feedService.StartManagedRebuildAsync(CacheRebuildMode.Incremental).ConfigureAwait(false);
                                        Logger.Debug("[PeriodicUpdate] Incremental cache update completed.");

                                        // Optional toast notification
                                        try
                                        {
                                            if (_settings != null &&
                                                _settings.EnableNotifications &&
                                                _settings.NotifyPeriodicUpdates)
                                            {
                                                var lastStatus = _feedService.GetLastRebuildStatus()
                                                                  ?? ResourceProvider.GetString("LOCFriendsAchFeed_Rebuild_Completed");

                                                try
                                                {
                                                        PlayniteApi.Notifications.Add(new NotificationMessage(
                                                        $"FriendsAchievementFeed-Periodic-{Guid.NewGuid()}",
                                                        $"{ResourceProvider.GetString("LOCFriendsAchFeed_Title_PluginName")}\n{lastStatus}",
                                                        NotificationType.Info));
                                                }
                                                catch (Exception ex)
                                                {
                                                    Logger.Debug(ex, "Failed to show periodic notification.");
                                                }
                                            }
                                        }
                                        catch (Exception ex)
                                        {
                                            Logger.Debug(ex, "Error in periodic update notification block.");
                                        }
                                    }
                                    finally
                                    {
                                        _feedService.RebuildProgress -= progressHandler;
                                    }

                                    // Re-evaluate game flag after rebuild
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
                                catch (OperationCanceledException) when (token.IsCancellationRequested)
                                {
                                    // graceful shutdown
                                }
                                catch (Exception ex)
                                {
                                    Logger.Error(ex, "[PeriodicUpdate] Failed to perform incremental update");
                                }
                            }
                            else
                            {
                                Logger.Debug("[PeriodicUpdate] Cache is recent; skipping update.");
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.Error(ex, "[PeriodicUpdate] Unexpected error in periodic update loop");
                        }

                        try
                        {
                            await System.Threading.Tasks.Task.Delay(interval, token);
                        }
                        catch (OperationCanceledException) when (token.IsCancellationRequested)
                        {
                            break;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // plugin or app shutting down
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "Failed to start periodic cache update loop");
                }
            }, token);

            // Subscribe to library changes to trigger targeted cache updates for changed games
            try
            {
                PlayniteApi.Database.Games.CollectionChanged += Database_Games_CollectionChanged;
            }
            catch (Exception ex)
            {
                Logger.Debug(ex, "Failed to subscribe to database collection changed events.");
            }
        }

        public override void OnApplicationStopped(OnApplicationStoppedEventArgs args)
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
            try
            {
                PlayniteApi.Database.Games.CollectionChanged -= Database_Games_CollectionChanged;
            }
            catch
            {
                // ignore
            }
        }

        private void Database_Games_CollectionChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            try
            {
                var ids = new List<int>();

                if (e.NewItems != null)
                {
                    foreach (var ni in e.NewItems)
                    {
                        if (ni is Game g && int.TryParse(g.GameId, out var appId) && appId != 0)
                        {
                            ids.Add(appId);
                        }
                    }
                }

                if (e.OldItems != null)
                {
                    foreach (var oi in e.OldItems)
                    {
                        if (oi is Game g && int.TryParse(g.GameId, out var appId) && appId != 0)
                        {
                            ids.Add(appId);
                        }
                    }
                }

                if (ids.Count == 0)
                {
                    return;
                }

                // Deduplicate
                ids = ids.Distinct().ToList();

                // Fire-and-forget targeted update
                System.Threading.Tasks.Task.Run(async () =>
                {
                    try
                    {
                        await _feedService.UpdateCacheForAppIdsAsync(ids).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Logger.Error(ex, "Error during targeted cache update after library change.");
                    }
                });
            }
            catch (Exception ex)
            {
                Logger.Debug(ex, "Error handling library collection change.");
            }
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
                    var allEntries = _feedService.GetAllCachedEntries() ?? new List<FeedEntry>();

                    // If any cached entry for this game exists, flag as having groups
                    hasGroups = allEntries.Any(e =>
                        string.Equals(e.GameName, game.Name, StringComparison.OrdinalIgnoreCase));
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
