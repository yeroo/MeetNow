using System;
using System.Globalization;
using System.Windows.Data;
namespace MeetNow
{
    public class OrganizerInitialsConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value != null)
            {
                string[] words = value.ToString().Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                string result = string.Empty;
                foreach (string word in words) { result += word[0]; }
                return result.Length>2?result.Substring(0,2):result;
            }
            else { return "-"; }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}