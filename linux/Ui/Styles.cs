namespace PommeBrowser.Linux.Ui
{
    /// <summary>Styles propres au navigateur, par-dessus ceux de libadwaita.</summary>
    static class Styles
    {
        const string Css = """
            .omnibox { min-width: 280px; }
            .omnibox-suggestions row { padding: 6px 10px; }
            .omnibox-suggestions .dim-label { font-size: smaller; }

            .status-dot { min-width: 10px; min-height: 10px; border-radius: 5px; background-color: alpha(currentColor, 0.25); }
            .status-dot.online { background-color: @success_color; }
            .status-dot.degraded { background-color: @warning_color; }
            .status-dot.offline { background-color: @error_color; }

            .home-page { padding: 24px 12px 48px 12px; }
            .home-title { font-size: 28px; font-weight: 800; }
            .home-card { padding: 12px 14px; border-radius: 12px; }
            .home-card .card-title { font-weight: 600; }
            .home-card .card-subtitle { font-size: smaller; opacity: 0.7; }

            .link-status { margin: 6px; padding: 3px 8px; border-radius: 6px; font-size: smaller;
                           background-color: alpha(@window_bg_color, 0.96); color: @window_fg_color;
                           box-shadow: 0 1px 3px alpha(black, 0.25); }

            window.private headerbar, window.private tabbar > revealer > box {
                background-color: mix(@headerbar_bg_color, #6a4c9c, 0.28);
            }
            .private-badge { font-weight: 700; color: #b69ae8; }
            """;

        public static void Load()
        {
            Gdk.Display? display = Gdk.Display.GetDefault();
            if (display == null)
                return;

            var provider = Gtk.CssProvider.New();
            provider.LoadFromString(Css);
            Gtk.StyleContext.AddProviderForDisplay(display, provider, 600);

            // Icône de l'application (fenêtres, dialogues) : fournie avec l'application.
            Gtk.IconTheme.GetForDisplay(display).AddSearchPath(System.IO.Path.Combine(System.AppContext.BaseDirectory, "icons"));
        }
    }
}
