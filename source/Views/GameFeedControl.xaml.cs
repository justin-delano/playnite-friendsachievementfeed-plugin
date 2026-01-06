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
            Game game = null)
        {
            _logic = new FeedControlLogic(api, settings, feedService);
            _pluginSettings = settings;

            InitializeComponent();

            // This control owns the VM -> safe to dispose when unloaded.
            MainControl.DisposeLogicOnUnload = true;

            // Update layout when relevant settings change (e.g. GameFeedTabHeight)
            if (_pluginSettings != null)
            {
                _pluginSettings.PropertyChanged += PluginSettings_PropertyChanged;
                this.Unloaded += (s, e) => _pluginSettings.PropertyChanged -= PluginSettings_PropertyChanged;
            }

            MainControl.Logic = _logic;
            DataContext = _logic;


            if (game != null)
            {
                _gameContext = game;
                _lastGame = game;

                _logic.GameNameProvider = () => _gameContext?.Name;
                _logic.GameIdProvider = () => _gameContext?.Id;
                _logic.NotifyCommandsChanged();

                ApplyGameViewLayout();
            }
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

            _logic.GameNameProvider = () => _gameContext?.Name;
            _logic.GameIdProvider = () => _gameContext?.Id;
            _logic.NotifyCommandsChanged();

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
    }
}
