using MyHomelabBrowser.classes;
using System;
using System.Threading.Tasks;
using System.Windows;

namespace MyHomelabBrowser
{
    public partial class MainWindow
    {
        /// <summary>
        /// Vérification silencieuse au démarrage. Auparavant la mise à jour était
        /// téléchargée puis appliquée avec redémarrage forcé 1,5 s après le lancement,
        /// sans sauvegarder les onglets. Désormais elle est proposée par une notification
        /// et, à défaut, installée à la fermeture du navigateur.
        /// </summary>
        private void ScheduleBackgroundUpdateCheck()
        {
            if (_updates == null || !_updates.IsInstalled)
                return;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(8));

                    var info = await _updates.CheckAsync();
                    if (info == null)
                        return;

                    await _updates.DownloadAsync(info);
                    _downloadedUpdateInfo = info;

                    // Installée automatiquement à la fermeture si l'utilisateur ne redémarre pas.
                    try { _updates.ApplyOnExit(info); } catch { }

                    string version = info.TargetFullRelease.Version.ToString();
                    await Dispatcher.InvokeAsync(() => ShowToast(
                        "Mise à jour prête",
                        $"PommeBrowser {version} sera installé à la fermeture.",
                        ToastKind.Info,
                        "Redémarrer maintenant",
                        RestartToApplyUpdate,
                        TimeSpan.FromSeconds(15)));
                }
                catch (Exception ex)
                {
                    RuntimeLogBuffer.Append("[Updates] " + ex.Message);
                }
            });
        }

        private void RestartToApplyUpdate()
        {
            if (_updates == null || _downloadedUpdateInfo == null)
                return;

            SaveSessionForUpdateRestart();
            FlushPersistentState();
            _updates.ApplyAndRestart(_downloadedUpdateInfo);
        }

        private void WireUpdateActions(SettingsView view)
        {
            view.SetCurrentVersion(AppVersion.Current);
            view.SetLatestVersion("(non vérifiée)");
            view.SetUpdateStatus("Prêt");
            view.SetUpdateBusy(false);
            view.SetChangelogAvailable(true);
            view.SetInstallAvailable(_downloadedUpdateInfo != null);

            view.CheckUpdatesRequested += async () =>
            {
                if (_updates == null || _isUpdateCheckRunning)
                    return;

                if (!_updates.IsInstalled)
                {
                    view.SetUpdateStatus("Mises à jour indisponibles hors version installée.");
                    return;
                }

                _isUpdateCheckRunning = true;
                view.SetUpdateBusy(true);
                view.SetUpdateStatus("Recherche de mise à jour…");
                view.SetLatestVersion("(en cours…)");
                view.SetInstallAvailable(false);
                _pendingUpdateInfo = null;

                try
                {
                    var info = await _updates.CheckAsync();

                    if (info == null)
                    {
                        view.SetLatestVersion(AppVersion.Current);
                        view.SetUpdateStatus("PommeBrowser est à jour.");
                        return;
                    }

                    _pendingUpdateInfo = info;
                    view.SetLatestVersion(info.TargetFullRelease.Version.ToString());
                    view.SetUpdateStatus("Mise à jour disponible.");
                    view.SetInstallAvailable(true);
                }
                catch (Exception ex)
                {
                    view.SetUpdateStatus("Erreur pendant la vérification.");
                    RuntimeLogBuffer.Append("[Updates] " + ex.Message);
                    ShowToast("Mise à jour", "La vérification a échoué : " + ex.Message, ToastKind.Warning);
                }
                finally
                {
                    _isUpdateCheckRunning = false;
                    view.SetUpdateBusy(false);
                }
            };

            view.InstallUpdateRequested += async () =>
            {
                var info = _pendingUpdateInfo ?? _downloadedUpdateInfo;
                if (_updates == null || info == null || _isUpdateCheckRunning)
                    return;

                _isUpdateCheckRunning = true;
                view.SetUpdateBusy(true);
                view.SetInstallAvailable(false);
                view.ShowUpdateProgress(true);
                view.SetUpdateStatus("Téléchargement…");

                try
                {
                    if (!ReferenceEquals(info, _downloadedUpdateInfo))
                    {
                        await _updates.DownloadAsync(info, percent =>
                            Dispatcher.BeginInvoke(() => view.SetUpdateProgress(percent)));
                    }

                    view.SetUpdateStatus("Installation et redémarrage…");

                    // Onglets et historique sauvegardés avant que Velopack ne ferme l'application.
                    SaveSessionForUpdateRestart();
                    FlushPersistentState();
                    _updates.ApplyAndRestart(info);
                }
                catch (Exception ex)
                {
                    view.SetUpdateStatus("Erreur pendant l'installation.");
                    view.ShowUpdateProgress(false);
                    MessageBox.Show(this, "Erreur de mise à jour :\n" + ex.Message, "Mise à jour",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                finally
                {
                    _isUpdateCheckRunning = false;
                    view.SetUpdateBusy(false);
                }
            };

            view.ChangelogRequested += () =>
            {
                var win = new ChangelogWindow("Historique des versions", _remoteChangelogJson ?? "{}")
                {
                    Owner = this
                };

                win.ShowDialog();
            };
        }
    }
}
