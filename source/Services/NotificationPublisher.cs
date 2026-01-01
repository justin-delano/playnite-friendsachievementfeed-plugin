using System;
using Playnite.SDK;
using Playnite.SDK.Models;
using FriendsAchievementFeed.Models;

namespace FriendsAchievementFeed.Services
{
    public class NotificationPublisher
    {
        private readonly IPlayniteAPI _api;
        private readonly FriendsAchievementFeedSettings _settings;
        private readonly ILogger _logger;

        public NotificationPublisher(IPlayniteAPI api, FriendsAchievementFeedSettings settings, ILogger logger)
        {
            _api = api;
            _settings = settings;
            _logger = logger;
        }

        public void ShowPeriodicStatus(string status)
        {
            if (_settings?.EnableNotifications != true || _settings.NotifyPeriodicUpdates == false)
            {
                return;
            }

            var messageTitle = ResourceProvider.GetString("LOCFriendsAchFeed_Title_PluginName");
            var statusText = string.IsNullOrWhiteSpace(status)
                ? ResourceProvider.GetString("LOCFriendsAchFeed_Rebuild_Completed")
                : status;

            var message = $"{messageTitle}\n{statusText}";

            try
            {
                _api.Notifications.Add(new NotificationMessage(
                    $"FriendsAchievementFeed-Periodic-{Guid.NewGuid()}",
                    message,
                    NotificationType.Info));
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Failed to show periodic notification.");
            }
        }
    }
}
