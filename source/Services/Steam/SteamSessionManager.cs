using FriendsAchievementFeed.Services.Steam.Models;
using Playnite.SDK;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FriendsAchievementFeed.Services
{
    /// <summary>
    /// Manages Steam authentication session state, including Steam ID persistence and cookie validation.
    /// Extracted from SteamClient for better separation of concerns.
    /// </summary>
    internal sealed class SteamSessionManager
    {
        private static readonly TimeSpan SessionCheckInterval = TimeSpan.FromMinutes(30);
        private static readonly TimeSpan CookieRefreshInterval = TimeSpan.FromHours(6);

        private readonly IPlayniteAPI _api;
        private readonly ILogger _logger;
        private readonly SteamSessionStore _sessionStore;

        private string _selfSteamId64;
        private DateTime _lastSessionCheckUtc = DateTime.MinValue;
        private DateTime _lastCookieRefreshUtc = DateTime.MinValue;

        public SteamSessionManager(IPlayniteAPI api, ILogger logger, string pluginUserDataPath)
        {
            _api = api ?? throw new ArgumentNullException(nameof(api));
            _logger = logger;

            if (string.IsNullOrWhiteSpace(pluginUserDataPath))
                throw new ArgumentNullException(nameof(pluginUserDataPath));

            _sessionStore = new SteamSessionStore(pluginUserDataPath);
            LoadSessionData();
        }

        public string GetCachedSteamId64() => _selfSteamId64;

        public bool NeedsRefresh => 
            string.IsNullOrWhiteSpace(_selfSteamId64) || 
            (DateTime.UtcNow - _lastCookieRefreshUtc) >= CookieRefreshInterval;

        private void LoadSessionData()
        {
            if (_sessionStore.TryLoad(out var session))
            {
                _selfSteamId64 = session.SelfSteamId64;
                _lastSessionCheckUtc = session.LastValidatedUtc;
            }
        }

        private void SaveSessionData()
        {
            if (string.IsNullOrWhiteSpace(_selfSteamId64))
                return;

            _sessionStore.Save(new SteamSessionData
            {
                SelfSteamId64 = _selfSteamId64,
                LastValidatedUtc = DateTime.UtcNow
            });
        }

        public bool HasSteamSessionCookies()
        {
            return SteamCookieManager.HasSteamSessionCookies(_api, _logger);
        }

        public async Task<string> GetSelfSteamId64Async(CancellationToken ct)
        {
            // Return cached if recent
            if (!string.IsNullOrWhiteSpace(_selfSteamId64) &&
                (DateTime.UtcNow - _lastSessionCheckUtc) < SessionCheckInterval)
            {
                return _selfSteamId64;
            }

            // Try to refresh from cookies
            if (!HasSteamSessionCookies())
            {
                _selfSteamId64 = null;
                return null;
            }

            // Extract Steam ID from CEF cookies
            try
            {
                using (var view = _api.WebViews.CreateOffscreenView())
                {
                    var cookies = view.GetCookies();
                    if (cookies != null)
                    {
                        var steamLogin = cookies.FirstOrDefault(c =>
                            c != null &&
                            string.Equals(c.Name, "steamLoginSecure", StringComparison.OrdinalIgnoreCase));

                        if (steamLogin != null)
                        {
                            var id = SteamCookieManager.TryExtractSteamId64FromSteamLoginSecure(steamLogin.Value);
                            if (!string.IsNullOrWhiteSpace(id))
                            {
                                _selfSteamId64 = id;
                                _lastSessionCheckUtc = DateTime.UtcNow;
                                SaveSessionData();
                                return id;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "[FAF] Failed to extract Steam ID from cookies.");
            }

            // Try refreshing headlessly if we don't have ID
            if (string.IsNullOrWhiteSpace(_selfSteamId64))
            {
                await RefreshCookiesHeadlessAsync(ct).ConfigureAwait(false);
            }

            return _selfSteamId64;
        }

        public async Task<bool> RefreshCookiesHeadlessAsync(CancellationToken ct)
        {
            try
            {
                ct.ThrowIfCancellationRequested();

                bool success = false;
                string extractedId = null;

                await _api.MainView.UIDispatcher.InvokeAsync(async () =>
                {
                    try
                    {
                        using (var view = _api.WebViews.CreateOffscreenView())
                        {
                            view.Navigate("https://store.steampowered.com/explore/");
                            await Task.Delay(2000, ct);

                            var cookies = view.GetCookies();
                            if (cookies != null)
                            {
                                var steamLogin = cookies.FirstOrDefault(c =>
                                    c != null &&
                                    string.Equals(c.Name, "steamLoginSecure", StringComparison.OrdinalIgnoreCase));

                                if (steamLogin != null)
                                {
                                    extractedId = SteamCookieManager.TryExtractSteamId64FromSteamLoginSecure(steamLogin.Value);
                                    success = true;
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.Debug(ex, "[FAF] CreateOffscreenView failed - may not be available in this Playnite version.");
                    }
                });

                if (success && !string.IsNullOrWhiteSpace(extractedId))
                {
                    _selfSteamId64 = extractedId;
                    _lastCookieRefreshUtc = DateTime.UtcNow;
                    _lastSessionCheckUtc = DateTime.UtcNow;
                    SaveSessionData();
                }

                return success;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "[FAF] Headless cookie refresh failed.");
                return false;
            }
        }

        public async Task<(bool Success, string Message)> AuthenticateInteractiveAsync(CancellationToken ct)
        {
            try
            {
                ct.ThrowIfCancellationRequested();

                bool loggedIn = false;
                string extractedId = null;

                await _api.MainView.UIDispatcher.InvokeAsync(async () =>
                {
                    using (var view = _api.WebViews.CreateView(1000, 800))
                    {
                        view.DeleteDomainCookies(".steamcommunity.com");
                        view.DeleteDomainCookies("steamcommunity.com");
                        view.DeleteDomainCookies(".steampowered.com");
                        view.DeleteDomainCookies("steampowered.com");

                        view.Navigate("https://steamcommunity.com/login/home/?goto=" +
                                      Uri.EscapeDataString("https://steamcommunity.com/my/"));

                        view.OpenDialog();
                        await Task.Delay(500);

                        var cookies = view.GetCookies();
                        if (cookies != null)
                        {
                            var steamCookies = cookies
                                .Where(c => c != null && !string.IsNullOrWhiteSpace(c.Domain))
                                .Where(c => IsSteamDomain(c.Domain))
                                .ToList();

                            if (steamCookies.Count > 0)
                            {
                                var steamLogin = steamCookies.FirstOrDefault(c =>
                                    string.Equals(c.Name, "steamLoginSecure", StringComparison.OrdinalIgnoreCase));

                                if (steamLogin != null)
                                {
                                    extractedId = SteamCookieManager.TryExtractSteamId64FromSteamLoginSecure(steamLogin.Value);
                                    loggedIn = !string.IsNullOrWhiteSpace(extractedId);
                                }
                            }
                        }
                    }
                });

                if (!loggedIn)
                    return (false, "Steam session cookies not found. Please ensure login completed.");

                _selfSteamId64 = extractedId;
                _lastCookieRefreshUtc = DateTime.UtcNow;
                _lastSessionCheckUtc = DateTime.UtcNow;
                SaveSessionData();

                return (true, "Steam authentication saved.");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger?.Error(ex, "[FAF] AuthenticateInteractiveAsync failed.");
                return (false, ex.Message);
            }
        }

        public void ClearSession()
        {
            SteamCookieManager.ClearSteamCookiesFromCef(_api, _logger);
            _sessionStore.Clear();
            _selfSteamId64 = null;
            _lastSessionCheckUtc = DateTime.MinValue;
            _lastCookieRefreshUtc = DateTime.MinValue;
        }

        private static bool IsSteamDomain(string domain)
        {
            if (string.IsNullOrWhiteSpace(domain)) return false;
            var d = domain.Trim().TrimStart('.');
            return d.EndsWith("steamcommunity.com", StringComparison.OrdinalIgnoreCase) ||
                   d.EndsWith("steampowered.com", StringComparison.OrdinalIgnoreCase);
        }
    }
}
