using System;
using System.Threading;
using System.Threading.Tasks;
using Common;
using FriendsAchievementFeed.Services;
using Playnite.SDK;

namespace FriendsAchievementFeed.Views.Helpers
{
    internal sealed class SteamAuthenticator
    {
        private readonly SteamClient _steam;
        private readonly ILogger _logger;

        public SteamAuthenticator(SteamClient steam, ILogger logger)
        {
            _steam = steam ?? throw new ArgumentNullException(nameof(steam));
            _logger = logger;
        }

        public async Task<(bool Success, string Message)> AuthenticateInteractiveAsync(CancellationToken token)
        {
            try
            {
                var (ok, msg) = await _steam.AuthenticateInteractiveAsync(token).ConfigureAwait(false);
                return (ok, SteamStatusProvider.AuthMessage(msg));
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "[FAF] Steam Authenticate failed.");
                return (false, SteamStatusProvider.AuthFailed(ex.Message));
            }
        }

        public async Task<string> CheckAuthAsync(CancellationToken token)
        {
            try
            {
                var self = await _steam.GetRequiredSelfSteamId64Async(token).ConfigureAwait(false);

                var probe = await _steam.ProbeLoggedInAsync(token).ConfigureAwait(false);

                // Optional debug:
                _logger?.Debug($"[FAF] Auth probe FinalUrl={probe.FinalUrl} LoggedIn={probe.IsLoggedIn}");

                // If your probe returns an "empty/no response" flag instead, use that here.
                if (string.IsNullOrWhiteSpace(probe.FinalUrl))
                    return SteamStatusProvider.AuthMessage("No response from Steam (network issue?).");

                if (!probe.IsLoggedIn)
                    return SteamStatusProvider.AuthMessage("Saved cookies appear logged out. Click Authenticate.");

                return SteamStatusProvider.AuthMessage($"Auth OK (SteamID {self}).");
            }
            catch (InvalidOperationException)
            {
                return SteamStatusProvider.NotAuthenticated();
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "[FAF] Steam auth check failed.");
                return SteamStatusProvider.AuthFailed(ex.Message);
            }
        }

        public string ClearSavedCookies()
        {
            try
            {
                _steam.ClearSavedCookies();
                return SteamStatusProvider.Cleared();
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "[FAF] Steam ClearSavedCookies failed.");
                return SteamStatusProvider.ClearFailed(ex.Message);
            }
        }
    }
}
