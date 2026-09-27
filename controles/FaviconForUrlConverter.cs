using MyHomelabBrowser.classes;
using System;
using System.Globalization;
using System.Windows.Data;

namespace MyHomelabBrowser.controles
{
    /// <summary>
    /// Favicon mise en cache pour une adresse (null si le site n'a pas encore été visité).
    /// </summary>
    public sealed class FaviconForUrlConverter : IValueConverter
    {
        public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is string url ? FaviconStore.TryGet(url) : null;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
