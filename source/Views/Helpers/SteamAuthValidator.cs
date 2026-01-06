using System;
using System.Threading;
using System.Threading.Tasks;
using FriendsAchievementFeed.Models;
using FriendsAchievementFeed.Services;
using Common;
using Playnite.SDK;
using StringResources = FriendsAchievementFeed.Services.StringResources;

namespace FriendsAchievementFeed.Views.Helpers
{
    internal sealed class SteamAuthValidator
    {
        private readonly FriendsAchievementFeedSettings _settings;
        private readonly FeedManager _feedService;
        private CancellationTokenSource _authCheckCts;
        private bool? _authValid = null;
        private string _authMessage = null;

        public event EventHandler AuthStateChanged;

        public bool IsSteamKeyConfigured =>
            _settings != null &&
            !string.IsNullOrWhiteSpace(_settings.SteamUserId) &&
            !string.IsNullOrWhiteSpace(_settings.SteamApiKey);

        public bool IsSteamAuthValid => _authValid == true;
        public bool IsSteamReady => IsSteamKeyConfigured && IsSteamAuthValid;
        public string AuthMessage => _authMessage;

        public SteamAuthValidator(FriendsAchievementFeedSettings settings, FeedManager feedService)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _feedService = feedService ?? throw new ArgumentNullException(nameof(feedService));
        }

        public void QueueValidation()
        {
            _authCheckCts?.Cancel();
            _authCheckCts?.Dispose();
            _authCheckCts = new CancellationTokenSource();
            var token = _authCheckCts.Token;

            Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(250, token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();

                    if (!IsSteamKeyConfigured)
                    {
                        _authValid = false;
                        _authMessage = StringResources.GetString("LOCFriendsAchFeed_Error_SteamNotConfigured");
                        RaiseAuthStateChanged();
                        return;
                    }

                    _authValid = null;
                    _authMessage = StringResources.GetString("LOCFriendsAchFeed_Settings_CheckingSteamAuth") ?? "Checking Steam authentication...";
                    RaiseAuthStateChanged();

                    var result = await _feedService.TestSteamAuthAsync().ConfigureAwait(false);
                    _authValid = result.Success;
                    _authMessage = result.Message;
                    RaiseAuthStateChanged();
                }
                catch (OperationCanceledException) { }
                catch (Exception)
                {
                    _authValid = false;
                    _authMessage = StringResources.GetString("LOCFriendsAchFeed_Error_SteamNotConfigured") ?? "Steam not configured.";
                    RaiseAuthStateChanged();
                }
            }, token);
        }

        public async Task<bool> EnsureReadyAsync(bool showDialog, Action<string> showDialogAction, CancellationToken token)
        {
            if (!IsSteamKeyConfigured)
            {
                _authValid = false;
                _authMessage = StringResources.GetString("LOCFriendsAchFeed_Error_SteamNotConfigured")
                              ?? "Steam API key / user id not configured.";
                RaiseAuthStateChanged();

                if (showDialog) showDialogAction?.Invoke(_authMessage);
                return false;
            }

            if (_authValid == true)
                return true;

            var result = await _feedService.TestSteamAuthAsync().ConfigureAwait(false);
            _authValid = result.Success;
            _authMessage = result.Message;
            RaiseAuthStateChanged();

            if (!result.Success && showDialog)
            {
                showDialogAction?.Invoke(_authMessage ?? StringResources.GetString("LOCFriendsAchFeed_Settings_SteamAuth_WebAuthUnavailable"));
            }

            return result.Success;
        }

        public void Dispose()
        {
            _authCheckCts?.Cancel();
            _authCheckCts?.Dispose();
            _authCheckCts = null;
        }

        private void RaiseAuthStateChanged() => AuthStateChanged?.Invoke(this, EventArgs.Empty);
    }
}
