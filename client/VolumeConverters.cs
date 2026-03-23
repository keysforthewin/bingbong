using System;
using System.Globalization;
using System.Windows.Data;

namespace bingbong
{
    /// <summary>
    /// Converts between float volume (0.0–1.0) and slider value (0–100).
    /// </summary>
    public class VolumeToSliderConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is float f)
                return (double)(f * 100f);
            return 100.0;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is double d)
                return (float)(d / 100.0);
            return 1.0f;
        }
    }

    /// <summary>
    /// Converts float volume (0.0–1.0) to a display string like "80%".
    /// </summary>
    public class VolumeToPercentConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is float f)
                return $"{(int)(f * 100)}%";
            return "100%";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
