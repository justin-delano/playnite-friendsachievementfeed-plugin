using System;
using System.Runtime.Serialization;

namespace FriendsAchievementFeed.Services.Steam.Models
{
    /// <summary>
    /// Steam Session Data (persisted for self Steam ID only - cookies are in CEF).
    /// Extracted from SteamClient.cs for better organization.
    /// </summary>
    [DataContract]
    internal sealed class SteamSessionData
    {
        [DataMember] public DateTime LastValidatedUtc { get; set; }
        [DataMember] public string SelfSteamId64 { get; set; }

        /// <summary>
        /// Optional: Track when we last fetched friends list
        /// </summary>
        [DataMember] public DateTime? LastFriendsRefreshUtc { get; set; }

        /// <summary>
        /// Optional: Cache session validation state
        /// </summary>
        [DataMember] public bool IsValidatedSession { get; set; }

        public bool IsExpired(TimeSpan maxAge)
        {
            return DateTime.UtcNow - LastValidatedUtc > maxAge;
        }

        public bool NeedsRefresh(TimeSpan refreshInterval)
        {
            if (!LastFriendsRefreshUtc.HasValue)
                return true;
            
            return DateTime.UtcNow - LastFriendsRefreshUtc.Value > refreshInterval;
        }
    }
}