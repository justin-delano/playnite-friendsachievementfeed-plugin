using System;
using FriendsAchievementFeed.Models;
using Playnite.SDK;

namespace FriendsAchievementFeed.Services
{
    internal class RebuildProgressMapper
    {
        private volatile bool _selfStartedSeen;
        private volatile bool _selfCompletedSeen;
        private RebuildStage? _lastStage;

        public void Reset()
        {
            _selfStartedSeen = false;
            _selfCompletedSeen = false;
            _lastStage = null;
        }

        public ProgressReport Map(RebuildUpdate update)
        {
            if (update == null)
            {
                return null;
            }

            if (update.Kind == RebuildUpdateKind.Stage)
            {
                _lastStage = update.Stage;

                var msg = StageMessage(update.Stage);
                if (!string.IsNullOrWhiteSpace(msg))
                {
                    if (update.Stage == RebuildStage.RefreshingSelfAchievements)
                    {
                        _selfStartedSeen = true;
                    }

                    return Build(msg);
                }

                return null;
            }

            switch (update.Kind)
            {
                case RebuildUpdateKind.SelfStarted:
                {
                    if (_selfStartedSeen) return null;
                    _selfStartedSeen = true;

                    var msg = string.Format(
                        StringResources.GetString("LOCFriendsAchFeed_Rebuild_Line_Self"),
                        StringResources.GetString("LOCFriendsAchFeed_Label_You") ?? "You",
                        StringResources.GetString("LOCFriendsAchFeed_Rebuild_Action_RefreshingAchievements"),
                        CountsText(0, update.SelfAppCount),
                        AppSuffix(update));

                    return Build(msg);
                }

                case RebuildUpdateKind.SelfProgress:
                {
                    var steps = ProgressSteps(update);
                    var msg = string.Format(
                        StringResources.GetString("LOCFriendsAchFeed_Rebuild_Line_Self"),
                        StringResources.GetString("LOCFriendsAchFeed_Label_You") ?? "You",
                        StringResources.GetString("LOCFriendsAchFeed_Rebuild_Action_RefreshingAchievements"),
                        CountsText(update.SelfAppIndex, update.SelfAppCount),
                        AppSuffix(update));

                    return Build(msg, steps.Cur, steps.Total);
                }

                case RebuildUpdateKind.SelfCompleted:
                {
                    if (_selfCompletedSeen) return null;
                    _selfCompletedSeen = true;

                    var steps = ProgressSteps(update);
                    return Build(
                        StringResources.GetString("LOCFriendsAchFeed_Rebuild_YourCacheUpToDate") ?? "Your achievements cache is up to date.",
                        steps.Cur, steps.Total);
                }

                case RebuildUpdateKind.FriendStarted:
                {
                    var headerTemplate = StringResources.GetString("LOCFriendsAchFeed_Rebuild_Line_Friend");
                    var detailJoin = StringResources.GetString("LOCFriendsAchFeed_Rebuild_Detail_WithSeparator");
                    var candidates = string.Format(StringResources.GetString("LOCFriendsAchFeed_Rebuild_Label_Candidates"), Math.Max(0, update.CandidateGames));
                    var scanCounts = CountsText(0, update.FriendAppCount);
                    var scanText = string.Format(StringResources.GetString("LOCFriendsAchFeed_Rebuild_Action_ScanningCounts"), scanCounts);
                    var detail = string.Format(detailJoin, candidates, scanText);

                    var msg = string.Format(headerTemplate ?? "{0} ({1}/{2}) — {3}", update.FriendPersonaName, update.FriendIndex, update.FriendCount, detail);

                    var steps = ProgressSteps(update, update.FriendIndex, update.FriendCount);
                    return Build(msg, steps.Cur, steps.Total);
                }

                case RebuildUpdateKind.FriendProgress:
                {
                    var headerTemplate = StringResources.GetString("LOCFriendsAchFeed_Rebuild_Line_Friend");
                    var scanCounts = CountsText(update.FriendAppIndex, update.FriendAppCount);
                    var scanText = string.Format(StringResources.GetString("LOCFriendsAchFeed_Rebuild_Action_ScanningCounts"), scanCounts) + AppSuffix(update);

                    var msg = string.Format(headerTemplate ?? "{0} ({1}/{2}) — {3}", update.FriendPersonaName, update.FriendIndex, update.FriendCount, scanText);

                    var steps = ProgressSteps(update, update.FriendIndex, update.FriendCount);
                    return Build(msg, steps.Cur, steps.Total);
                }

                case RebuildUpdateKind.FriendCompleted:
                {
                    var headerTemplate = StringResources.GetString("LOCFriendsAchFeed_Rebuild_Line_Friend");
                    var detailJoin = StringResources.GetString("LOCFriendsAchFeed_Rebuild_Detail_WithSeparator");
                    var candidates = string.Format(StringResources.GetString("LOCFriendsAchFeed_Rebuild_Label_Candidates"), Math.Max(0, update.CandidateGames));
                    var newEntries = string.Format(StringResources.GetString("LOCFriendsAchFeed_Rebuild_Label_NewEntries"), Math.Max(0, update.FriendNewEntries));
                    var detail = string.Format(detailJoin, candidates, newEntries);

                    var msg = string.Format(headerTemplate ?? "{0} ({1}/{2}) — {3}", update.FriendPersonaName, update.FriendIndex, update.FriendCount, detail);

                    var steps = ProgressSteps(update, update.FriendIndex, update.FriendCount);
                    return Build(msg, steps.Cur, steps.Total);
                }

                default:
                    return null;
            }
        }

        private ProgressReport Build(string message, int current = 0, int total = 0, bool canceled = false)
            => new ProgressReport { Message = message, CurrentStep = current, TotalSteps = total, IsCanceled = canceled };

        private string StageMessage(RebuildStage stage)
        {
            switch (stage)
            {
                case RebuildStage.NotConfigured: return StringResources.GetString("LOCFriendsAchFeed_Error_SteamNotConfigured");
                case RebuildStage.LoadingOwnedGames: return StringResources.GetString("LOCFriendsAchFeed_Targeted_LoadingOwnedGames");
                case RebuildStage.LoadingFriends: return StringResources.GetString("LOCFriendsAchFeed_Targeted_LoadingFriends");
                case RebuildStage.LoadingExistingCache: return StringResources.GetString("LOCFriendsAchFeed_Targeted_LoadingExistingCache");
                case RebuildStage.LoadingSelfOwnedApps: return StringResources.GetString("LOCFriendsAchFeed_Targeted_LoadingSelfOwnedApps");
                case RebuildStage.RefreshingSelfAchievements: return StringResources.GetString("LOCFriendsAchFeed_Targeted_RefreshingSelfAchievements");
                case RebuildStage.ProcessingFriends: return StringResources.GetString("LOCFriendsAchFeed_Targeted_ProcessingFriends");
                default: return null;
            }
        }

        private (int Cur, int Total) ProgressSteps(RebuildUpdate update, int fallbackCur = 0, int fallbackTotal = 0)
        {
            if (update != null && update.OverallCount > 0)
            {
                return (Math.Max(0, update.OverallIndex), Math.Max(1, update.OverallCount));
            }

            return (Math.Max(0, fallbackCur), Math.Max(0, fallbackTotal));
        }

        private string CountsText(int current, int total)
        {
            if (total > 0)
            {
                return string.Format(StringResources.GetString("LOCFriendsAchFeed_Format_Counts"), Math.Max(0, current), Math.Max(1, total));
            }

            return StringResources.GetString("LOCFriendsAchFeed_Text_Ellipsis");
        }

        private string AppSuffix(RebuildUpdate update)
        {
            if (update == null)
            {
                return string.Empty;
            }

            if (!string.IsNullOrWhiteSpace(update.CurrentGameName))
            {
                return " — " + update.CurrentGameName;
            }

            if (update.CurrentAppId > 0)
            {
                var appIdLabel = string.Format(StringResources.GetString("LOCFriendsAchFeed_Rebuild_Label_AppId"), update.CurrentAppId);
                return " — " + appIdLabel;
            }

            return string.Empty;
        }
    }
}
