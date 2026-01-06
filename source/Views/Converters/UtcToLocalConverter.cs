using System;
using System.Globalization;
using System.Windows.Data;
using Common;

namespace FriendsAchievementFeed.Views.Converters
{
    // Converts DateTime values to local time for display.
    public sealed class UtcToLocalConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null)
            {
                return null;
            }

            if (value is DateTime dt)
            {
                return DateTimeUtilities.AsLocalFromUtc(dt);
            }

            return value;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value;
        }
    }
}
