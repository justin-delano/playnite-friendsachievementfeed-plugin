namespace FriendsAchievementFeed.Models
{
    public class FriendSlot
    {
        public string Name { get; set; } = string.Empty;
        public string SteamId { get; set; } = string.Empty;

        public FriendSlot Clone()
        {
            return new FriendSlot
            {
                Name = Name,
                SteamId = SteamId
            };
        }
    }
}
