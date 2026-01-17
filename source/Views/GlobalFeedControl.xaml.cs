using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using FriendsAchievementFeed.Models;
using FriendsAchievementFeed.Services;
using Playnite.SDK;

namespace FriendsAchievementFeed.Views
{
    public partial class GlobalFeedControl : Playnite.SDK.Controls.PluginUserControl
    {
        private readonly FeedControlLogic _logic;

        public GlobalFeedControl(
            IPlayniteAPI api,
            FriendsAchievementFeedSettings settings,
            ILogger logger,
            FeedManager feedService,
            FeedControlLogic logic)
        {
            _logic = logic;
            InitializeComponent();
            MainControl.Logic = _logic;
            MainControl.DisposeLogicOnUnload = false;
            DataContext = _logic;
            Loaded += GlobalFeedControl_Loaded;
        }

        private void GlobalFeedControl_Loaded(object sender, RoutedEventArgs e)
        {
            if (_logic == null) return;

            // When switching to global view, ensure game context is cleared
            _logic.GameNameProvider = null;
            _logic.GameIdProvider = null;
            _logic.NotifyCommandsChanged();
            _logic.SetViewContext(isGameView: false);

            _logic.ClearFeed();

            // Refresh to show global feed
            _ = _logic.RefreshAsync();
        }

        private void GameClearButton_Click(object sender, RoutedEventArgs e)
        {
            var vm = MainControl?.Logic;
            if (vm != null)
            {
                vm.GameSearchText = string.Empty;
            }
        }
    }
}
