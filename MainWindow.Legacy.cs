using MyHomelabBrowser.classes.Flash;
using MyHomelabBrowser.controles;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Controls;
using System.Windows.Threading;

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

            await Task.Delay(50);

            if (!_legacyLauncher.CanLaunch())
            {
                content.IsLegacyLaunching = false;
                content.IsLegacyExternal = true;
                content.LegacyLastError = "Basilisk n’est pas configuré ou chemin invalide.";

                await Dispatcher.InvokeAsync(SyncWebHostWithSelection);
                return false;
            }

            try
            {
                // Un nouvel essai ne doit pas laisser tourner l'instance précédente :
                // elle resterait orpheline et verrouillerait le profil Basilisk.
                await StopLegacyProcessAsync(content);
                content.LegacyHwnd = IntPtr.Zero;
                content.LegacyTopHwnd = IntPtr.Zero;
                content.LegacyEmbedHwnd = IntPtr.Zero;
                content.LegacyPid = null;

                content.LegacyProfileLease = LegacyProfileManager.CreateLease(uri.Host, content.IsPrivate);

                var p = _legacyLauncher.Launch(
                    uri.AbsoluteUri,
                    content.LegacyProfileLease.ProfilePath);
                content.LegacyProc = p;

                if (p == null)
                {
                    content.LegacyProfileLease?.Dispose();
                    content.LegacyProfileLease = null;
                    content.IsLegacyLaunching = false;
                    content.IsLegacyExternal = true;
                    content.LegacyLastError = "Process Basilisk non lancé (Launch() a retourné null).";

                    await Dispatcher.InvokeAsync(SyncWebHostWithSelection);
                    return false;
                }

                // Attendre la fenêtre principale de Basilisk.
                var (top, realPid) = await WaitForAnyTopWindowFromProcessFamilyAsync(p, timeoutMs: 15000, cancelled: () => content.IsClosed);

                // Onglet fermé pendant le lancement : on n'embarque rien.
                if (content.IsClosed)
                {
                    content.IsLegacyLaunching = false;
                    await StopLegacyProcessAsync(content);
                    return false;
                }

                if (top == IntPtr.Zero)
                {
                    try { p.Kill(entireProcessTree: true); } catch { }
                    content.LegacyProfileLease?.Dispose();
                    content.LegacyProfileLease = null;
                    content.IsLegacyLaunching = false;
                    content.IsLegacyExternal = true;
                    content.LegacyLastError = "Fenêtre Basilisk introuvable (timeout).";

                    await Dispatcher.InvokeAsync(SyncWebHostWithSelection);
                    return false;
                }

                // ✅ handles (ok hors UI)
                content.LegacyPid = realPid;
                content.LegacyTopHwnd = top;

                // ✅ enfant embed (focus/clavier)
                var embed = FindBestEmbedChild(top);
                var target = embed != IntPtr.Zero ? embed : top;

                content.LegacyHwnd = target;
                content.LegacyEmbedHwnd = target;

                // ✅ dock host : création OBLIGATOIRE sur UI thread STA
                if (content.LegacyHost == null)
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        content.LegacyHost = new MyHomelabBrowser.controles.ExternalWindowDock();
                    });
                }

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
                }, DispatcherPriority.Loaded);

                return true;
            }
            catch (Exception ex)
            {
                try { content.LegacyProc?.Kill(entireProcessTree: true); } catch { }
                content.LegacyProc = null;
                content.LegacyProfileLease?.Dispose();
                content.LegacyProfileLease = null;
                content.IsLegacyLaunching = false;
                content.IsLegacyExternal = true;
                content.LegacyLastError = "Erreur au lancement de Basilisk :\n" + ex;

                await Dispatcher.InvokeAsync(SyncWebHostWithSelection);
                return false;
            }
        }

        /// <summary>
        /// PID du processus lancé et de tous ses descendants (Basilisk relance souvent
        /// un processus enfant qui porte la vraie fenêtre). Un seul instantané système.
        /// </summary>
        private static HashSet<int> GetProcessFamilyPids(int rootPid)
        {
            var family = new HashSet<int> { rootPid };
            var childrenByParent = new Dictionary<int, List<int>>();

            IntPtr snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if (snapshot == IntPtr.Zero || snapshot == (IntPtr)(-1))
                return family;

            try
            {
                var pe = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32)) };
                if (!Process32First(snapshot, ref pe))
                    return family;

                do
                {
                    int parent = (int)pe.th32ParentProcessID;
                    if (!childrenByParent.TryGetValue(parent, out var list))
                    {
                        list = new List<int>();
                        childrenByParent[parent] = list;
                    }
                    list.Add((int)pe.th32ProcessID);
                    pe.dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32));
                }
                while (Process32Next(snapshot, ref pe));
            }
            finally
            {
                CloseHandle(snapshot);
            }

            var queue = new Queue<int>();
            queue.Enqueue(rootPid);
            while (queue.Count > 0)
            {
                int parent = queue.Dequeue();
                if (!childrenByParent.TryGetValue(parent, out var children))
                    continue;

                foreach (int child in children)
                {
                    if (family.Add(child))
                        queue.Enqueue(child);
                }
            }

            return family;
        }

        /// <summary>
        /// Attend la première fenêtre visible de la famille de processus Basilisk.
        /// L'énumération Win32 tourne hors du thread UI pour ne pas figer l'interface.
        /// </summary>
        private static async Task<(IntPtr top, int realPid)> WaitForAnyTopWindowFromProcessFamilyAsync(
            Process rootProc,
            int timeoutMs = 15000,
            Func<bool>? cancelled = null)
        {
            int rootPid;
            try { rootPid = rootProc.Id; }
            catch { return (IntPtr.Zero, 0); }

            var sw = Stopwatch.StartNew();

            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (cancelled?.Invoke() == true)
                    break;

                var found = await Task.Run(() =>
                {
                    foreach (var pid in GetProcessFamilyPids(rootPid))
                    {
                        var hwnd = FindAnyTopLevelWindowForPid(pid);
                        if (hwnd != IntPtr.Zero)
                            return (hwnd, pid);
                    }

                    return (IntPtr.Zero, 0);
                });

                if (found.Item1 != IntPtr.Zero)
                    return found;

                await Task.Delay(120);
            }

            return (IntPtr.Zero, 0);
        }

        private const uint TH32CS_SNAPPROCESS = 0x00000002;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct PROCESSENTRY32
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szExeFile;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        private static IntPtr FindAnyTopLevelWindowForPid(int pid)
        {
            IntPtr found = IntPtr.Zero;

            EnumWindows((hWnd, lParam) =>
            {
                GetWindowThreadProcessId(hWnd, out int winPid);
                if (winPid != pid)
                    return true;

                if (!IsWindowVisible(hWnd))
                    return true;

                found = hWnd;
                return false;
            }, IntPtr.Zero);

            return found;
        }

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);
    }
}
