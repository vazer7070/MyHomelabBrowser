using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia.Controls;
using Avalonia.Platform;
using Microsoft.Web.WebView2.Core;
using MyHomelabBrowser.classes;

namespace PommeBrowser.Engine.WebView2
{
    /// <summary>
    /// Fenêtre ouverte par une page avec window.open et une taille (connexion OAuth, paiement…) :
    /// fenêtre séparée qui garde son lien avec la page (window.opener, postMessage), comme dans
    /// l'édition WPF. Elle partage la session de l'onglet (même environnement, même profil).
    /// </summary>
    [SupportedOSPlatform("windows")]
    static class WebView2PopupWindow
    {
        const int DefaultWidth = 1000;
        const int DefaultHeight = 720;

        public static void Open(NativeWebView opener, CoreWebView2NewWindowRequestedEventArgs request, bool isPrivate)
        {
            CoreWebView2Deferral deferral = request.GetDeferral();
            (int width, int height) = Size(request);

            var view = new NativeWebView();
            EngineHost.Prepare(view, isPrivate);
            Window? owner = TopLevel.GetTopLevel(opener) as Window;
            var window = new Window
            {
                Width = width,
                Height = height,
                Title = "PommeBrowser",
                Icon = owner?.Icon,
                Content = view,
                WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen
            };

            bool answered = false;
            void Answer(Action decide)
            {
                if (answered)
                    return;
                answered = true;
                try
                {
                    decide();
                }
                catch (Exception ex) when (ex is InvalidOperationException or COMException or ArgumentException)
                {
                    RuntimeLogBuffer.Append("[WebView2] Fenêtre : " + ex.Message);
                }
                finally
                {
                    deferral.Complete();
                }
            }

            // La vue ne doit encore rien avoir chargé : WebView2 y ouvre lui-même la page demandée.
            view.AdapterCreated += (_, _) => Answer(() =>
            {
                if (view.TryGetPlatformHandle() is not IWindowsWebView2PlatformHandle handle)
                {
                    request.Handled = false;
                    return;
                }

                CoreWebView2 core = CoreWebView2.CreateFromComICoreWebView2(handle.CoreWebView2);
                core.DocumentTitleChanged += (_, _) =>
                    window.Title = string.IsNullOrWhiteSpace(core.DocumentTitle) ? "PommeBrowser" : core.DocumentTitle;
                core.WindowCloseRequested += (_, _) => window.Close();
                request.NewWindow = core;
                request.Handled = true;
            });

            // Fermée avant d'être prête : la page n'obtient pas de fenêtre.
            window.Closed += (_, _) => Answer(() => request.Handled = true);

            if (owner != null)
                window.Show(owner);
            else
                window.Show();
        }

        static (int Width, int Height) Size(CoreWebView2NewWindowRequestedEventArgs request)
        {
            try
            {
                if (request.WindowFeatures is { HasSize: true } features)
                {
                    int width = (int)features.Width;
                    int height = (int)features.Height;
                    return (width >= 200 ? Math.Min(width, 1400) : DefaultWidth, height >= 150 ? Math.Min(height, 1000) : DefaultHeight);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or COMException)
            {
            }
            return (DefaultWidth, DefaultHeight);
        }
    }
}
