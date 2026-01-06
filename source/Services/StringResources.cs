using Playnite.SDK;

namespace FriendsAchievementFeed.Services
{
    /// <summary>
    /// Centralized resource string access with fallback support
    /// </summary>
    internal static class StringResources
    {
        public static string GetString(string key, string fallback = null)
        {
            return Playnite.SDK.ResourceProvider.GetString(key) ?? fallback ?? key;
        }

        public static string GetStringOrEmpty(string key)
        {
            return Playnite.SDK.ResourceProvider.GetString(key) ?? string.Empty;
        }

        // Common error messages
        public static string ErrorFileOperationFailed => 
            GetString("LOCFriendsAchFeed_Error_FileOperationFailed", "File operation failed");

        public static string ErrorNotifySubscribers => 
            GetString("LOCFriendsAchFeed_Error_NotifySubscribers", "Failed to notify subscribers");

        public static string ErrorSteamNotConfigured => 
            GetString("LOCFriendsAchFeed_Error_SteamNotConfigured", "Steam not configured");

        public static string ErrorFailedRebuild => 
            GetString("LOCFriendsAchFeed_Error_FailedRebuild", "Cache rebuild failed");

        public static string RebuildCanceled => 
            GetString("LOCFriendsAchFeed_Rebuild_Canceled", "Cache rebuild canceled");

        public static string RebuildCompleted => 
            GetString("LOCFriendsAchFeed_Rebuild_Completed", "Cache rebuild completed");

        public static string PluginName => 
            GetString("LOCFriendsAchFeed_Title_PluginName", "Friends Achievement Feed");

        public static string DebugRebuildCanceled => 
            GetString("LOCFriendsAchFeed_Debug_RebuildCanceledByUser", "Rebuild canceled by user");
    }
}