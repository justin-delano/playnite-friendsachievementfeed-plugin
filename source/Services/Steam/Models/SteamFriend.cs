using System;
using System.Collections.Generic;
using System.Text;

namespace FriendsAchievementFeed.Services.Steam.Models
{
    public class SteamFriend
    {
        public ulong SteamId { get; set; }
        public string Relationship { get; set; }
        public DateTime FriendSince { get; set; }
    }
}
