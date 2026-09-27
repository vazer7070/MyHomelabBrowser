using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace MyHomelabBrowser.classes
{
    public class BoolToVisibility : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool b && b)
                return Visibility.Visible;

            return Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}

namespace MyHomelabBrowser.classes
{
    /// <summary>
    /// Visible si la valeur est une chaîne non vide (bouton « effacer » d'un champ).
    /// </summary>
    public class NonEmptyToVisibility : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is string s && s.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
