using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Playnite.SDK.Controls;
using System.Diagnostics;
using Playnite.SDK;
using System.Linq;
using System.Windows.Media;
using System.Windows.Input;
using FriendsAchievementFeed.Models;

namespace FriendsAchievementFeed.Views.Shared
{
    public partial class FeedViewControl : PluginUserControl
    {
        public FeedViewControl()
        {
            InitializeComponent();
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
        private static void ClearParentListBoxSelection(FrameworkElement fe)
        {
            // Resolve the owning ListBoxItem
            var container = fe as ListBoxItem ?? ItemsControl.ContainerFromElement(null, fe) as ListBoxItem;
            if (container == null)
            {
                return;
            }

            // Find the parent ListBox
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
                {
                    return;
                }

                if (sender is FrameworkElement fe &&
                    fe.DataContext is FriendsAchievementFeed.Models.FeedEntry entry)
                {
                    var currentIcon = entry.AchievementIconUrl ?? string.Empty;
                    var unlockedIcon = entry.AchievementIconUnlockedUrl ?? string.Empty;

                    var isLockedVisual = !string.Equals(
                        currentIcon,
                        unlockedIcon,
                        StringComparison.OrdinalIgnoreCase);

                    var isHiddenDescription = entry.HideDescription;

                    // Nothing to reveal — don't toggle.
                    if (!isLockedVisual && !isHiddenDescription)
                    {
                        return;
                    }

                    Logic?.ToggleReveal(entry);

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
                if (e.ChangedButton != MouseButton.Left)
                {
                    return;
                }

                if (sender is FrameworkElement fe)
                {
                    // Data context may be a FeedEntry (inner row) or a FeedGroup (header)
                    string steamId = null;
                    if (fe.DataContext is FeedEntry entry)
                    {
                        steamId = entry.FriendSteamId;
                    }
                    else if (fe.DataContext is FeedGroup group)
                    {
                        steamId = group.FriendSteamId;
                    }

                    if (!string.IsNullOrWhiteSpace(steamId))
                    {
                        var url = $"https://steamcommunity.com/profiles/{steamId}";
                        OpenUrlInWebViewOrDefault(url);
                    }

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
                if (e.ChangedButton != MouseButton.Left)
                {
                    return;
                }

                if (sender is FrameworkElement fe)
                {
                    // Data context may be a FeedEntry (header in per-entry views) or a FeedGroup (group header)
                    FeedEntry sample = null;

                    if (fe.DataContext is FeedEntry entry)
                    {
                        sample = entry;
                    }
                    else if (fe.DataContext is FeedGroup group)
                    {
                        sample = group.Achievements?.FirstOrDefault();
                    }

                    if (sample != null)
                    {
                        if (sample.PlayniteGameId.HasValue)
                        {
                            API.Instance.MainView.SelectGame(sample.PlayniteGameId.Value);
                            API.Instance.MainView.SwitchToLibraryView();
                        }
                        else if (sample.AppId > 0)
                        {
                            var url = $"https://store.steampowered.com/app/{sample.AppId}";
                            OpenUrlInWebViewOrDefault(url);
                        }
                        else if (fe.DataContext is FeedGroup grp && !string.IsNullOrWhiteSpace(grp.GameName))
                        {
                            // Try to find a game by name in Playnite database
                            var found = API.Instance.Database.Games.FirstOrDefault(g => string.Equals(g.Name, grp.GameName, StringComparison.OrdinalIgnoreCase));
                            if (found != null)
                            {
                                API.Instance.MainView.SelectGame(found.Id);
                                API.Instance.MainView.SwitchToLibraryView();
                            }
                        }
                    }

                    ClearParentListBoxSelection(fe);
                    e.Handled = true;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"GameName_Click handler error: {ex.Message}");
            }
        }

        private void OpenUrlInWebViewOrDefault(string url, int width = 900, int height = 700)
        {
            try
            {
                // Try to open in Playnite embedded webview
                var webViews = API.Instance.WebViews;
                if (webViews != null)
                {
                    using (var view = webViews.CreateView(width, height, Colors.Black))
                    {
                        view.Navigate(url);
                        view.OpenDialog();
                    }
                    return;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"OpenUrlInWebView failed: {ex.Message}");
            }

            try
            {
                // Fallback to default browser
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Fallback browser launch failed: {ex.Message}");
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
            catch (Exception ex)
            {
                if (Logic != null)
                {
                    Logic.StatusMessage = ResourceProvider.GetString("LOCFriendsAchFeed_Error_FailedLoadFeed");
                    Logic.Logger?.Error(ex, "Failed to refresh feed on load");
                }
            }
        }

        private void FeedViewControl_Unloaded(object sender, RoutedEventArgs e)
        {
            try
            {
                Logic?.Dispose();
            }
            catch (Exception)
            {
                // ignore cleanup failures
            }
        }
    }
}