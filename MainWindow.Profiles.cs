using Microsoft.Web.WebView2.Core;
using MyHomelabBrowser.classes.Profiles;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Controls;

namespace MyHomelabBrowser
{
    public partial class MainWindow
    {
        // ---------------------------
        // Données WebView2 des profils
        // ---------------------------

        /// <summary>
        /// Les cookies et sessions d'un profil vivent dans un dossier WebView2 nommé d'après
        /// lui : sans ce suivi, un profil renommé perdait toutes ses connexions.
        /// </summary>
        void OnProfileRenamed(string oldName, string newName)
        {
            string oldId = WebViewProfileData.NormalizeId(oldName);
            string newId = WebViewProfileData.NormalizeId(newName);
            if (oldId == newId)
                return;

            // L'environnement déjà ouvert continue de servir le profil sous son nouveau nom ;
            // son dossier, verrouillé, sera déplacé au prochain démarrage.
            bool inUse = _envByProfile.Remove(oldId, out CoreWebView2Environment? environment);
            if (environment != null)
                _envByProfile[newId] = environment;

            WebViewProfileData.MoveOrSchedule(oldId, newId, inUse);
        }

        /// <summary>
        /// Supprimer un profil laissait ses cookies et son cache sur le disque.
        /// </summary>
        async void OnProfileDeleted(string name)
        {
            string id = WebViewProfileData.NormalizeId(name);
            bool inUse = _envByProfile.Remove(id, out CoreWebView2Environment? environment);

            if (environment != null)
                await ClearBrowsingDataForEnvironmentAsync(environment);

            WebViewProfileData.DeleteOrSchedule(id, inUse);
        }

        async Task ClearBrowsingDataForEnvironmentAsync(CoreWebView2Environment environment)
        {
            // Effacement immédiat via un onglet encore ouvert : le dossier, lui, n'est
            // supprimé qu'une fois libéré par le navigateur.
            foreach (TabItem tab in Tabs.Items.OfType<TabItem>().ToList())
            {
                if (tab.Tag is WebTabContent { Web.CoreWebView2: CoreWebView2 core } &&
                    ReferenceEquals(core.Environment, environment))
                {
                    try
                    {
                        await core.Profile.ClearBrowsingDataAsync();
                    }
                    catch
                    {
                        // Le dossier sera de toute façon supprimé au prochain démarrage.
                    }
                    return;
                }
            }
        }
    }
}
