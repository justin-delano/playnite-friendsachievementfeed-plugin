using System;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FriendsAchievementFeed.Models;
using FriendsAchievementFeed.Views;
using FriendsAchievementFeed.Views.Helpers;
using Playnite.SDK;
using Playnite.SDK.Controls;

namespace FriendsAchievementFeed.Views.Shared
{
    public partial class FeedViewControl : PluginUserControl
    {
        public FeedViewControl()
        {
            InitializeComponent();
        }

        // Optional: only enable this from the owner that created the VM when confident
        // the view will not be re-used after unload.
        public static readonly DependencyProperty DisposeLogicOnUnloadProperty =
            DependencyProperty.Register(nameof(DisposeLogicOnUnload), typeof(bool), typeof(FeedViewControl), new PropertyMetadata(false));

        public bool DisposeLogicOnUnload
        {
            get => (bool)GetValue(DisposeLogicOnUnloadProperty);
            set => SetValue(DisposeLogicOnUnloadProperty, value);
        }

        // NEW: filler slot for per-game refresh injection
        public static readonly DependencyProperty ExtraRebuildContentProperty =
            DependencyProperty.Register(
                nameof(ExtraRebuildContent),
                typeof(object),
                typeof(FeedViewControl),
                new PropertyMetadata(null));

        public object ExtraRebuildContent
        {
            get => GetValue(ExtraRebuildContentProperty);
            set => SetValue(ExtraRebuildContentProperty, value);
        }

        private void FriendClearButton_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is FeedControlLogic vm)
            {
                vm.FriendSearchText = string.Empty;
            }
        }

        private void AchievementClearButton_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is FeedControlLogic vm)
            {
                vm.AchievementSearchText = string.Empty;
            }
        }

        private static ListBoxItem FindParentListBoxItem(DependencyObject obj)
        {
            while (obj != null)
            {
                if (obj is ListBoxItem lbi)
                {
                    return lbi;
                }

                obj = VisualTreeHelper.GetParent(obj);
            }

            return null;
        }

        private static void ClearParentListBoxSelection(FrameworkElement fe)
        {
            if (fe == null) return;

            var container = fe as ListBoxItem ?? FindParentListBoxItem(fe);
            if (container == null) return;

            if (ItemsControl.ItemsControlFromItemContainer(container) is ListBox parentList)
            {
                parentList.SelectedItem = null;
                parentList.SelectedIndex = -1;
            }
        }

        private void AchievementItem_Click(object sender, MouseButtonEventArgs e)
        {
            try
            {
                if (e.ChangedButton != MouseButton.Left)
                    return;

                if (sender is FrameworkElement fe && fe.DataContext is FeedEntry entry)
                {
                    if (entry.CanReveal)
                    {
                        Logic?.ToggleReveal(entry);
                    }

                    ClearParentListBoxSelection(fe);
                    e.Handled = true;
                }
            }
            catch
            {
                // swallow – don't break UI on click
            }
        }

        private void FriendName_Click(object sender, MouseButtonEventArgs e)
        {
            try
            {
                if (e.ChangedButton != MouseButton.Left) return;

                if (sender is FrameworkElement fe)
                {
                    var steamId = fe.DataContext is FeedEntry entry
                        ? entry.FriendSteamId
                        : (fe.DataContext is FeedGroup group ? group.FriendSteamId : null);

                    if (!string.IsNullOrWhiteSpace(steamId))
                        GameNavigationHelper.NavigateToFriendProfile(steamId);

                    ClearParentListBoxSelection(fe);
                    e.Handled = true;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"FriendName_Click handler error: {ex.Message}");
            }
        }

        private void GameName_Click(object sender, MouseButtonEventArgs e)
        {
            try
            {
                if (e.ChangedButton != MouseButton.Left) return;

                if (sender is FrameworkElement fe)
                {
                    if (fe.DataContext is FeedEntry entry)
                        GameNavigationHelper.NavigateToGame(entry);
                    else if (fe.DataContext is FeedGroup group)
                        GameNavigationHelper.NavigateToGame(group);

                    ClearParentListBoxSelection(fe);
                    e.Handled = true;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"GameName_Click handler error: {ex.Message}");
            }
        }



        public static readonly DependencyProperty ExtraFiltersContentProperty =
            DependencyProperty.Register(nameof(ExtraFiltersContent), typeof(object), typeof(FeedViewControl));

        public object ExtraFiltersContent
        {
            get => GetValue(ExtraFiltersContentProperty);
            set => SetValue(ExtraFiltersContentProperty, value);
        }

        public static readonly DependencyProperty ShowGameNameProperty =
            DependencyProperty.Register(nameof(ShowGameName), typeof(bool), typeof(FeedViewControl), new PropertyMetadata(false));

        public bool ShowGameName
        {
            get => (bool)GetValue(ShowGameNameProperty);
            set => SetValue(ShowGameNameProperty, value);
        }

        private FeedControlLogic _logic;
        public FeedControlLogic Logic
        {
            get => _logic;
            set
            {
                _logic = value;
                DataContext = _logic;
            }
        }

        protected override void OnInitialized(EventArgs e)
        {
            base.OnInitialized(e);
            Loaded += FeedViewControl_Loaded;
            Unloaded += FeedViewControl_Unloaded;
        }

        private async void FeedViewControl_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                if (Logic != null)
                {
                    await Logic.RefreshAsync();
                }
            }
            catch
            {
                // ignore
            }
        }

        private void FeedViewControl_Unloaded(object sender, RoutedEventArgs e)
        {
            try
            {
                // IMPORTANT: do NOT dispose injected VMs unless explicitly requested
                if (DisposeLogicOnUnload)
                {
                    Logic?.Dispose();
                }
            }
            catch
            {
                // ignore cleanup failures
            }
        }
    }

}
