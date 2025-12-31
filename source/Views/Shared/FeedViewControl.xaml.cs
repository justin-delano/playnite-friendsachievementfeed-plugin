using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using FriendsAchievementFeed.Models;
using FriendsAchievementFeed.Views;          // <-- ADD THIS
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
                if (e.ChangedButton != MouseButton.Left)
                {
                    return;
                }

                if (sender is FrameworkElement fe)
                {
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
                            var found = API.Instance.Database.Games
                                .FirstOrDefault(g => string.Equals(g.Name, grp.GameName, StringComparison.OrdinalIgnoreCase));

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

    public class UtcToLocalConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null)
            {
                return null;
            }

            if (value is DateTime dt)
            {
                if (dt.Kind == DateTimeKind.Local)
                {
                    return dt;
                }

                if (dt.Kind == DateTimeKind.Utc)
                {
                    return dt.ToLocalTime();
                }

                return DateTime.SpecifyKind(dt, DateTimeKind.Utc).ToLocalTime();
            }

            return value;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value;
        }
    }
}
