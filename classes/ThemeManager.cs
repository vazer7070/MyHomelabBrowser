using Microsoft.Web.WebView2.Core;
using System;
using System.Linq;
using System.Windows;

namespace MyHomelabBrowser.classes
{
    /// <summary>
    /// Applique la palette choisie au démarrage. Les styles utilisent des StaticResource :
    /// pour le thème clair, les dictionnaires de styles sont rechargés après la palette
    /// claire, afin que chaque style soit construit avec les bonnes couleurs.
    /// </summary>
    public static class ThemeManager
    {
        public static AppearanceSettings Appearance { get; private set; } = new();

        public static bool IsDark { get; private set; } = true;

        public static void Apply(Application app, AppearanceSettings appearance)
        {
            Appearance = appearance;
            IsDark = appearance.ResolveIsDark();

            if (IsDark)
                return;

            var merged = app.Resources.MergedDictionaries;
            var others = merged
                .Where(d => d.Source != null && !d.Source.OriginalString.EndsWith("Colors.xaml", StringComparison.OrdinalIgnoreCase))
                .Select(d => d.Source)
                .ToList();

            merged.Clear();
            merged.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/themes/ColorsLight.xaml", UriKind.Absolute) });
            foreach (Uri source in others)
                merged.Add(new ResourceDictionary { Source = source });
        }

        /// <summary>
        /// Schéma de couleurs annoncé aux sites (prefers-color-scheme).
        /// </summary>
        public static CoreWebView2PreferredColorScheme PreferredColorScheme => Appearance.Theme switch
        {
            AppTheme.Dark => CoreWebView2PreferredColorScheme.Dark,
            AppTheme.Light => CoreWebView2PreferredColorScheme.Light,
            _ => CoreWebView2PreferredColorScheme.Auto
        };
    }
}
