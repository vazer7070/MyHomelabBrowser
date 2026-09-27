using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace MyHomelabBrowser.classes
{
    /// <summary>
    /// Barre de titre Windows sombre, assortie au thème. Sous Windows 11, elle prend
    /// la couleur de la barre d'onglets ; sous Windows 10, le mode sombre seul s'applique.
    /// </summary>
    internal static class DarkTitleBar
    {
        private const int DwmwaUseImmersiveDarkModeBefore20H1 = 19;
        private const int DwmwaUseImmersiveDarkMode = 20;
        private const int DwmwaCaptionColor = 35;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        /// <summary>
        /// Applique le style à toutes les fenêtres de l'application dès leur chargement.
        /// </summary>
        public static void RegisterForAllWindows()
        {
            EventManager.RegisterClassHandler(
                typeof(Window),
                FrameworkElement.LoadedEvent,
                new RoutedEventHandler((sender, _) =>
                {
                    if (sender is Window window)
                        Apply(window);
                }));
        }

        /// <summary>
        /// À appeler dans le constructeur pour éviter l'affichage d'une barre claire au premier rendu.
        /// </summary>
        public static void Apply(Window window)
        {
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero)
            {
                window.SourceInitialized += (_, _) => Apply(window);
                return;
            }

            try
            {
                int enabled = 1;
                if (DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref enabled, sizeof(int)) != 0)
                    DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkModeBefore20H1, ref enabled, sizeof(int));

                // Attribut ignoré (code d'erreur) avant Windows 11.
                if (window.TryFindResource("ChromeColor") is Color color)
                {
                    int colorRef = color.R | (color.G << 8) | (color.B << 16);
                    DwmSetWindowAttribute(hwnd, DwmwaCaptionColor, ref colorRef, sizeof(int));
                }
            }
            catch (DllNotFoundException)
            {
            }
            catch (EntryPointNotFoundException)
            {
            }
        }
    }
}
