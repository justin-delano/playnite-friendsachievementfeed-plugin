using System.Collections.Generic;
using System.Runtime.Serialization;

namespace FriendsAchievementFeed.Services.Steam
{
    /// <summary>
    /// Shared Steam Web API response models used by both SteamClient and SteamApiHelper.
    /// Consolidates duplicate model definitions to reduce code duplication.
    /// </summary>
    
    // Owned Games API Response Models
    [DataContract]
    internal sealed class OwnedGamesEnvelope
    {
        [DataMember(Name = "response")]
        public OwnedGamesResponse Response { get; set; }
    }

    [DataContract]
    internal sealed class OwnedGamesResponse
    {
        [DataMember(Name = "games")]
        public List<OwnedGame> Games { get; set; }
    }

    [DataContract]
    internal sealed class OwnedGame
    {
        [DataMember(Name = "appid")]
        public int? AppId { get; set; }

        [DataMember(Name = "playtime_forever")]
        public int? PlaytimeForever { get; set; }
    }

    // Schema API Response Models
    [DataContract]
    internal sealed class SchemaRoot
    {
        [DataMember(Name = "response")]
        public SchemaResponse Response { get; set; }
    }

    [DataContract]
    internal sealed class SchemaResponse
    {
        [DataMember(Name = "game")]
        public SchemaGame Game { get; set; }
    }

    [DataContract]
    internal sealed class SchemaGame
    {
        [DataMember(Name = "availableGameStats")]
        public SchemaAvailableGameStats AvailableGameStats { get; set; }
    }

    [DataContract]
    internal sealed class SchemaAvailableGameStats
    {
        [DataMember(Name = "achievements")]
        public SchemaAchievement[] Achievements { get; set; }
    }

    [DataContract]
    internal sealed class SchemaAchievement
    {
        [DataMember(Name = "name")]
        public string Name { get; set; }
    }

    // Player Summaries API Response Models
    [DataContract]
    internal sealed class PlayerSummariesRoot
    {
        [DataMember(Name = "response")]
        public PlayerSummariesResponse Response { get; set; }
    }

    [DataContract]
    internal sealed class PlayerSummariesResponse
    {
        [DataMember(Name = "players")]
        public List<PlayerSummaryDto> Players { get; set; }
    }

    [DataContract]
    internal sealed class PlayerSummaryDto
    {
        [DataMember(Name = "steamid")]
        public string SteamId { get; set; }

        [DataMember(Name = "personaname")]
        public string PersonaName { get; set; }

        [DataMember(Name = "avatar")]
        public string Avatar { get; set; }

        [DataMember(Name = "avatarmedium")]
        public string AvatarMedium { get; set; }

        [DataMember(Name = "avatarfull")]
        public string AvatarFull { get; set; }
    }

    // Friends List API Response Models
    [DataContract]
    internal sealed class FriendListResponseRoot
    {
        [DataMember(Name = "friendslist")]
        public FriendList FriendsList { get; set; }
    }

    [DataContract]
    internal sealed class FriendList
    {
        [DataMember(Name = "friends")]
        public List<FriendEntry> Friends { get; set; }
    }

    [DataContract]
    internal sealed class FriendEntry
    {
        [DataMember(Name = "steamid")]
        public string SteamId { get; set; }

        [DataMember(Name = "relationship")]
        public string Relationship { get; set; }
    }
}
