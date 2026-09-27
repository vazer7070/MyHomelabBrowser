using MyHomelabBrowser.classes.Flash;
using System.Windows;
using System.Windows.Controls;

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
                    $"Ruffle {RuffleAssetService.PinnedVersion} est embarqué localement. " +
                    "Le navigateur ne dépend pas d’un CDN pour exécuter Flash.";
            }
            else
            {
                RuffleRuntimeStatusText.Text =
                    $"Les fichiers locaux Ruffle {RuffleAssetService.PinnedVersion} sont absents. " +
                    "Le navigateur utilisera temporairement le CDN épinglé. Lance " +
                    "install-ruffle-assets.ps1 avant de publier l’application.";
            }
        }
    }
}
