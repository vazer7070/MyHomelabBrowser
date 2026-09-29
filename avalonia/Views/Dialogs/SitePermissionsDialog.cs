using System;
using System.Linq;
using System.Threading.Tasks;
using MyHomelabBrowser.classes.Security;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace PommeBrowser.Views.Dialogs
{
    /// <summary>Autorisations mémorisées par site (position, caméra, HTTP…), avec possibilité de les oublier.</summary>
    public static class SitePermissionsDialog
    {
        public static Task ShowAsync(MainWindow window)
        {
            BrowserApp app = window.App;
            var dialog = new ListDialog(Tr("Autorisations des sites"),
                Tr("Réponses mémorisées pour chaque site. Oubliez-en une pour que la question soit posée à nouveau."),
                Tr("Aucune autorisation mémorisée."),
                d => app.SiteSecurity.All
                    .OrderBy(x => x.Site, StringComparer.OrdinalIgnoreCase)
                    .Select(decision => d.Row(decision.Site,
                        PermissionPrompt.KindLabel(decision.Kind) + " · " + (decision.Allowed ? Tr("autorisé") : Tr("bloqué")) + " · " + decision.DecidedAt.ToString("g", Culture),
                        Tr("Oublier"),
                        () => app.SiteSecurity.Remove(decision))));
            return dialog.ShowAsync(window);
        }
    }
}
