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
                    Tr("Ruffle {0} est embarqué localement. ", RuffleAssetService.PinnedVersion) +
                    Tr("Le navigateur ne dépend pas d’un CDN pour exécuter Flash.");
            }
            else
            {
                RuffleRuntimeStatusText.Text =
                    Tr("Les fichiers locaux Ruffle {0} sont absents. ", RuffleAssetService.PinnedVersion) +
                    Tr("Le navigateur utilisera temporairement le CDN épinglé. Lance install-ruffle-assets.ps1 avant de publier l’application.");
            }
        }
    }
}
