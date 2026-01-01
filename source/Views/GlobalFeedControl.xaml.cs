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
            AchievementFeedService feedService)
        {
            _logic = new FeedControlLogic(api, settings, logger, feedService);
            InitializeComponent();
            MainControl.Logic = _logic;
            MainControl.DisposeLogicOnUnload = true;
            DataContext = _logic;
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
