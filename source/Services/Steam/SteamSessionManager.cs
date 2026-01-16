using FriendsAchievementFeed.Services.Steam.Models;
using Playnite.SDK;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace FriendsAchievementFeed.Services
{
    internal sealed class SteamSessionManager
    {
        private static readonly TimeSpan SessionCheckInterval = TimeSpan.FromMinutes(30);
        // Refresh every 6 hours standard, or immediately if we don't have an ID
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

        public bool NeedsRefresh
        {
            get
            {
                // Always refresh if we don't have an ID
                if (string.IsNullOrWhiteSpace(_selfSteamId64))
                    return true;

                // Otherwise respect the interval
                return (DateTime.UtcNow - _lastCookieRefreshUtc) >= CookieRefreshInterval;
            }
        }

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
            // 1. Return memory cached ID if validated recently
            if (!string.IsNullOrWhiteSpace(_selfSteamId64) &&
                (DateTime.UtcNow - _lastSessionCheckUtc) < SessionCheckInterval)
            {
                return _selfSteamId64;
            }

            // 2. Refresh if interval elapsed or ID missing
            if (NeedsRefresh)
            {
                await RefreshCookiesHeadlessAsync(ct).ConfigureAwait(false);
            }

            // 3. Mark session checked if we have an ID now (regardless of where it came from)
            if (!string.IsNullOrWhiteSpace(_selfSteamId64))
            {
                _lastSessionCheckUtc = DateTime.UtcNow;
                SaveSessionData();
            }

            return _selfSteamId64;
        }

        public async Task<bool> RefreshCookiesHeadlessAsync(CancellationToken ct, bool force = false)
        {
            // If not forced, check if we need to refresh based on time
            if (!force && !NeedsRefresh)
            {
                _logger?.Debug("[FAF] Cookie refresh skipped - not needed yet.");
                return true;
            }

            // Prevent spam: Always update the timestamp, even if we fail.
            // This stops the "refresh loop" from happening every single second on failure.
            _lastCookieRefreshUtc = DateTime.UtcNow;

            bool success = false;
            string extractedId = null;

            // Use TaskCompletionSource to ensuring we wait for the UI thread logic to actually FINISH.
            var tcs = new TaskCompletionSource<bool>();

            await _api.MainView.UIDispatcher.InvokeAsync(async () =>
            {
                try
                {
                    using (var view = _api.WebViews.CreateOffscreenView())
                    {
                        // Mimic Playnite Steam Library: Check Community primarily.
                        // Navigating to root is safer than /my/profile to avoid redirects confusing the cookie jar.
                        var targetUrl = "https://steamcommunity.com/";
                        
                        _logger?.Debug($"[FAF] Navigating to {targetUrl} to refresh session...");
                        await view.NavigateAndWaitAsync(targetUrl);
                        
                        // Give the browser a moment to commit cookies to storage
                        await Task.Delay(1000);

                        var cookies = view.GetCookies();
                        
                        // Look for the auth cookie
                        var authCookie = cookies?.FirstOrDefault(c => 
                            c.Name.Equals("steamLoginSecure", StringComparison.OrdinalIgnoreCase));

                        if (authCookie != null)
                        {
                            // Try extract ID
                            extractedId = SteamCookieManager.TryExtractSteamId64FromSteamLoginSecure(authCookie.Value);

                            // Success conditions:
                            // A) We extracted a new ID
                            if (!string.IsNullOrEmpty(extractedId))
                            {
                                success = true;
                            }
                            // B) Extraction failed (weird format), but we have a cached ID. 
                            //    Since the cookie EXISTS, we assume the session is valid.
                            else if (!string.IsNullOrEmpty(_selfSteamId64))
                            {
                                extractedId = _selfSteamId64;
                                success = true;
                            }
                        }
                    }
                    tcs.TrySetResult(true);
                }
                catch (Exception ex)
                {
                    _logger?.Error(ex, "[FAF] Offscreen navigation failed.");
                    tcs.TrySetResult(false);
                }
            });

            // Wait here until the UI thread is actually done
            await tcs.Task;

            if (success)
            {
                if (!string.IsNullOrEmpty(extractedId))
                {
                    _selfSteamId64 = extractedId;
                }
                
                _lastSessionCheckUtc = DateTime.UtcNow;
                SaveSessionData();
                _logger?.Info($"[FAF] Session refreshed successfully.");
                return true;
            }

            _logger?.Warn("[FAF] Session check failed (Auth cookie not found). Scraper will continue with cached cookies if available.");
            return false;
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
                            var authCookie = cookies.FirstOrDefault(c => 
                                c.Name.Equals("steamLoginSecure", StringComparison.OrdinalIgnoreCase));

                            if (authCookie != null)
                            {
                                extractedId = SteamCookieManager.TryExtractSteamId64FromSteamLoginSecure(authCookie.Value);
                                loggedIn = !string.IsNullOrWhiteSpace(extractedId);
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
    }
}