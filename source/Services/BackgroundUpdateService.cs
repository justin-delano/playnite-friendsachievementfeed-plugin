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
            if (_cts != null)
            {
                return;
            }

            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            Task.Run(async () => await PeriodicUpdateLoop(token).ConfigureAwait(false), token);
        }

        public void Stop()
        {
            try
            {
                _cts?.Cancel();
                _feedService?.CancelActiveRebuild();
            }
            catch
            {
                // ignore shutdown errors
            }
            finally
            {
                _cts?.Dispose();
                _cts = null;
            }
        }

        private async Task PeriodicUpdateLoop(CancellationToken token)
        {
            var interval = TimeSpan.FromHours(Math.Max(1, _settings.PeriodicUpdateHours));

            while (!token.IsCancellationRequested)
            {
                try
                {
                    await PerformUpdateIfNeeded(interval, token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "[PeriodicUpdate] Unexpected error in periodic update loop");
                }

                await DelayNextUpdate(interval, token).ConfigureAwait(false);
            }
        }

        private async Task PerformUpdateIfNeeded(TimeSpan interval, CancellationToken token)
        {
            if (ShouldPerformUpdate(interval))
            {
                await ExecuteDeltaUpdate(token).ConfigureAwait(false);
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

        private async Task ExecuteDeltaUpdate(CancellationToken token)
        {
            _logger.Debug("[PeriodicUpdate] Triggering delta cache update...");

            try
            {
                await _feedService.StartManagedRebuildAsync(null).ConfigureAwait(false);

                _logger.Debug("[PeriodicUpdate] Delta cache update completed.");
                HandleUpdateCompletion();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // Graceful shutdown
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "[PeriodicUpdate] Failed to perform delta update");
            }
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
