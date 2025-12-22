using System;
using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using FriendsAchievementFeed.Services;
using FriendsAchievementFeed.Models;
using Common;
using Playnite.SDK;

namespace FriendsAchievementFeed.Views
{
    public partial class SettingsControl : UserControl
    {
        private readonly FriendsAchievementFeedPlugin _plugin;

        public SettingsControl(FriendsAchievementFeedPlugin plugin)
        {
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
            InitializeComponent();

            // Ensure settings are the DataContext so bindings work
            this.DataContext = _plugin.Settings;
        }

        // Event handlers for new UI actions
        private void WipeCache_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _plugin.FeedService.Cache.ClearCache();
                _plugin.PlayniteApi.Notifications.Add(new NotificationMessage(
                    $"FriendsAchievementFeed-Wipe-{Guid.NewGuid()}",
                    ResourceProvider.GetString("LOCFriendsAchFeed_Settings_Cache_Wiped") ?? "Cache wiped.",
                    NotificationType.Info));
            }
            catch (Exception ex)
            {
                MessageBox.Show("Failed to wipe cache: " + ex.Message);
            }
        }

        // Per-game cache removal removed — single global wipe is supported.

        private async void CheckSteamAuth_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                SteamAuthStatusText.Text = ResourceProvider.GetString("LOCFriendsAchFeed_Settings_SteamAuth_Status_Checking") ?? "Checking...";
                var result = await _plugin.FeedService.TestSteamAuthAsync();

                if (result.Success)
                {
                    SteamAuthStatusText.Text = ResourceProvider.GetString("LOCFriendsAchFeed_Settings_SteamAuth_Status_OK") ?? "Steam auth OK";
                }
                else
                {
                    SteamAuthStatusText.Text = result.Message ?? (ResourceProvider.GetString("LOCFriendsAchFeed_Settings_SteamAuth_Status_Failed") ?? "Steam auth failed");
                }
            }
            catch (Exception ex)
            {
                SteamAuthStatusText.Text = "Error checking Steam auth: " + ex.Message;
            }
        }

        private void ReauthSteam_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Open Steam Community login page in default browser to allow user to sign in.
                var psi = new ProcessStartInfo
                {
                    FileName = "https://steamcommunity.com/login/home",
                    UseShellExecute = true
                };
                Process.Start(psi);
                SteamAuthStatusText.Text = ResourceProvider.GetString("LOCFriendsAchFeed_Settings_SteamAuth_Status_ReauthStarted") ?? "Opened Steam login in browser.";
            }
            catch (Exception ex)
            {
                MessageBox.Show("Failed to open Steam login: " + ex.Message);
            }
        }
    }
}

