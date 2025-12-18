using System;
using System.Collections.Generic;
using System.Text;

namespace Common.SteamKitModels
{
    public class SteamFriend
    {
        public ulong SteamId { get; set; }
        public string Relationship { get; set; }
        public DateTime FriendSince { get; set; }
    }
}
