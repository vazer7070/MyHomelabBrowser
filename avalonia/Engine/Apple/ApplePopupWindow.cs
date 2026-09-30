using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using static PommeBrowser.Engine.Apple.ObjC;

namespace PommeBrowser.Engine.Apple
{
    /// <summary>
    /// Fenêtre ouverte par une page (window.open : connexion OAuth, paiement…). WebKit exige une
    /// vue créée avec la configuration qu'il fournit : elle est placée dans une fenêtre native,
    /// qui garde le lien avec la page (window.opener) et partage sa session.
    /// </summary>
    [SupportedOSPlatform("macos")]
    static class ApplePopupWindow
    {
        const ulong StyleTitled = 1, StyleClosable = 2, StyleMiniaturizable = 4, StyleResizable = 8;
        const ulong BackingBuffered = 2;
        const double DefaultWidth = 1000, DefaultHeight = 720;

        static readonly Dictionary<nint, nint> Windows = new();

        public static nint Create(nint configuration, nint features, nint uiDelegate)
        {
            double width = Dimension(features, "width", DefaultWidth, 200, 1400);
            double height = Dimension(features, "height", DefaultHeight, 150, 1000);
            var frame = new CGRect { X = 0, Y = 0, Width = width, Height = height };

            nint view = SendRectObject(Send(Class("WKWebView"), Sel("alloc")), Sel("initWithFrame:configuration:"), frame, configuration);
            if (view == 0)
                return 0;
            Send(view, Sel("setUIDelegate:"), uiDelegate);

            nint window = SendRect(Send(Class("NSWindow"), Sel("alloc")), Sel("initWithContentRect:styleMask:backing:defer:"),
                frame, StyleTitled | StyleClosable | StyleMiniaturizable | StyleResizable, BackingBuffered, false);
            SendBool(window, Sel("setReleasedWhenClosed:"), false);
            Send(window, Sel("setTitle:"), String("PommeBrowser"));
            Send(window, Sel("setContentView:"), view);
            Send(window, Sel("center"));
            Send(window, Sel("makeKeyAndOrderFront:"), 0);
            Windows[view] = window;

            // La fenêtre retient la vue ; WebKit attend un objet qu'il ne possède pas encore.
            return Send(view, Sel("autorelease"));
        }

        /// <summary>window.close() dans la fenêtre : elle se ferme. Faux si la vue n'est pas une de ces fenêtres.</summary>
        public static bool Close(nint view)
        {
            if (!Windows.Remove(view, out nint window))
                return false;
            Send(window, Sel("close"));
            Release(window);
            return true;
        }

        static double Dimension(nint features, string property, double fallback, double min, double max)
        {
            if (features == 0)
                return fallback;
            nint number = Send(features, Sel(property));
            if (number == 0)
                return fallback;
            double value = GetDouble(number, Sel("doubleValue"));
            return value >= min ? Math.Min(value, max) : fallback;
        }
    }
}
