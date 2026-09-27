using MyHomelabBrowser.classes.Flash;
using MyHomelabBrowser.controles;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Controls;
using System.Windows.Threading;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace MyHomelabBrowser
{
    public partial class MainWindow
    {
        // Mode Legacy : Basilisk lancé à part puis incrusté dans l'onglet (Win32).

        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        const int SW_HIDE = 0;

        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool IsWindowVisible(IntPtr hWnd);

        private void HideLegacyWindowsExcept(WebTabContent? keep)
        {
            foreach (var item in Tabs.Items)
            {
                if (item is not TabItem ti) continue;
                if (ti.Tag is not WebTabContent wt) continue;

                // ✅ Ne pas toucher à l'onglet actif legacy
                if (keep != null && ReferenceEquals(wt, keep))
                    continue;

                // ✅ On cache uniquement les fenêtres legacy des autres onglets
                if (wt.LegacyHwnd != IntPtr.Zero)
                    ShowWindow(wt.LegacyHwnd, SW_HIDE);

                if (wt.LegacyTopHwnd != IntPtr.Zero && wt.LegacyTopHwnd == wt.LegacyHwnd)
                    ShowWindow(wt.LegacyTopHwnd, SW_HIDE);
            }
        }

        private IntPtr FindBestEmbedChild(IntPtr top)
        {
            IntPtr best = IntPtr.Zero;
            long bestArea = -1;

            void Scan(IntPtr parent)
            {
                EnumChildWindows(parent, (h, _) =>
                {
                    if (!IsWindowVisible(h)) return true;

                    if (GetWindowRect(h, out var rc))
                    {
                        int w = rc.Right - rc.Left;
                        int hgt = rc.Bottom - rc.Top;

                        if (w > 10 && hgt > 10)
                        {
                            long area = (long)w * hgt;

                            if (area > bestArea)
                            {
                                bestArea = area;
                                best = h;
                            }
                        }
                    }

                    // ✅ descend dans tous les enfants
                    Scan(h);

                    return true;
                }, IntPtr.Zero);
            }

            Scan(top);
            return best;
        }

        private const int GW_OWNER = 4;

        [DllImport("user32.dll")]
        private static extern int GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr hWnd, int uCmd);

        [DllImport("user32.dll")] static extern IntPtr SetFocus(IntPtr hWnd);

        [DllImport("user32.dll")] static extern IntPtr SetActiveWindow(IntPtr hWnd);

        [DllImport("user32.dll")] static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();

        void FocusLegacyEmbedded(IntPtr embedHwnd, IntPtr topHwnd)
        {
            if (embedHwnd == IntPtr.Zero) return;

            uint thisThread = GetCurrentThreadId();
            uint targetThread = (uint)GetWindowThreadProcessId(embedHwnd, out _);

            // ✅ indispensable dans beaucoup de cas (threads différents)
            AttachThreadInput(thisThread, targetThread, true);
            try
            {
                // Top utile pour activation; focus sur embed pour clavier
                if (topHwnd != IntPtr.Zero)
                    SetActiveWindow(topHwnd);

                SetFocus(embedHwnd);
            }
            finally
            {
                AttachThreadInput(thisThread, targetThread, false);
            }
        }

        async Task<bool> LaunchLegacyIntoInternalTabAsync(WebTabContent content, Uri uri, BrowserTabHeader header)
        {
            if (content.IsLegacyLaunching)
                return false;

            content.PendingLegacyUri = null;
            content.IsLegacyLaunching = true;
            content.IsLegacyExternal = true;
            content.LegacyLastError = null;
            content.LegacyUrl = uri.AbsoluteUri;

            await Dispatcher.InvokeAsync(() =>
            {
                header.SetTitle("Legacy Flash");
                content.FlashMode = FlashMode.Legacy;
                SyncWebHostWithSelection();
            });

            if (!_legacyLauncher.CanLaunch())
                return await FailLegacyLaunchAsync(content, Tr("Basilisk n’est pas configuré ou chemin invalide."));

            try
            {
                // Un nouvel essai ne doit pas laisser tourner l'instance précédente :
                // elle resterait orpheline et garderait son profil verrouillé.
                await StopLegacyProcessAsync(content);
                content.LegacyHwnd = IntPtr.Zero;
                content.LegacyTopHwnd = IntPtr.Zero;
                content.LegacyEmbedHwnd = IntPtr.Zero;
                content.LegacyPid = null;

                content.LegacyProfileLease = LegacyProfileManager.CreateLease(uri.Host, content.IsPrivate);

                LegacyProcess? process = await Task.Run(() => _legacyLauncher.Launch(
                    uri.AbsoluteUri,
                    content.LegacyProfileLease.ProfilePath,
                    content.IsPrivate));
                content.LegacyProc = process;

                if (process == null)
                    return await FailLegacyLaunchAsync(content, Tr("Basilisk n’est pas configuré ou chemin invalide."));

                // Attendre la fenêtre principale de Basilisk (quel que soit le processus du job qui la porte).
                var (top, realPid) = await WaitForBasiliskWindowAsync(process, timeoutMs: 20000, cancelled: () => content.IsClosed);

                // Onglet fermé pendant le lancement : on n'embarque rien.
                if (content.IsClosed)
                {
                    content.IsLegacyLaunching = false;
                    await StopLegacyProcessAsync(content);
                    return false;
                }

                if (top == IntPtr.Zero)
                {
                    string? dialog = process.FindDialogMessage();
                    string reason = dialog != null
                        ? Tr("Basilisk a affiché un message : {0}", dialog)
                        : process.IsRunning
                            ? Tr("Fenêtre Basilisk introuvable (timeout).")
                            : Tr("Basilisk s’est fermé au démarrage. Vérifiez le chemin de Basilisk et qu’il peut s’ouvrir seul.");
                    await StopLegacyProcessAsync(content);
                    return await FailLegacyLaunchAsync(content, reason);
                }

                content.LegacyPid = realPid;
                content.LegacyTopHwnd = top;
                process.MainWindow = top;

                // Enfant intégré (focus/clavier), ou la fenêtre entière à défaut.
                var embed = FindBestEmbedChild(top);
                var target = embed != IntPtr.Zero ? embed : top;

                content.LegacyHwnd = target;
                content.LegacyEmbedHwnd = target;

                // Tout ce qui touche WPF passe par le Dispatcher.
                await Dispatcher.InvokeAsync(() =>
                {
                    content.LegacyHost ??= new MyHomelabBrowser.controles.ExternalWindowDock();
                    content.LegacyHost.SetTopLevel(top);
                    content.LegacyHost.Bind(target);
                });

                content.IsLegacyLaunching = false;
                content.IsLegacyExternal = true;
                content.LegacyLastError = null;

                await Dispatcher.InvokeAsync(() =>
                {
                    SyncWebHostWithSelection();

                    content.LegacyHost?.ShowDock();
                    content.LegacyHost?.UpdateDockPosition();

                    // Onglet déjà quitté pendant le lancement : Basilisk passe en arrière-plan.
                    content.LegacyProc?.SetBackground(!IsSelectedContent(content));
                }, DispatcherPriority.Loaded);

                return true;
            }
            catch (Exception ex)
            {
                await StopLegacyProcessAsync(content);
                FlashDbg("[Legacy] " + ex);
                return await FailLegacyLaunchAsync(content, Tr("Erreur au lancement de Basilisk :\n") + ex.Message);
            }
        }

        async Task<bool> FailLegacyLaunchAsync(WebTabContent content, string message)
        {
            content.LegacyProfileLease?.Dispose();
            content.LegacyProfileLease = null;
            content.IsLegacyLaunching = false;
            content.IsLegacyExternal = true;
            content.LegacyLastError = message;

            await Dispatcher.InvokeAsync(SyncWebHostWithSelection);
            return false;
        }

        bool IsSelectedContent(WebTabContent content)
            => Tabs.SelectedItem is TabItem { Tag: WebTabContent selected } && ReferenceEquals(selected, content);

        /// <summary>
        /// Onglet Legacy restauré sans avoir été affiché : Basilisk n'est lancé qu'à la
        /// première ouverture de l'onglet (démarrage plus rapide, pas de rafale de processus).
        /// </summary>
        void LaunchPendingLegacyIfNeeded(TabItem tab, WebTabContent content)
        {
            if (content.PendingLegacyUri is not { } uri ||
                content.IsLegacyLaunching ||
                content.LegacyProc != null ||
                tab.Header is not BrowserTabHeader header)
            {
                return;
            }

            _ = LaunchLegacyIntoInternalTabAsync(content, uri, header);
        }

        /// <summary>
        /// Attend la fenêtre principale de Basilisk. L'énumération Win32 tourne hors du
        /// thread UI ; si aucune fenêtre de navigateur n'apparaît, toute fenêtre visible
        /// du job est acceptée après quelques secondes (dérivés de Basilisk).
        /// </summary>
        private static async Task<(IntPtr top, int realPid)> WaitForBasiliskWindowAsync(
            LegacyProcess process,
            int timeoutMs,
            Func<bool>? cancelled = null)
        {
            var sw = Stopwatch.StartNew();

            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (cancelled?.Invoke() == true)
                    break;

                bool anyWindow = sw.ElapsedMilliseconds > 6000;
                var found = await Task.Run(() => process.FindMainWindow(anyWindow));
                if (found.Window != IntPtr.Zero)
                    return found;

                // Boîte d'erreur de Basilisk ou de son lanceur portable : inutile d'attendre.
                if (sw.ElapsedMilliseconds > 1500 && await Task.Run(process.FindDialogMessage) != null)
                    break;

                // Tous les processus du job sont terminés : inutile d'attendre davantage.
                if (!process.IsRunning)
                    break;

                await Task.Delay(120);
            }

            return (IntPtr.Zero, 0);
        }

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);
    }
}
