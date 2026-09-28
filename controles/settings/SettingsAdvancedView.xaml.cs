using MyHomelabBrowser.classes.Flash;
using System.Windows;
using System.Windows.Controls;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace MyHomelabBrowser.controles.settings
{
    public partial class SettingsAdvancedView : UserControl
    {
        public SettingsAdvancedView()
        {
            InitializeComponent();
            Loaded += SettingsAdvancedView_Loaded;
        }

        private void SettingsAdvancedView_Loaded(object sender, RoutedEventArgs e)
        {
            if (RuffleAssetService.HasLocalAssets)
            {
                RuffleRuntimeStatusText.Text =
                    Tr("Ruffle {0} est embarqué localement. ", RuffleAssetService.InstalledVersion ?? RuffleAssetService.PinnedVersion) +
                    Tr("Le navigateur ne dépend pas d’un CDN pour exécuter Flash.");
            }
            else
            {
                RuffleRuntimeStatusText.Text =
                    Tr("Les fichiers locaux Ruffle {0} sont absents. ", RuffleAssetService.PinnedVersion) +
                    Tr("Les contenus Flash ne peuvent pas être lus avec Ruffle : réinstallez PommeBrowser, ou recompilez-le avec un accès à Internet.");
            }
        }
    }
}
