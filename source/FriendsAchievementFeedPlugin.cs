using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows;
using FriendsAchievementFeed.Models;
using FriendsAchievementFeed.Services;
using FriendsAchievementFeed.Views;
using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using Common;
using System.Threading.Tasks;
using System.Threading;

namespace FriendsAchievementFeed
{
    public class FriendsAchievementFeedPlugin : GenericPlugin
    {
        private static readonly Guid SteamPluginId = Guid.Parse("cb91dfc9-b977-43bf-8e70-55f46e410fab");
        private static readonly ILogger _logger = LogManager.GetLogger(nameof(FriendsAchievementFeedPlugin));

        public const string SourceName = "FriendsAchievementFeed";

        private readonly FriendsAchievementFeedSettings _settings;
        private readonly AchievementFeedService _feedService;
        private readonly NotificationPublisher _notifications;

        private readonly BackgroundUpdateService _backgroundUpdates;

        // Startup gate: ensure SteamID64 persistence happens BEFORE anything else.
        private readonly object _startupInitLock = new object();
        private Task _startupInitTask;
        private CancellationTokenSource _startupInitCts;

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

            _feedService = new AchievementFeedService(api, _settings, _logger, this);
            _notifications = new NotificationPublisher(api, _settings, _logger);
            _backgroundUpdates = new BackgroundUpdateService(_feedService, _settings, _logger, _notifications, UpdateGameFeedGroupsForCurrentGame);
            // Theme integration
            AddCustomElementSupport(new AddCustomElementSupportArgs
            {
                SourceName = SourceName,
                ElementList = new List<string> { "GameFeedTab" }
            });

            // Listen for new games entering the database to auto-scan Steam additions.
            PlayniteApi?.Database?.Games?.ItemCollectionChanged += Games_ItemCollectionChanged;

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
                var control = new GameFeedControl(PlayniteApi, _settings, _logger, _feedService);
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
            return new SettingsControl(this, _logger);
        }

        // === Menus ===

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
                    catch (Exception ex) { _logger.Error(ex, "Failed to open game feed window."); }
                }
            };
        }

        // === Windows ===

        private void ShowGameFeedWindow(Game game)
        {
            var view = new GameFeedControl(PlayniteApi, _settings, _logger, _feedService, game);

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
                    Content = new GlobalFeedControl(PlayniteApi, _settings, _logger, _feedService)
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
            // IMPORTANT:
            // Do not start any other plugin work here.
            // We gate startup so SteamID64 persistence happens first.
            EnsureStartupInitialized();
        }

        public override void OnApplicationStopped(OnApplicationStoppedEventArgs args)
        {
            // Stop startup init if still running
            try
            {
                _startupInitCts?.Cancel();
                _startupInitCts?.Dispose();
                _startupInitCts = null;

                PlayniteApi?.Database?.Games?.ItemCollectionChanged -= Games_ItemCollectionChanged;
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "[FAF] Error during application shutdown cleanup.");
            }

            _backgroundUpdates.Stop();
        }

        private void EnsureStartupInitialized()
        {
            lock (_startupInitLock)
            {
                if (_startupInitTask != null)
                {
                    return;
                }

                _startupInitCts = new CancellationTokenSource();
                var token = _startupInitCts.Token;

                _startupInitTask = Task.Run(async () =>
                {
                    // 1) Persist SteamID64 BEFORE anything else starts
                    await PersistSelfSteamId64OnStartupAsync(token).ConfigureAwait(false);

                    // 2) Now start the rest of the plugin startup work
                    try
                    {
                        await PlayniteApi.MainView.UIDispatcher.InvokeAsync(() =>
                        {
                            InitializeHasGameFeedGroups();
                            _backgroundUpdates.Start();
                        });
                    }
                    catch (Exception ex)
                    {
                        _logger.Error(ex, "[FAF] Failed during post-auth startup initialization.");
                    }
                }, token);
            }
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
                _logger.Error(ex, "Failed to initialize HasGameFeedGroups on startup.");
            }
        }

        private async Task PersistSelfSteamId64OnStartupAsync(CancellationToken token)
        {
            // If you already have it saved, don't touch it.
            // NOTE: This assumes you have _settings.SteamUserId (SteamID64) in settings.
            if (!string.IsNullOrWhiteSpace(_settings?.SteamUserId))
            {
                return;
            }

            // Short retries: catches "cookies not ready yet" without delaying startup too long.
            // Total wait ~12 seconds. You can extend if needed.
            var retryDelays = new[]
            {
                TimeSpan.Zero,
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(10),
            };

            for (int attempt = 0; attempt < retryDelays.Length && !token.IsCancellationRequested; attempt++)
            {
                if (retryDelays[attempt] > TimeSpan.Zero)
                {
                    try { await Task.Delay(retryDelays[attempt], token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
                }

                try
                {
                    using (var steam = new SteamClient(PlayniteApi, _logger, GetPluginUserDataPath()))
                    {
                        var id = await steam.GetSelfSteamId64Async(token).ConfigureAwait(false);

                        if (!string.IsNullOrWhiteSpace(id) && ulong.TryParse(id, out _))
                        {
                            _settings.SteamUserId = id;

                            // Save on UI thread (safer with Playnite settings)
                            await PlayniteApi.MainView.UIDispatcher.InvokeAsync(() =>
                            {
                                SavePluginSettings(_settings);
                            });

                            _logger.Info($"[FAF] Detected SteamID64 from Playnite Steam session and saved to settings: {id}");
                            return;
                        }
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, $"[FAF] SteamID64 auto-detect attempt {attempt + 1} failed.");
                }
            }

            _logger.Debug("[FAF] SteamID64 not detected on startup (user may not be logged into Steam via Playnite web login yet).");
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
                _logger.Error(ex, "[PeriodicUpdate] Failed to update HasGameFeedGroups after cache rebuild.");
            }
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
                _logger.Error(ex, $"Failed to evaluate feed groups for game {game?.Name ?? "(null)"}.");
            }

            _settings.HasGameFeedGroups = hasGroups;
        }

        private void Games_ItemCollectionChanged(object sender, ItemCollectionChangedEventArgs<Game> e)
        {
            if (e?.AddedItems == null || e.AddedItems.Count == 0)
            {
                return;
            }

            foreach (var game in e.AddedItems)
            {
                if (game == null)
                {
                    continue;
                }

                if (game.PluginId != SteamPluginId)
                {
                    continue; // Only auto-scan Steam games.
                }

                // Fire and forget; StartManagedSingleGameScanAsync already manages progress/state.
                _ = TriggerNewSteamGameScanAsync(game);
            }
        }

        private Task TriggerNewSteamGameScanAsync(Game game)
        {
            return Task.Run(async () =>
            {
                try
                {
                    _logger.Info($"[FAF] Detected new Steam game '{game?.Name}' ({game?.GameId}); starting single-game scan.");
                    await _feedService.StartManagedSingleGameScanAsync(game.Id).ConfigureAwait(false);
                    UpdateGameFeedGroupsFlag(game);
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, $"[FAF] Failed auto-scan for new Steam game '{game?.Name}' ({game?.GameId}).");
                }
            });
        }
    }
}
