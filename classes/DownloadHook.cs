using System;
using System.IO;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using MyHomelabBrowser.classes.Profiles;
using MyHomelabBrowser.classes.Security;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace MyHomelabBrowser.classes
{
    public static class DownloadHook
    {
        public static void Attach(WebView2 wv, bool isPrivate)
        {
            if (wv == null) return;

            if (wv.CoreWebView2 != null)
            {
                AttachToCore(wv.CoreWebView2, isPrivate);
                return;
            }

            wv.CoreWebView2InitializationCompleted += (_, e) =>
            {
                if (!e.IsSuccess) return;
                if (wv.CoreWebView2 != null)
                    AttachToCore(wv.CoreWebView2, isPrivate);
            };
        }

        private static void AttachToCore(CoreWebView2 core, bool isPrivate)
        {
            // La confiance privée est capturée au moment où le WebView du profil est initialisé.
            BrowserCertificateTrustHost.Current.Attach(core, AppDataContext.Root);

            // ⚠️ méthode NOMMÉE → détachable
            core.DownloadStarting -= Core_DownloadStarting;
            core.DownloadStarting += Core_DownloadStarting;

            void Core_DownloadStarting(
                object? sender,
                CoreWebView2DownloadStartingEventArgs e)
            {
                HandleDownloadStarting(e, isPrivate);
            }
        }

        private static void HandleDownloadStarting(
            CoreWebView2DownloadStartingEventArgs e,
            bool isPrivate)
        {
            var def = e.GetDeferral();

            try
            {
                var op = e.DownloadOperation;

                var folder = DownloadManager.Instance.DownloadFolder;
                try
                {
                    Directory.CreateDirectory(folder);
                }
                catch
                {
                    // Dossier configuré introuvable (disque retiré, partage réseau absent…).
                    folder = DownloadManager.DefaultDownloadFolder;
                    Directory.CreateDirectory(folder);
                }

                // ✅ SEULE SOURCE FIABLE DU NOM
                string fileName = "download.bin";

                if (!string.IsNullOrWhiteSpace(e.ResultFilePath))
                {
                    fileName = Path.GetFileName(e.ResultFilePath);
                }

                var finalPath = MakeUniquePath(Path.Combine(folder, fileName));

                e.ResultFilePath = finalPath;
                e.Handled = true;

                var item = new DownloadItem
                {
                    FileName = fileName,
                    Url = op.Uri,
                    ResultFilePath = finalPath,
                    StartedAt = DateTime.Now,
                    State = DownloadUiState.InProgress,
                    Operation = op,
                    IsPrivate = isPrivate,
                    TotalBytes = op.TotalBytesToReceive >= 0
                        ? op.TotalBytesToReceive
                        : null
                };

                App.Current.Dispatcher.Invoke(() =>
                {
                    DownloadManager.Instance.Items.Insert(0, item);

                    if (!item.IsPrivate)
                        DownloadManager.Instance.SaveHistory();

                    if (App.Current.MainWindow is MainWindow mw)
                        mw.UpdateDownloadsBadge();
                });

                op.BytesReceivedChanged += (_, _) =>
                {
                    App.Current.Dispatcher.Invoke(() =>
                    {
                        item.ReceivedBytes = (long)op.BytesReceived;
                        if (op.TotalBytesToReceive >= 0)
                            item.TotalBytes = op.TotalBytesToReceive;
                    });
                };

                op.StateChanged += (_, _) =>
                {
                    App.Current.Dispatcher.Invoke(() =>
                    {
                        switch (op.State)
                        {
                            case CoreWebView2DownloadState.InProgress:
                                item.State = DownloadUiState.InProgress;
                                item.IsPaused = false;
                                break;

                            case CoreWebView2DownloadState.Completed:
                                item.State = DownloadUiState.Completed;
                                item.FinishedAt = DateTime.Now;

                                if (item.TotalBytes.HasValue)
                                    item.ReceivedBytes = (long)item.TotalBytes.Value;

                                if (!item.IsPrivate)
                                    DownloadManager.Instance.SaveHistory();

                                if (App.Current.MainWindow is MainWindow mw)
                                    mw.ShowToast(
                                        Tr("Téléchargement terminé"),
                                        item.FileName,
                                        item
                                    );
                                break;

                            case CoreWebView2DownloadState.Interrupted:
                                item.InterruptReason = op.InterruptReason;
                                item.CanResume = op.CanResume;

                                // WebView2 signale une pause comme une interruption :
                                // l'élément reste « en cours » et propose Reprendre.
                                bool paused = !item.CancelRequested &&
                                    (item.IsPaused || op.InterruptReason == CoreWebView2DownloadInterruptReason.UserPaused);

                                if (paused)
                                {
                                    item.IsPaused = true;
                                    break;
                                }

                                item.State = item.CancelRequested ||
                                             op.InterruptReason == CoreWebView2DownloadInterruptReason.UserCanceled
                                    ? DownloadUiState.Cancelled
                                    : DownloadUiState.Interrupted;

                                if (!item.IsPrivate)
                                    DownloadManager.Instance.SaveHistory();
                                break;
                        }

                        if (App.Current.MainWindow is MainWindow mw2)
                            mw2.UpdateDownloadsBadge();
                    });
                };
            }
            finally
            {
                def.Complete();
            }
        }

        private static string MakeUniquePath(string path)
        {
            if (!File.Exists(path)) return path;

            var dir = Path.GetDirectoryName(path)!;
            var name = Path.GetFileNameWithoutExtension(path);
            var ext = Path.GetExtension(path);

            for (int i = 1; i < 9999; i++)
            {
                var p = Path.Combine(dir, $"{name} ({i}){ext}");
                if (!File.Exists(p)) return p;
            }

            return Path.Combine(dir, $"{name} ({Guid.NewGuid():N}){ext}");
        }
    }
}
