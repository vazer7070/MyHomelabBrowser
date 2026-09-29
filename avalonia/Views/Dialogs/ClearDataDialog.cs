using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using PommeBrowser.Engine;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace PommeBrowser.Views.Dialogs
{
    /// <summary>Effacement des données de navigation (Ctrl+Maj+Suppr) : période, historique, cookies, cache, téléchargements.</summary>
    public static class ClearDataDialog
    {
        public static async Task ShowAsync(MainWindow window)
        {
            BrowserApp app = window.App;
            var dialog = new FormDialog(Tr("Effacer les données de navigation"), Tr("Effacer les données"), destructive: true);
            var periods = new (string Label, double Hours)[]
            {
                (Tr("Dernière heure"), 1),
                (Tr("Dernières 24 heures"), 24),
                (Tr("7 derniers jours"), 24 * 7),
                (Tr("4 dernières semaines"), 24 * 28),
                (Tr("Depuis toujours"), 0)
            };
            ComboBox period = dialog.AddChoice(Tr("Période"), periods.Select(p => p.Label).ToList(), 0);
            CheckBox history = dialog.AddCheck(Tr("Historique de navigation"), true);
            CheckBox cookies = dialog.AddCheck(Tr("Cookies et données des sites (vous serez déconnecté de la plupart des sites)"), true);
            CheckBox cache = dialog.AddCheck(Tr("Images et fichiers en cache"), true);
            CheckBox downloads = dialog.AddCheck(Tr("Historique des téléchargements (les fichiers restent sur le disque)"), false);
            dialog.AddText(Tr("Le coffre des mots de passe PommeBrowser n’est pas concerné."), hint: true);

            void Validate() => dialog.SetConfirmEnabled(history.IsChecked == true || cookies.IsChecked == true || cache.IsChecked == true || downloads.IsChecked == true);
            foreach (CheckBox box in new[] { history, cookies, cache, downloads })
                box.IsCheckedChanged += (_, _) => Validate();

            dialog.Submit = async () =>
            {
                double hours = periods[Math.Max(0, period.SelectedIndex)].Hours;
                DateTime since = hours <= 0 ? DateTime.MinValue : DateTime.Now.AddHours(-hours);
                try
                {
                    if (cookies.IsChecked == true || cache.IsChecked == true)
                        await EngineHost.ClearBrowsingDataAsync(hours <= 0 ? null : TimeSpan.FromHours(hours), cookies.IsChecked == true, cache.IsChecked == true);
                    if (history.IsChecked == true)
                        app.History.RemoveSince(since);
                    if (downloads.IsChecked == true)
                        app.Downloads.ClearFinished();
                }
                catch (Exception ex)
                {
                    return Tr("Effacement incomplet : {0}", ex.Message);
                }
                return null;
            };

            if (await dialog.ShowAsync(window))
                window.ShowToast(Tr("Les données sélectionnées ont été supprimées."));
        }
    }
}
