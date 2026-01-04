using System;
using System.Diagnostics;
using System.Linq;
using System.Windows.Media;
using FriendsAchievementFeed.Models;
using Playnite.SDK;

namespace FriendsAchievementFeed.Views.Helpers
{
    internal static class GameNavigationHelper
    {
        public static void NavigateToFriendProfile(string steamId)
        {
            if (string.IsNullOrWhiteSpace(steamId)) return;
            
            var url = $"https://steamcommunity.com/profiles/{steamId}";
            OpenUrl(url);
        }

        public static void NavigateToGame(FeedEntry entry)
        {
            if (entry == null) return;

            if (entry.PlayniteGameId.HasValue)
            {
                API.Instance.MainView.SelectGame(entry.PlayniteGameId.Value);
                API.Instance.MainView.SwitchToLibraryView();
            }
            else if (entry.AppId > 0)
            {
                var url = $"https://store.steampowered.com/app/{entry.AppId}";
                OpenUrl(url);
            }
        }

        public static void NavigateToGame(FeedGroup group)
        {
            if (group == null) return;

            var sample = group.Achievements?.FirstOrDefault();
            if (sample != null)
            {
                NavigateToGame(sample);
                return;
            }

            if (!string.IsNullOrWhiteSpace(group.GameName))
            {
                var found = API.Instance.Database.Games
                    .FirstOrDefault(g => string.Equals(g.Name, group.GameName, StringComparison.OrdinalIgnoreCase));

                if (found != null)
                {
                    API.Instance.MainView.SelectGame(found.Id);
                    API.Instance.MainView.SwitchToLibraryView();
                }
            }
        }

        public static void OpenUrl(string url, int width = 900, int height = 700)
        {
            if (string.IsNullOrWhiteSpace(url)) return;

            try
            {
                if (API.Instance.WebViews != null)
                {
                    using (var view = API.Instance.WebViews.CreateView(width, height, Colors.Black))
                    {
                        view.Navigate(url);
                        view.OpenDialog();
                    }
                    return;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"WebView navigation failed: {ex.Message}");
            }

            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Browser launch failed: {ex.Message}");
            }
        }
    }
}
