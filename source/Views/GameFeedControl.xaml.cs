using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using FriendsAchievementFeed.Models;
using FriendsAchievementFeed.Services;
using Playnite.SDK;
using Playnite.SDK.Models;

namespace FriendsAchievementFeed.Views
{
    public partial class GameFeedControl : Playnite.SDK.Controls.PluginUserControl, INotifyPropertyChanged
    {
        private Game _lastGame;
        private Game _gameContext;
        private readonly FeedControlLogic _logic;
        private readonly FriendsAchievementFeedSettings _pluginSettings;

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));



        public GameFeedControl(
            IPlayniteAPI api,
            FriendsAchievementFeedSettings settings,
            ILogger logger,
            FeedManager feedService,
            FeedControlLogic logic,
            Game game)
        {
            _logic = logic;
            _pluginSettings = settings;

            InitializeComponent();

            MainControl.DisposeLogicOnUnload = false;

            // Update layout when relevant settings change (e.g. GameFeedTabHeight)
            if (_pluginSettings != null)
            {
                _pluginSettings.PropertyChanged += PluginSettings_PropertyChanged;
                this.Unloaded += (s, e) => _pluginSettings.PropertyChanged -= PluginSettings_PropertyChanged;
            }
            this.Loaded += GameFeedControl_Loaded;

            MainControl.Logic = _logic;
            DataContext = _logic;


            if (game != null)
            {
                _gameContext = game;
                _lastGame = game;

                _logic.GameNameProvider = () => _gameContext?.Name;
                _logic.GameIdProvider = () => _gameContext?.Id;
                _logic.NotifyCommandsChanged();

                // When switching to a game view, clear any global game filter
                _logic.GameSearchText = string.Empty;

                ApplyGameViewLayout();
            }
        }

        public GameFeedControl(
            IPlayniteAPI api,
            FriendsAchievementFeedSettings settings,
            ILogger logger,
            FeedManager feedService,
            FeedControlLogic logic) : this(api, settings, logger, feedService, logic, null)
        {
        }


        private void PluginSettings_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(FriendsAchievementFeedSettings.GameFeedTabHeight))
            {
                Application.Current?.Dispatcher?.BeginInvoke(new Action(ApplyGameViewLayout));
            }
        }

        public override void GameContextChanged(Game oldContext, Game newContext)
        {
            base.GameContextChanged(oldContext, newContext);
            _gameContext = newContext;
            _lastGame = newContext;

            if (_logic == null) return;

            _logic.GameNameProvider = () => _gameContext?.Name;
            _logic.GameIdProvider = () => _gameContext?.Id;
            _logic.NotifyCommandsChanged();
            _logic.SetViewContext(isGameView: true);

            _logic.ClearFeed();

            // When game context changes, clear any global game filter
            _logic.GameSearchText = string.Empty;

            _ = _logic.RefreshAsync();
        }

        public void ApplyGameViewLayout()
        {
            try
            {
                var h = _pluginSettings?.GameFeedTabHeight ?? 1000;
                MainControl.Height = h > 0 ? h : 1000;

                MainControl.Margin = new Thickness(0, 8, 0, 0);
            }
            catch
            {
                // ignore
            }
        }

        private void GameFeedControl_Loaded(object sender, RoutedEventArgs e)
        {
            if (_logic == null) return;

            // When a game view becomes active, ensure it reflects the current game context.
            // This is critical for when the view is loaded in a tab, where GameContextChanged may not fire.
            _logic.GameNameProvider = () => _gameContext?.Name;
            _logic.GameIdProvider = () => _gameContext?.Id;
            _logic.NotifyCommandsChanged();
            _logic.SetViewContext(isGameView: true);

            _logic.ClearFeed();

            // When switching to a game view, clear any global game filter and refresh
            _logic.GameSearchText = string.Empty;
            _ = _logic.RefreshAsync();
        }
    }
}
