using System;

namespace PommeBrowser.Linux.Ui
{
    /// <summary>
    /// Presse-papiers pour les secrets (mot de passe, code 2FA) : effacé au bout de 30 secondes,
    /// sauf si autre chose a été copié entre-temps.
    /// </summary>
    static class SecureClipboard
    {
        public const int ClearAfterSeconds = 30;

        public static bool Copy(string text, bool secret)
        {
            Gdk.Display? display = Gdk.Display.GetDefault();
            if (display == null)
                return false;

            Gdk.Clipboard clipboard = display.GetClipboard();
            clipboard.SetText(text);
            if (!secret)
                return true;

            IntPtr ours = clipboard.GetContent() is { } content ? content.Handle.DangerousGetHandle() : IntPtr.Zero;
            GLib.Functions.TimeoutAddSeconds(0, ClearAfterSeconds, () =>
            {
                // Toujours notre contenu : personne n'a copié autre chose depuis.
                if (ours != IntPtr.Zero && clipboard.GetContent() is { } current && current.Handle.DangerousGetHandle() == ours)
                    clipboard.SetContent(null);
                return false;
            });
            return true;
        }
    }
}
