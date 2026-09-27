using System.Windows;
using System.Windows.Input;

namespace MyHomelabBrowser.controles
{
    /// <summary>
    /// Base des boîtes de dialogue au style PommeBrowser (DialogWindowStyle) :
    /// déplacement à la souris et fermeture par Échap ou par le bouton du titre.
    /// </summary>
    public class DialogWindow : Window
    {
        public DialogWindow()
        {
            SetResourceReference(StyleProperty, "DialogWindowStyle");
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            CommandBindings.Add(new CommandBinding(ApplicationCommands.Close, (_, _) => Close()));

            MouseLeftButtonDown += (_, e) =>
            {
                if (e.ButtonState == MouseButtonState.Pressed && e.OriginalSource is not System.Windows.Controls.Primitives.TextBoxBase)
                {
                    try { DragMove(); } catch { }
                }
            };
        }

        /// <summary>
        /// Affiche la boîte au-dessus de <paramref name="owner"/> et renvoie vrai si elle a été validée.
        /// </summary>
        public bool ShowFor(Window? owner)
        {
            if (owner != null && owner.IsVisible)
                Owner = owner;
            else
                WindowStartupLocation = WindowStartupLocation.CenterScreen;

            return ShowDialog() == true;
        }
    }
}
