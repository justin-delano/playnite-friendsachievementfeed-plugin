// SettingsControl.xaml.cs
using System;
using System.Linq;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Threading;
using System.Threading.Tasks;
using FriendsAchievementFeed.Services;
using FriendsAchievementFeed.Models;
using Common;
using Playnite.SDK;

namespace FriendsAchievementFeed.Views
{
    public partial class SettingsControl : UserControl
    {
        // -----------------------------
        // Option A (safe): UserControl DependencyProperties for auth UI
        // -----------------------------

        public static readonly DependencyProperty SteamAuthStatusProperty =
            DependencyProperty.Register(
                nameof(SteamAuthStatus),
                typeof(string),
                typeof(SettingsControl),
                new PropertyMetadata("Steam: Not checked"));

        public string SteamAuthStatus
        {
            get => (string)GetValue(SteamAuthStatusProperty);
            set => SetValue(SteamAuthStatusProperty, value);
        }

        public static readonly DependencyProperty SteamAuthBusyProperty =
            DependencyProperty.Register(
                nameof(SteamAuthBusy),
                typeof(bool),
                typeof(SettingsControl),
                new PropertyMetadata(false));

        public bool SteamAuthBusy
        {
            get => (bool)GetValue(SteamAuthBusyProperty);
            set => SetValue(SteamAuthBusyProperty, value);
        }

        private readonly FriendsAchievementFeedPlugin _plugin;
        private readonly SteamClient _steam;
        private List<SteamFriend> _friends = new List<SteamFriend>();
        private readonly ILogger _logger;

        public SettingsControl(FriendsAchievementFeedPlugin plugin, ILogger logger)
        {
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
            _logger = logger;

            InitializeComponent();

            // IMPORTANT: keep settings as DataContext so ALL your existing bindings still work
            DataContext = _plugin.Settings;

            _steam = new SteamClient(_plugin.PlayniteApi, _logger, _plugin.GetPluginUserDataPath());

            Loaded += async (s, e) =>
            {
                await CheckSteamAuthAsync(diskOnly: true).ConfigureAwait(true);
            };
        }

        // -----------------------------
        // Steam auth UI
        // -----------------------------

        private async void SteamAuth_Check_Click(object sender, RoutedEventArgs e)
        {
            await CheckSteamAuthAsync(diskOnly: false).ConfigureAwait(true);
        }

        private async void SteamAuth_Authenticate_Click(object sender, RoutedEventArgs e)
        {
            SetSteamAuthBusy(true);

            try
            {
                var (ok, msg) = await _steam.AuthenticateInteractiveAsync(CancellationToken.None).ConfigureAwait(true);
                SteamAuthStatus = $"Steam: {msg}";

                await CheckSteamAuthAsync(diskOnly: false).ConfigureAwait(true);
                await LoadFriendsAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "[FAF] Steam Authenticate failed.");
                SteamAuthStatus = $"Steam: Authenticate failed: {ex.Message}";
            }
            finally
            {
                SetSteamAuthBusy(false);
            }
        }

        private void SteamAuth_Clear_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _steam.ClearSavedCookies();
                SteamAuthStatus = "Steam: Cleared saved session. Click Authenticate.";
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "[FAF] Steam ClearSavedCookies failed.");
                SteamAuthStatus = $"Steam: Clear failed: {ex.Message}";
            }
        }

        private async Task CheckSteamAuthAsync(bool diskOnly)
        {
            SetSteamAuthBusy(true);

            try
            {
                await _steam.ReloadCookiesFromDiskAsync(CancellationToken.None).ConfigureAwait(true);

                var self = await _steam.GetSelfSteamId64Async(CancellationToken.None).ConfigureAwait(true);
                if (string.IsNullOrWhiteSpace(self))
                {
                    SteamAuthStatus = "Steam: Not authenticated (no saved session). Click Authenticate.";
                    return;
                }

                if (diskOnly)
                {
                    SteamAuthStatus = $"Steam: Session found (SteamID {self}).";
                    return;
                }

                var page = await _steam.GetProfilePageAsync(self, CancellationToken.None).ConfigureAwait(true);

                if ((int)page.StatusCode == 429)
                {
                    SteamAuthStatus = "Steam: Rate-limited (429). Try again later.";
                    return;
                }

                var html = page?.Html ?? "";
                if (string.IsNullOrWhiteSpace(html))
                {
                    SteamAuthStatus = "Steam: No profile returned (network issue?).";
                    return;
                }

                if (SteamClient.LooksLoggedOutHeader(html))
                {
                    SteamAuthStatus = "Steam: Saved cookies appear logged out. Click Authenticate.";
                    return;
                }

                SteamAuthStatus = $"Steam: Auth OK (SteamID {self}).";
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "[FAF] Steam auth check failed.");
                SteamAuthStatus = $"Steam: Auth check failed: {ex.Message}";
            }
            finally
            {
                SetSteamAuthBusy(false);
            }
        }

        private void SetSteamAuthBusy(bool busy)
        {
            SteamAuthBusy = busy;
        }

        // -----------------------------
        // Cache actions
        // -----------------------------

        private void WipeCache_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _plugin.FeedService.Cache.ClearCache();

                var stillPresent = _plugin.FeedService.Cache.CacheFileExists();
                if (!stillPresent)
                {
                    _plugin.PlayniteApi.Dialogs.ShowMessage(
                        ResourceProvider.GetString("LOCFriendsAchFeed_Settings_Cache_Wiped") ?? "Cache wiped.",
                        "Friends Achievement Feed",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
                else
                {
                    _plugin.PlayniteApi.Dialogs.ShowMessage(
                        ResourceProvider.GetString("LOCFriendsAchFeed_Settings_Cache_WipeFailed") ?? "Failed to wipe cache (files remain).",
                        "Friends Achievement Feed",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                _plugin.PlayniteApi.Dialogs.ShowMessage(
                    "Failed to wipe cache: " + ex.Message,
                    "Friends Achievement Feed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        // -----------------------------
        // Friends list (Family Sharing tab)
        // -----------------------------

        private async Task LoadFriendsAsync()
        {
            try
            {
                FriendsStatusText.Text = "Loading friends…";

                var self = await _steam.GetSelfSteamId64Async(CancellationToken.None).ConfigureAwait(true);
                if (string.IsNullOrWhiteSpace(self))
                {
                    FriendsStatusText.Text = "Authenticate with Steam (General tab) to load friends.";
                    return;
                }

                if (string.IsNullOrWhiteSpace(_plugin.Settings.SteamUserId))
                {
                    _plugin.Settings.SteamUserId = self;
                    _plugin.Settings.EndEdit();
                }

                var list = await _plugin.FeedService.GetFriendsAsync().ConfigureAwait(false);
                _friends = list ?? new List<SteamFriend>();

                Dispatcher.Invoke(() =>
                {
                    FriendCombo1.ItemsSource = _friends;
                    FriendCombo2.ItemsSource = _friends;
                    FriendCombo3.ItemsSource = _friends;
                    FriendCombo4.ItemsSource = _friends;
                    FriendCombo5.ItemsSource = _friends;

                    SelectComboBySteamId(FriendCombo1, _plugin.Settings.Friend1SteamId);
                    SelectComboBySteamId(FriendCombo2, _plugin.Settings.Friend2SteamId);
                    SelectComboBySteamId(FriendCombo3, _plugin.Settings.Friend3SteamId);
                    SelectComboBySteamId(FriendCombo4, _plugin.Settings.Friend4SteamId);
                    SelectComboBySteamId(FriendCombo5, _plugin.Settings.Friend5SteamId);

                    FriendsStatusText.Text = _friends.Count > 0
                        ? $"Loaded {_friends.Count} friends."
                        : "No friends found or profile private.";
                });
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => FriendsStatusText.Text = "Failed to load friends: " + ex.Message);
            }
        }

        private void SelectComboBySteamId(ComboBox cb, string steamId)
        {
            if (cb == null) return;
            if (string.IsNullOrWhiteSpace(steamId))
            {
                cb.SelectedItem = null;
                return;
            }

            var match = _friends.FirstOrDefault(f => string.Equals(f.SteamId, steamId, StringComparison.OrdinalIgnoreCase));
            if (match != null) cb.SelectedItem = match;
        }

        private async void TabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var tc = sender as TabControl;
            if (tc == null) return;

            // Inspect the newly selected TabItem by name (uses x:Name from XAML)
            if (e.AddedItems == null || e.AddedItems.Count == 0) return;
            if (e.AddedItems[0] is not TabItem selected) return;

            var name = selected.Name ?? string.Empty;
            if (string.Equals(name, "FamilySharingTab", StringComparison.OrdinalIgnoreCase))
            {
                await LoadFriendsAsync().ConfigureAwait(true);
                _logger?.Info("[FAF] Loaded friends list for Family Sharing tab.");
            }
            else if (string.Equals(name, "GeneralTab", StringComparison.OrdinalIgnoreCase))
            {
                await CheckSteamAuthAsync(diskOnly: true).ConfigureAwait(true);
                _logger?.Info("[FAF] Checked Steam auth for General tab.");
            }
        }

        private void FriendCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is not ComboBox cb) return;

            var sel = cb.SelectedItem as SteamFriend;

            if (cb.Name == "FriendCombo1")
            {
                _plugin.Settings.Friend1SteamId = sel?.SteamId ?? string.Empty;
                _plugin.Settings.Friend1Name = sel?.PersonaName ?? string.Empty;
            }
            else if (cb.Name == "FriendCombo2")
            {
                _plugin.Settings.Friend2SteamId = sel?.SteamId ?? string.Empty;
                _plugin.Settings.Friend2Name = sel?.PersonaName ?? string.Empty;
            }
            else if (cb.Name == "FriendCombo3")
            {
                _plugin.Settings.Friend3SteamId = sel?.SteamId ?? string.Empty;
                _plugin.Settings.Friend3Name = sel?.PersonaName ?? string.Empty;
            }
            else if (cb.Name == "FriendCombo4")
            {
                _plugin.Settings.Friend4SteamId = sel?.SteamId ?? string.Empty;
                _plugin.Settings.Friend4Name = sel?.PersonaName ?? string.Empty;
            }
            else if (cb.Name == "FriendCombo5")
            {
                _plugin.Settings.Friend5SteamId = sel?.SteamId ?? string.Empty;
                _plugin.Settings.Friend5Name = sel?.PersonaName ?? string.Empty;
            }

            _plugin.Settings.EndEdit();
        }

        private void FamilyScan_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var opts = new CacheRebuildOptions();
                opts.FamilySharingFriendIDs = new List<string>();
                if (!string.IsNullOrWhiteSpace(_plugin.Settings.Friend1SteamId)) opts.FamilySharingFriendIDs.Add(_plugin.Settings.Friend1SteamId);
                if (!string.IsNullOrWhiteSpace(_plugin.Settings.Friend2SteamId)) opts.FamilySharingFriendIDs.Add(_plugin.Settings.Friend2SteamId);
                if (!string.IsNullOrWhiteSpace(_plugin.Settings.Friend3SteamId)) opts.FamilySharingFriendIDs.Add(_plugin.Settings.Friend3SteamId);
                if (!string.IsNullOrWhiteSpace(_plugin.Settings.Friend4SteamId)) opts.FamilySharingFriendIDs.Add(_plugin.Settings.Friend4SteamId);
                if (!string.IsNullOrWhiteSpace(_plugin.Settings.Friend5SteamId)) opts.FamilySharingFriendIDs.Add(_plugin.Settings.Friend5SteamId);

                _plugin.PlayniteApi.Dialogs.ActivateGlobalProgress(async a =>
                {
                    a.Text = "Starting family-sharing scan…";
                    a.IsIndeterminate = true;

                    using var cancelReg = a.CancelToken.Register(() => _plugin.FeedService.CancelActiveRebuild());

                    EventHandler<ProgressReport> handler = (s, r) =>
                    {
                        if (r == null) return;
                        a.Text = r.Message ?? a.Text;
                        if (r.TotalSteps > 0)
                        {
                            a.IsIndeterminate = false;
                            a.ProgressMaxValue = r.TotalSteps;
                            a.CurrentProgressValue = r.CurrentStep;
                        }
                        else
                        {
                            a.IsIndeterminate = true;
                        }
                    };

                    _plugin.FeedService.RebuildProgress += handler;
                    try
                    {
                        await _plugin.FeedService.StartManagedRebuildAsync(opts).ConfigureAwait(false);
                    }
                    finally
                    {
                        _plugin.FeedService.RebuildProgress -= handler;
                    }
                }, new GlobalProgressOptions("Friends Achievement Feed — Family Sharing Scan") { IsIndeterminate = true, Cancelable = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show("Failed to start family-sharing scan: " + ex.Message);
            }
        }

        private void ClearFriend1_Click(object sender, RoutedEventArgs e)
        {
            _plugin.Settings.Friend1SteamId = string.Empty;
            _plugin.Settings.Friend1Name = string.Empty;
            _plugin.Settings.EndEdit();
            FriendCombo1.SelectedItem = null;
            FriendCombo1.Text = string.Empty;
        }

        private void ClearFriend2_Click(object sender, RoutedEventArgs e)
        {
            _plugin.Settings.Friend2SteamId = string.Empty;
            _plugin.Settings.Friend2Name = string.Empty;
            _plugin.Settings.EndEdit();
            FriendCombo2.SelectedItem = null;
            FriendCombo2.Text = string.Empty;
        }

        private void ClearFriend3_Click(object sender, RoutedEventArgs e)
        {
            _plugin.Settings.Friend3SteamId = string.Empty;
            _plugin.Settings.Friend3Name = string.Empty;
            _plugin.Settings.EndEdit();
            FriendCombo3.SelectedItem = null;
            FriendCombo3.Text = string.Empty;
        }

        private void ClearFriend4_Click(object sender, RoutedEventArgs e)
        {
            _plugin.Settings.Friend4SteamId = string.Empty;
            _plugin.Settings.Friend4Name = string.Empty;
            _plugin.Settings.EndEdit();
            FriendCombo4.SelectedItem = null;
            FriendCombo4.Text = string.Empty;
        }

        private void ClearFriend5_Click(object sender, RoutedEventArgs e)
        {
            _plugin.Settings.Friend5SteamId = string.Empty;
            _plugin.Settings.Friend5Name = string.Empty;
            _plugin.Settings.EndEdit();
            FriendCombo5.SelectedItem = null;
            FriendCombo5.Text = string.Empty;
        }
    }
}
