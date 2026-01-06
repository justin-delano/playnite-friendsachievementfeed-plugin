using System;
using System.Windows;
using System.Windows.Threading;
using FriendsAchievementFeed.Models;
using FriendsAchievementFeed.Services;
using Playnite.SDK;

namespace FriendsAchievementFeed.Views.Helpers
{
    internal sealed class RebuildProgressHandler
    {
        private readonly FeedManager _feedService;
        private readonly ILogger _logger;
        private readonly object _progressUiLock = new object();
        private DateTime _lastProgressUiUpdateUtc = DateTime.MinValue;
        private static readonly TimeSpan ProgressUiMinInterval = TimeSpan.FromMilliseconds(50);
        private bool _userRequestedCancel = false;

        public event EventHandler<ProgressUpdateEventArgs> ProgressUpdated;

        public RebuildProgressHandler(FeedManager feedService, ILogger logger)
        {
            _feedService = feedService ?? throw new ArgumentNullException(nameof(feedService));
            _logger = logger;
            _feedService.RebuildProgress += OnRebuildProgress;
        }

        public void CancelRebuild()
        {
            if (_feedService?.IsRebuilding == true)
            {
                _userRequestedCancel = true;
                _feedService.CancelActiveRebuild();
            }
        }

        private void OnRebuildProgress(object sender, ProgressReport report)
        {
            try
            {
                var now = DateTime.UtcNow;
                double pct;
                
                lock (_progressUiLock)
                {
                    pct = CalculatePercent(report);
                    var isFinal = pct >= 100 || (report?.TotalSteps > 0 && report.CurrentStep >= report.TotalSteps);
                    
                    if (!isFinal && (now - _lastProgressUiUpdateUtc) < ProgressUiMinInterval)
                        return;

                    _lastProgressUiUpdateUtc = now;
                }

                Application.Current?.Dispatcher?.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        var percent = CalculatePercent(report);
                        var msg = report?.Message ?? _feedService.GetLastRebuildStatus() ?? string.Empty;

                        if (report?.IsCanceled == true && !_userRequestedCancel)
                        {
                            msg = _feedService.GetLastRebuildStatus() ?? string.Empty;
                        }
                        else if (report?.IsCanceled == true)
                        {
                            _userRequestedCancel = false;
                        }

                        ProgressUpdated?.Invoke(this, new ProgressUpdateEventArgs
                        {
                            Percent = percent,
                            Message = msg,
                            IsRebuilding = _feedService.IsRebuilding
                        });
                    }
                    catch (Exception ex)
                    {
                        _logger?.Debug($"Progress UI update error: {ex.Message}");
                    }
                }), DispatcherPriority.Background);
            }
            catch (Exception ex)
            {
                _logger?.Debug($"Progress dispatch failed: {ex.Message}");
            }
        }

        private static double CalculatePercent(ProgressReport report)
        {
            var pct = report?.PercentComplete ?? 0;
            if ((pct <= 0 || double.IsNaN(pct)) && report != null && report.TotalSteps > 0)
            {
                pct = Math.Max(0, Math.Min(100, (report.CurrentStep * 100.0) / report.TotalSteps));
            }
            return pct;
        }

        public void Dispose()
        {
            if (_feedService != null)
            {
                _feedService.RebuildProgress -= OnRebuildProgress;
            }
        }
    }

    internal sealed class ProgressUpdateEventArgs : EventArgs
    {
        public double Percent { get; set; }
        public string Message { get; set; }
        public bool IsRebuilding { get; set; }
    }
}
