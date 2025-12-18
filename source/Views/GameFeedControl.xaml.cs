using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Collections.Specialized;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
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

        // Single constructor: optional `game` parameter. If `game` is provided,
        // apply the same layout and GameNameProvider that GameFeedGameView used.
        public GameFeedControl(
            IPlayniteAPI api,
            FriendsAchievementFeedSettings settings,
            ILogger logger,
            AchievementFeedService feedService,
            Game game = null)
        {
            _logic = new FeedControlLogic(api, settings, logger, feedService);
            _pluginSettings = settings;
            InitializeComponent();

            MainControl.Logic = _logic;
            DataContext = _logic;

            if (_logic.AllEntries is INotifyCollectionChanged incc)
            {
                incc.CollectionChanged += (s, e) => UpdateStatusMessage();
            }

            if (game != null)
            {
                _gameContext = game;
                _lastGame = game;
                _logic.GameNameProvider = () => _gameContext?.Name;
                try
                {
                    var h = _pluginSettings?.GameFeedTabHeight ?? 1000;
                    MainControl.Height = h > 0 ? h : 1000;
                    MainControl.Margin = new Thickness(0, 8, 0, 0);
                }
                catch
                {
                    // ignore if MainControl isn't available yet for some reason
                }
            }
        }

        public override void GameContextChanged(Game oldContext, Game newContext)
        {
            base.GameContextChanged(oldContext, newContext);
            _gameContext = newContext;
            _lastGame = newContext;
            _logic.GameNameProvider = () => _gameContext?.Name;
            _ = _logic.RefreshAsync();
        }

        private void UpdateStatusMessage()
        {
            try
            {
                var visibleCount = 0;
                if (_logic.EntriesView != null)
                {
                    visibleCount = _logic.EntriesView.Cast<object>().Count();
                }

                _logic.StatusMessage = visibleCount > 0
                    ? string.Format(ResourceProvider.GetString("LOCFriendsAchFeed_Status_FriendsActivity_Count"), visibleCount)
                    : ResourceProvider.GetString("LOCFriendsAchFeed_Status_NoFriendAchievementsForGame");
            }
            catch
            {
                // ignore
            }
        }

        // Called by external callers (like plugin when creating the Game view)
        // to apply the layout that used to live in GameFeedGameView.xaml.
        public void ApplyGameViewLayout()
        {
            try
            {
                try
                {
                    var h = _pluginSettings?.GameFeedTabHeight ?? 1000;
                    MainControl.Height = h > 0 ? h : 1000;
                }
                catch
                {
                    MainControl.Height = 1000;
                }

                MainControl.Margin = new Thickness(0, 8, 0, 0);
            }
            catch
            {
                // ignore
            }
        }
    }
}
