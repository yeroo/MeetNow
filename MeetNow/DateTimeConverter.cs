using System;
using System.Windows.Data;

namespace MeetNow
{
    public class DateTimeConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            DateTime dateTime = (DateTime)value;
            return dateTime.ToString("HH:mm");
        }

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            // This converter does not convert back.
            throw new NotImplementedException();
        }
    }
}
