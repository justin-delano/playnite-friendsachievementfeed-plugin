using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FriendsAchievementFeed.Models;
using Playnite.SDK;

namespace FriendsAchievementFeed.Services
{
    /// <summary>
    /// Centralizes cache-exposure and debounced settings persistence.
    /// </summary>
    public class SettingsPersistenceService
    {
        private readonly FriendsAchievementFeedSettings _settings;
        private readonly FriendsAchievementFeedPlugin _plugin;
        private readonly ICacheService _cacheService;
        private readonly ILogger _logger;
        private readonly Action<Action> _postToUi;

        private readonly SemaphoreSlim _settingsSaveGate = new SemaphoreSlim(1, 1);
        private CancellationTokenSource _settingsSaveDebounceCts;

        public SettingsPersistenceService(
            FriendsAchievementFeedSettings settings,
            FriendsAchievementFeedPlugin plugin,
            ICacheService cacheService,
            ILogger logger,
            Action<Action> postToUi)
        {
            _settings = settings;
            _plugin = plugin;
            _cacheService = cacheService;
            _logger = logger;
            _postToUi = postToUi;
        }

        public void OnCacheChanged(object sender, EventArgs e)
        {
            _postToUi(() =>
            {
                try
                {
                    var entries = _cacheService.GetCachedFriendEntries() ?? new List<FeedEntry>();

                    var cacheDir = Path.Combine(_plugin.GetPluginUserDataPath(), "achievement_cache");
                    _settings.ExposedGlobalFeedPath = cacheDir;

                    _settings.ExposedGameFeeds = entries
                        .GroupBy(en => en.PlayniteGameId?.ToString() ?? en.AppId.ToString())
                        .Where(g => !string.IsNullOrWhiteSpace(g.Key))
                        .ToDictionary(
                            g => g.Key,
                            g => Path.Combine(cacheDir, $"{g.Key}.json"),
                            StringComparer.OrdinalIgnoreCase);

                    ScheduleSettingsSave();
                }
                catch (Exception ex)
                {
                    _logger?.Error(ex, ResourceProvider.GetString("LOCFriendsAchFeed_Error_FileOperationFailed"));
                }
            });
        }

        private void ScheduleSettingsSave()
        {
            _settingsSaveDebounceCts?.Cancel();
            _settingsSaveDebounceCts?.Dispose();
            _settingsSaveDebounceCts = new CancellationTokenSource();
            var token = _settingsSaveDebounceCts.Token;

            Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(400, token).ConfigureAwait(false);

                    await _settingsSaveGate.WaitAsync(token).ConfigureAwait(false);
                    try
                    {
                        for (var attempt = 0; attempt < 5; attempt++)
                        {
                            token.ThrowIfCancellationRequested();
                            try
                            {
                                _settings.EndEdit();
                                return;
                            }
                            catch (IOException)
                            {
                                await Task.Delay(150 * (attempt + 1), token).ConfigureAwait(false);
                            }
                        }
                    }
                    finally
                    {
                        _settingsSaveGate.Release();
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    _logger?.Error(ex, "[FAF] Failed to persist plugin settings.");
                }
            });
        }
    }
}
