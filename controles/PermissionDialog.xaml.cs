using System.Windows;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace MyHomelabBrowser.controles
{
    /// <summary>
    /// Demande d'un site (caméra, position…). Fermer la boîte ou Échap = bloquer.
    /// </summary>
    public partial class PermissionDialog : DialogWindow
    {
        public bool Allowed { get; private set; }
        public bool Remember => RememberBox.IsChecked == true;

        public PermissionDialog(string host, string request, bool isPrivate)
        {
            InitializeComponent();

            SiteText.Text = host;
            RequestText.Text = Tr("Ce site souhaite {0}.", request);

            if (isPrivate)
                RememberBox.Content = Tr("Retenir ce choix jusqu’à la fin de la navigation privée");
        }

        private void Allow_Click(object sender, RoutedEventArgs e)
        {
            Allowed = true;
            DialogResult = true;
        }
    }
}
