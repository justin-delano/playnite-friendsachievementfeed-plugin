using System;
using System.Collections.Generic;

namespace FriendsAchievementFeed.Models
{
    /// <summary>
    /// Options for cache rebuild operations.
    /// Extracted from CacheRebuildService.cs for better organization.
    /// </summary>
    public sealed class CacheRebuildOptions
    {
        public List<string> FamilySharingFriendIDs { get; set; } = null;
        public bool LimitToFamilySharingFriends { get; set; } = false;
    }

    /// <summary>
    /// Flexible scan options:
    /// - Scan across games, friends, or both.
    /// - Support explicit game selection (Playnite IDs / Steam AppIds), or inferred via shared library intersection.
    /// - Quick scan mode supports a tiny incremental update (recent friends + recent games).
    /// - Emits a unified overall progress bar (OverallIndex/OverallCount).
    /// </summary>
    public sealed class CacheScanOptions
    {
        public IReadOnlyCollection<System.Guid> PlayniteGameIds { get; set; }
        public IReadOnlyCollection<int> SteamAppIds { get; set; }
        public IReadOnlyCollection<string> FriendSteamIds { get; set; }

        public IReadOnlyCollection<string> IncludeUnownedFriendIds { get; set; }

        public bool IncludeSelf { get; set; } = true;
        public bool IncludeFriends { get; set; } = true;

        /// <summary>
        /// If true, refresh friend achievements for all Steam games in Playnite DB, regardless of ownership/minutes.
        /// NOTE: This can be slow. Default is "shared games per friend", dropping 0-minute entries when minutes are available.
        /// </summary>
        public bool FriendsAllLibraryApps { get; set; } = false;

        /// <summary>
        /// If true, refresh self achievements for all Steam games in Playnite DB (slow).
        /// NOTE: This overrides default behavior.
        /// </summary>
        public bool SelfAllLibraryApps { get; set; } = false;

        /// <summary>
        /// When explicit apps are selected, allow recording family-share discoveries based on those scans.
        /// </summary>
        public bool ExplicitAppsAllowUnownedDiscovery { get; set; } = true;

        /// <summary>
        /// Quick incremental mode:
        /// - Choose up to QuickScanRecentFriendsCount most-recent friends (by cached unlock activity).
        /// - For each, choose up to QuickScanRecentGamesPerFriend most-recent games (by cached unlock activity).
        /// - Scan ONLY those friend/game pairs (<= friendsCount * gamesPerFriend).
        /// - Then run self scan (last) ONLY for affected games.
        ///
        /// This mode intentionally does NOT expand via IncludeUnownedFriendIds or Forced apps, because the goal is a tiny bounded scan.
        /// </summary>
        public bool QuickScanRecentPairs { get; set; } = false;

        public int QuickScanRecentFriendsCount { get; set; } = 5;

        public int QuickScanRecentGamesPerFriend { get; set; } = 5;
    }

    public enum RebuildUpdateKind
    {
        Stage,
        SelfStarted,
        SelfProgress,
        SelfCompleted,

        FriendStarted,
        FriendProgress,
        FriendCompleted,

        Completed
    }

    public enum RebuildStage
    {
        NotConfigured,
        LoadingOwnedGames,
        LoadingFriends,
        LoadingExistingCache,
        LoadingSelfOwnedApps,
        RefreshingSelfAchievements,
        ProcessingFriends,
        Completed
    }

    /// <summary>
    /// Progress update information for cache rebuild operations.
    /// </summary>
    public sealed class RebuildUpdate
    {
        public RebuildUpdateKind Kind { get; set; }
        public RebuildStage Stage { get; set; }

        public string FriendSteamId { get; set; }
        public string FriendPersonaName { get; set; }
        public int FriendIndex { get; set; }
        public int FriendCount { get; set; }

        public int CandidateGames { get; set; }
        public int IncludeUnownedCandidates { get; set; }

        public int FriendNewEntries { get; set; }

        public int FriendAppIndex { get; set; }
        public int FriendAppCount { get; set; }

        public int SelfAppIndex { get; set; }
        public int SelfAppCount { get; set; }

        public int CurrentAppId { get; set; }
        public string CurrentGameName { get; set; }

        public bool FriendOwnershipDataUnavailable { get; set; }

        public int TotalNewEntriesSoFar { get; set; }
        public int TotalCandidateGamesSoFar { get; set; }
        public int TotalIncludeUnownedCandidatesSoFar { get; set; }

        // unified progress bar
        public int OverallIndex { get; set; }
        public int OverallCount { get; set; }
    }

    /// <summary>
    /// Summary information after a cache rebuild operation completes.
    /// </summary>
    public sealed class RebuildSummary
    {
        public int NewEntriesCount { get; set; }
        public int CandidateGamesTotal { get; set; }
        public int IncludeUnownedCandidatesTotal { get; set; }
        public bool NoCandidatesDetected { get; set; }
        public int FriendsOwnershipDataUnavailable { get; set; }
    }

    /// <summary>
    /// Payload information for cache rebuild events.
    /// </summary>
    public sealed class RebuildPayload
    {
        public RebuildSummary Summary { get; set; } = new RebuildSummary();
        public List<FriendsAchievementFeed.Models.FeedEntry> NewEntries { get; set; } = new List<FriendsAchievementFeed.Models.FeedEntry>();
    }

    /// <summary>
    /// Family sharing scan result data.
    /// New format: stored per Playnite game file (Guid filename), containing SteamID64s.
    /// </summary>
    public class FamilySharingScanResult
    {
        public DateTime LastUpdatedUtc { get; set; }
        public List<string> SteamIds { get; set; } = new List<string>();
    }
}