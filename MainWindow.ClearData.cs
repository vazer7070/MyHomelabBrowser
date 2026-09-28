using Microsoft.Web.WebView2.Core;
using MyHomelabBrowser.classes;
using MyHomelabBrowser.controles;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace MyHomelabBrowser
{
    public partial class MainWindow
    {
        // ---------------------------
        // Effacement des données de navigation (Ctrl+Maj+Suppr)
        // ---------------------------
        async void OpenClearBrowsingData()
        {
            var dialog = new ClearBrowsingDataDialog();
            if (!dialog.ShowFor(this))
                return;

            try
            {
                await ClearBrowsingDataAsync(dialog);
                ShowToast(Tr("Données effacées"), Tr("Les données sélectionnées ont été supprimées."), ToastKind.Success);
            }
            catch (Exception ex)
            {
                ShowToast(Tr("Effacement incomplet"), ex.Message, ToastKind.Warning);
            }
        }

        async Task ClearBrowsingDataAsync(ClearBrowsingDataDialog choice)
        {
            DateTime since = choice.Since;

            CoreWebView2BrowsingDataKinds kinds = 0;
            if (choice.ClearHistory)
                kinds |= CoreWebView2BrowsingDataKinds.BrowsingHistory;
            if (choice.ClearCookies)
                kinds |= CoreWebView2BrowsingDataKinds.Cookies | CoreWebView2BrowsingDataKinds.AllDomStorage;
            if (choice.ClearCache)
                kinds |= CoreWebView2BrowsingDataKinds.DiskCache | CoreWebView2BrowsingDataKinds.CacheStorage;
            if (choice.ClearDownloads)
                kinds |= CoreWebView2BrowsingDataKinds.DownloadHistory;
            if (choice.ClearAutofill)
                kinds |= CoreWebView2BrowsingDataKinds.GeneralAutofill | CoreWebView2BrowsingDataKinds.PasswordAutosave;

            if (kinds != 0)
            {
                await WithCurrentWebProfileAsync(profile => since == DateTime.MinValue
                    ? profile.ClearBrowsingDataAsync(kinds)
                    : profile.ClearBrowsingDataAsync(kinds, since, DateTime.Now.AddMinutes(1)));
            }

            if (choice.ClearHistory)
                RemoveHistorySince(since);

            if (choice.ClearDownloads)
            {
                if (since == DateTime.MinValue)
                    DownloadManager.Instance.RemoveFinished();
                else
                    DownloadManager.Instance.RemoveFinishedSince(since);
            }
        }

        /// <summary>
        /// Exécute une action sur le profil WebView2 du profil PommeBrowser courant : via un
        /// onglet ouvert s'il y en a un, sinon via un contrôleur temporaire invisible.
        /// </summary>
        async Task WithCurrentWebProfileAsync(Func<CoreWebView2Profile, Task> action)
        {
            CoreWebView2Environment environment = await GetEnvironmentForCurrentProfileAsync();

            foreach (TabItem tab in Tabs.Items.OfType<TabItem>())
            {
                if (tab.Tag is WebTabContent { IsPrivate: false, Web.CoreWebView2: CoreWebView2 core } &&
                    ReferenceEquals(core.Environment, environment))
                {
                    await action(core.Profile);
                    return;
                }
            }

            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            CoreWebView2Controller controller = await environment.CreateCoreWebView2ControllerAsync(hwnd);
            try
            {
                controller.IsVisible = false;
                await action(controller.CoreWebView2.Profile);
            }
            finally
            {
                controller.Close();
            }
        }
    }
}
