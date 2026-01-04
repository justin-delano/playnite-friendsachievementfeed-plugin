using System;
using System.Threading;
using System.Threading.Tasks;
using Common;
using FriendsAchievementFeed.Services;
using Playnite.SDK;

namespace FriendsAchievementFeed.Views.Helpers
{
    internal sealed class SteamAuthUiHelper
    {
        private readonly SteamClient _steam;
        private readonly ILogger _logger;

        public SteamAuthUiHelper(SteamClient steam, ILogger logger)
        {
            _steam = steam ?? throw new ArgumentNullException(nameof(steam));
            _logger = logger;
        }

        public async Task<(bool Success, string Message)> AuthenticateInteractiveAsync(CancellationToken token)
        {
            try
            {
                var (ok, msg) = await _steam.AuthenticateInteractiveAsync(token).ConfigureAwait(false);
                return (ok, SteamStatusHelper.AuthMessage(msg));
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "[FAF] Steam Authenticate failed.");
                return (false, SteamStatusHelper.AuthFailed(ex.Message));
            }
        }

        public async Task<string> CheckAuthAsync(bool diskOnly, CancellationToken token)
        {
            try
            {
                await _steam.RefreshCookiesHeadlessAsync(token).ConfigureAwait(false);

                var self = await _steam.GetSelfSteamId64Async(token).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(self))
                    return SteamStatusHelper.NotAuthenticated();

                if (diskOnly)
                    return SteamStatusHelper.SessionFound(self);

                var page = await _steam.GetProfilePageAsync(self, token).ConfigureAwait(false);

                if ((int)page.StatusCode == 429)
                    return SteamStatusHelper.AuthMessage("Rate-limited (429). Try again later.");

                var html = page?.Html ?? "";
                if (string.IsNullOrWhiteSpace(html))
                    return SteamStatusHelper.AuthMessage("No profile returned (network issue?).");

                if (SteamClient.LooksLoggedOutHeader(html))
                    return SteamStatusHelper.AuthMessage("Saved cookies appear logged out. Click Authenticate.");

                return SteamStatusHelper.AuthMessage($"Auth OK (SteamID {self}).");
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "[FAF] Steam auth check failed.");
                return SteamStatusHelper.AuthFailed(ex.Message);
            }
        }

        public string ClearSavedCookies()
        {
            try
            {
                _steam.ClearSavedCookies();
                return SteamStatusHelper.Cleared();
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "[FAF] Steam ClearSavedCookies failed.");
                return SteamStatusHelper.ClearFailed(ex.Message);
            }
        }
    }
}
