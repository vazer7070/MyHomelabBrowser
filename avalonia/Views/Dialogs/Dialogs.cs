using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace PommeBrowser.Views.Dialogs
{
    /// <summary>Boîtes de confirmation et de message.</summary>
    public static class Dialogs
    {
        /// <summary>Question : vrai si l'utilisateur confirme.</summary>
        public static Task<bool> ConfirmAsync(Window owner, string heading, string body, string confirmLabel, bool destructive = false)
        {
            var dialog = new FormDialog(heading, confirmLabel, destructive);
            dialog.AddText(body);
            return dialog.ShowAsync(owner);
        }

        /// <summary>Simple message, avec un bouton « Fermer ».</summary>
        public static Task AlertAsync(Window owner, string heading, string body)
        {
            var dialog = new FormDialog(heading, Tr("Fermer"), cancelLabel: string.Empty);
            dialog.AddText(body);
            return dialog.ShowAsync(owner);
        }

        /// <summary>
        /// Plusieurs réponses possibles : index du bouton choisi (de gauche à droite), ou -1 si la
        /// boîte est fermée sans réponse. Le dernier bouton est le choix par défaut.
        /// </summary>
        public static async Task<int> ChoiceAsync(Window owner, string heading, string body, params (string Label, bool Primary, bool Destructive)[] buttons)
        {
            int result = -1;
            var dialog = new FormDialog(heading, string.Empty, cancelLabel: string.Empty);
            dialog.AddText(body);
            for (int i = buttons.Length - 1; i >= 0; i--)
            {
                int index = i;
                Button button = dialog.AddExtraButton(buttons[i].Label, buttons[i].Destructive, () =>
                {
                    result = index;
                    dialog.Close();
                });
                if (buttons[i].Primary)
                    button.Classes.Add("primary");
                if (i == buttons.Length - 1)
                    button.IsDefault = true;
            }
            await dialog.ShowDialog(owner);
            return result;
        }

        public static bool IsWebAddress(string? text)
            => Uri.TryCreate(text?.Trim(), UriKind.Absolute, out Uri? uri) &&
               (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeFile);
    }
}
