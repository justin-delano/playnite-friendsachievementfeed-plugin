using Common;

namespace FriendsAchievementFeed.Models
{
    public class FriendSlot : ObservableObjectPlus
    {
        private string _name = string.Empty;
        private string _steamId = string.Empty;

        public string Name
        {
            get => _name;
            set => SetValue(ref _name, value ?? string.Empty);
        }

        public string SteamId
        {
            get => _steamId;
            set => SetValue(ref _steamId, value ?? string.Empty);
        }

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
