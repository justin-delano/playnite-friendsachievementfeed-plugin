namespace Common
{
    public static class SteamStatusProvider
    {
        public static string NotAuthenticated() => "Steam: Not authenticated (no saved session). Click Authenticate.";
        public static string SessionFound(string steamId) => $"Steam: Session found (SteamID {steamId}).";
        public static string AuthMessage(string message) => $"Steam: {message}";
        public static string AuthFailed(string reason) => $"Steam: Authenticate failed: {reason}";
        public static string Cleared() => "Steam: Cleared saved session. Click Authenticate.";
        public static string ClearFailed(string reason) => $"Steam: Clear failed: {reason}";
    }
}
