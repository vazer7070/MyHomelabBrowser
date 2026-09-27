using System;
using System.Threading.Tasks;
using System.Windows;

namespace MyHomelabBrowser.classes
{
    /// <summary>
    /// Copie un secret (mot de passe, code 2FA) puis l'efface du presse-papiers
    /// s'il y est toujours au bout de quelques secondes.
    /// </summary>
    public static class ClipboardHelper
    {
        public static bool TryCopyWithAutoClear(string secret, int clearAfterSeconds = 30)
        {
            try
            {
                Clipboard.SetText(secret);
            }
            catch
            {
                return false;
            }

            _ = ClearLaterAsync(secret, clearAfterSeconds);
            return true;
        }

        private static async Task ClearLaterAsync(string secret, int seconds)
        {
            await Task.Delay(TimeSpan.FromSeconds(seconds));

            try
            {
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    if (Clipboard.ContainsText() && string.Equals(Clipboard.GetText(), secret, StringComparison.Ordinal))
                        Clipboard.Clear();
                });
            }
            catch
            {
                // Presse-papiers verrouillé par une autre application.
            }
        }
    }
}
