using System;
using System.Threading;
using System.Threading.Tasks;
using Playnite.SDK;
using FriendsAchievementFeed.Models;

namespace FriendsAchievementFeed.Services
{
    public class BackgroundUpdateService
    {
        private readonly AchievementFeedService _feedService;
        private readonly FriendsAchievementFeedSettings _settings;
        private readonly ILogger _logger;
        private readonly NotificationPublisher _notifications;
        private readonly Action _onUpdateCompleted;

        private readonly object _ctsLock = new object();
        private CancellationTokenSource _cts;

        public BackgroundUpdateService(
            AchievementFeedService feedService,
            FriendsAchievementFeedSettings settings,
            ILogger logger,
            NotificationPublisher notifications,
            Action onUpdateCompleted)
        {
            _feedService = feedService;
            _settings = settings;
            _logger = logger;
            _notifications = notifications;
            _onUpdateCompleted = onUpdateCompleted;
        }

        public void Start()
        {
            lock (_ctsLock)
            {
                if (_cts != null)
                {
                    return;
                }

                _cts = new CancellationTokenSource();
            }

            var token = _cts.Token;

            var interval = TimeSpan.FromHours(Math.Max(1, _settings.PeriodicUpdateHours));

            // Run an initial check immediately on startup, then continue with the normal loop.
            Task.Run(async () =>
            {
                try
                {
                    await PerformUpdateIfNeeded(interval, token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    var msg = L("LOCFriendsAchFeed_Error_Periodic_InitialCheckFailed", "Periodic update initial check failed.");
                    _logger.Error(ex, msg);
                }

                await PeriodicUpdateLoop(interval, token).ConfigureAwait(false);
            }, token);
        }

        public void Stop()
        {
            CancellationTokenSource ctsToDispose = null;

            lock (_ctsLock)
            {
                if (_cts == null)
                {
                    return;
                }

                ctsToDispose = _cts;
                _cts = null;
            }

            try
            {
                ctsToDispose?.Cancel();
                _feedService?.CancelActiveRebuild();
            }
            catch (Exception ex)
            {
                // Log but ignore shutdown errors to ensure cleanup completes
                _logger?.Debug(ex, "[PeriodicUpdate] Error during background update service shutdown.");
            }
            finally
            {
                ctsToDispose?.Dispose();
            }
        }

        private async Task PeriodicUpdateLoop(TimeSpan interval, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await PerformUpdateIfNeeded(interval, token).ConfigureAwait(false);
                }
                    catch (Exception ex)
                    {
                        var msg = L("LOCFriendsAchFeed_Error_Periodic_UpdateFailed", "Periodic update failed.");
                        _logger.Error(ex, msg);
                    }

                await DelayNextUpdate(interval, token).ConfigureAwait(false);
            }
        }

        private async Task PerformUpdateIfNeeded(TimeSpan interval, CancellationToken token)
        {
            if (ShouldPerformUpdate(interval))
            {
                await ExecuteUpdate(token).ConfigureAwait(false);
            }
            else
            {
                _logger.Debug("[PeriodicUpdate] Cache is recent; skipping update.");
            }
        }

        private bool ShouldPerformUpdate(TimeSpan interval)
        {
            var cacheLast = _feedService.GetCacheLastUpdated();
            _logger.Debug($"[PeriodicUpdate] Cache valid={_feedService.IsCacheValid()}, lastUpdatedUtc={cacheLast?.ToString() ?? "(none)"}");

            return _settings.EnablePeriodicUpdates &&
                    (!_feedService.IsCacheValid() ||
                    !cacheLast.HasValue ||
                    DateTime.UtcNow - cacheLast.Value >= interval);
        }

        private async Task ExecuteUpdate(CancellationToken token)
        {
            _logger.Debug("[PeriodicUpdate] Triggering cache update...");

            try
            {
                await _feedService.StartManagedRebuildAsync(null).ConfigureAwait(false);

                _logger.Debug("[PeriodicUpdate] Cache update completed.");
                HandleUpdateCompletion();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // Graceful shutdown
            }
            catch (Exception ex)
            {
                var msg = L("LOCFriendsAchFeed_Error_Periodic_UpdateFailed", "Periodic update failed.");
                _logger.Error(ex, msg);
            }
        }

        private string L(string key, string fallback)
        {
            return ResourceProvider.GetString(key) ?? fallback;
        }

        private void HandleUpdateCompletion()
        {
            var lastStatus = _feedService.GetLastRebuildStatus() ?? ResourceProvider.GetString("LOCFriendsAchFeed_Rebuild_Completed");
            _notifications?.ShowPeriodicStatus(lastStatus);
            _onUpdateCompleted?.Invoke();
        }

        private async Task DelayNextUpdate(TimeSpan interval, CancellationToken token)
        {
            try
            {
                await Task.Delay(interval, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // Loop will terminate
            }
        }
    }
}
