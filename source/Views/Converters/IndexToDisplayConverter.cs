using System;
using System.Globalization;
using System.Windows.Data;

namespace FriendsAchievementFeed.Views.Converters
{
    // Converts zero-based index to a 1-based label for display slots.
    public sealed class IndexToDisplayConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is int idx)
            {
                return (idx + 1).ToString();
            }

            return string.Empty;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}
