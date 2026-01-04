using FriendsAchievementFeed.Services;
using FriendsAchievementFeed.Models;

namespace FriendsAchievementFeed.Views.Helpers
{
    internal static class StatusMessageResolver
    {
        public static string GetEffectiveMessage(object report, string fallbackStatus)
        {
            string message = null;
            
            if (report is ProgressReport progressReport)  
                message = progressReport.Message;

            if (!string.IsNullOrWhiteSpace(message))
                return message;
                
            return fallbackStatus ?? string.Empty;
        }
    }
}
