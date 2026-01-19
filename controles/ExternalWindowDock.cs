using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;

namespace MyHomelabBrowser.controles
{
    public sealed class ExternalWindowDock : Border
    {
        private IntPtr _hwnd = IntPtr.Zero;       // Basilisk top-level qu'on embed
        private IntPtr _topHwnd = IntPtr.Zero;    // alias (au cas où)
        private bool _embedded = false;
        private DispatcherTimer? _retryTimer;


        private readonly Win32Host _host;

        private const int SW_HIDE = 0;
        private const int SW_SHOW = 5;

        // Styles
        private const int GWL_STYLE = -16;
        private const int GWL_EXSTYLE = -20;

        private const int WS_CHILD = 0x40000000;
        private const int WS_VISIBLE = 0x10000000;
        private const int WS_POPUP = unchecked((int)0x80000000);
        private const int WS_CAPTION = 0x00C00000;
        private const int WS_THICKFRAME = 0x00040000;

        private const int WS_EX_APPWINDOW = 0x00040000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;

        // ✅ Compat / Alias : ton code appelle Bind()
        public void Bind(IntPtr hwnd) => Attach(hwnd);

        // ✅ Compat / Alias : ton code appelle ShowDock()
        public void ShowDock() => ShowEmbedded();

        // ✅ Compat / Alias : ton code appelle HideDock()
        public void HideDock() => HideEmbedded();

        public IntPtr DockedHwnd => _hwnd;

        // ✅ VRAI handle host maintenant
        public IntPtr HostHandle => _host.Handle;

        public ExternalWindowDock()
        {
            _host = new Win32Host();
            Child = _host;

            Loaded += (_, _) =>
            {
                // force layout puis dock si un hwnd a déjà été fourni
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_hwnd != IntPtr.Zero)
                    {
                        EnsureEmbedded();
                        UpdateDockPosition();
                        ShowEmbedded();
                    }
                }), DispatcherPriority.Loaded);
            };

            Unloaded += (_, _) => DetachExternalWindow();

            SizeChanged += (_, _) =>
                Dispatcher.BeginInvoke(UpdateDockPosition, DispatcherPriority.Loaded);

            IsVisibleChanged += (_, _) =>
            {
                if (!IsVisible) HideEmbedded();
                else Dispatcher.BeginInvoke(new Action(() =>
                {
                    EnsureEmbedded();
                    UpdateDockPosition();
                    ShowEmbedded();
                }), DispatcherPriority.Loaded);
            };
        }

        // ✅ Pour compat avec ton code actuel
        public void SetTopLevel(IntPtr topHwnd)
        {
            _topHwnd = topHwnd;
            // on garde, mais en vrai embed, c'est WS_CHILD donc taskbar disparait anyway
        }

        public void Attach(IntPtr hwnd)
        {

            if (hwnd == IntPtr.Zero) return;

            if (_hwnd != IntPtr.Zero && _hwnd != hwnd)
                DetachExternalWindow();

            _hwnd = hwnd;
            _topHwnd = hwnd;
            _embedded = false;

            Dispatcher.BeginInvoke(new Action(() =>
            {
                EnsureEmbedded();

                // ✅ si le host n'a pas encore de handle, on retry
                if (!_embedded)
                    StartRetry();

                UpdateDockPosition();
                ShowEmbedded();
            }), DispatcherPriority.Loaded);
        }

        private void StartRetry()
        {
            _retryTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _retryTimer.Tick -= RetryTick;
            _retryTimer.Tick += RetryTick;
            _retryTimer.Start();
        }

        private void RetryTick(object? sender, EventArgs e)
        {
            if (_hwnd == IntPtr.Zero)
            {
                _retryTimer?.Stop();
                return;
            }

            EnsureEmbedded();

            if (_embedded)
            {
                _retryTimer?.Stop();
                UpdateDockPosition();
                ShowEmbedded();
            }
        }


        // ✅ Compat : ton ancien code appelle RefreshLayout()
        public void RefreshLayout() => UpdateDockPosition();

        // ✅ Compat : ton ancien code appelle ForceRedraw()
        public void ForceRedraw() => UpdateDockPosition();

        // ✅ Compat : ton ancien code appelle ShowEmbedded()
        public void ShowEmbedded()
        {
            if (_hwnd == IntPtr.Zero) return;

            EnsureEmbedded();
            UpdateDockPosition();

            // Une fois child, SW_SHOW ne crée PAS de taskbar
            ShowWindow(_hwnd, SW_SHOW);
        }

        // ✅ Compat : ton ancien code appelle HideEmbedded()
        public void HideEmbedded()
        {
            if (_hwnd == IntPtr.Zero) return;
            ShowWindow(_hwnd, SW_HIDE);
        }

        // ✅ Compat : fermeture logique (si onglet fermé)
        public void DetachExternalWindow()
        {
            if (_hwnd == IntPtr.Zero) return;

            try
            {
                // remettre en top-level (optionnel mais propre)
                SetParent(_hwnd, IntPtr.Zero);

                int style = GetWindowLong(_hwnd, GWL_STYLE);
                style &= ~WS_CHILD;
                style |= WS_POPUP;
                style |= WS_CAPTION;
                style |= WS_THICKFRAME;
                SetWindowLong(_hwnd, GWL_STYLE, style);

                int ex = GetWindowLong(_hwnd, GWL_EXSTYLE);
                ex |= WS_EX_APPWINDOW;
                ex &= ~WS_EX_TOOLWINDOW;
                SetWindowLong(_hwnd, GWL_EXSTYLE, ex);
            }
            catch { /* ignore */ }

            try { ShowWindow(_hwnd, SW_HIDE); } catch { /* ignore */ }
            _retryTimer?.Stop();

            _hwnd = IntPtr.Zero;
            _topHwnd = IntPtr.Zero;
            _embedded = false;
        }

        // ✅ IMPORTANT : en vrai embed, on resize en coordonnées PARENT (0,0,w,h)
        public void UpdateDockPosition()
        {
            if (_hwnd == IntPtr.Zero) return;
            if (!IsVisible) return;
            if (ActualWidth < 2 || ActualHeight < 2) return;

            EnsureEmbedded();

            int w = Math.Max(1, (int)ActualWidth);
            int h = Math.Max(1, (int)ActualHeight);
            // child coords => (0,0)
            MoveWindow(_hwnd, 0, 0, w, h, true);
        }

        private void EnsureEmbedded()
        {
           
            if (_hwnd == IntPtr.Zero) return;
            if (_embedded) return;
            if (HostHandle == IntPtr.Zero) return;

            try
            {
                var prev = SetParent(_hwnd, HostHandle);
                if (prev == IntPtr.Zero)
                {
                    int err = Marshal.GetLastWin32Error();
                    System.Diagnostics.Debug.WriteLine("[DOCK] SetParent failed err=" + err + " hwnd=" + _hwnd + " host=" + HostHandle);
                }


                // ✅ child style (plus de taskbar possible)
                int style = GetWindowLong(_hwnd, GWL_STYLE);
                style &= ~WS_POPUP;
                style &= ~WS_CAPTION;
                style &= ~WS_THICKFRAME;
                style |= WS_CHILD;
                style |= WS_VISIBLE;
                SetWindowLong(_hwnd, GWL_STYLE, style);

                // ✅ exstyle : retire appwindow
                int ex = GetWindowLong(_hwnd, GWL_EXSTYLE);
                ex &= ~WS_EX_APPWINDOW;
                ex |= WS_EX_TOOLWINDOW;
                SetWindowLong(_hwnd, GWL_EXSTYLE, ex);

                _embedded = true;
            }
            catch
            {
                // si ça échoue, on laisse l'ancien comportement (mais tu verras encore la taskbar)
                _embedded = false;
            }
        }

        // ==========================
        // Win32 imports
        // ==========================
        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool MoveWindow(IntPtr hWnd, int X, int Y, int nWidth, int nHeight, bool bRepaint);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        // ==========================
        // Host HWND (HwndHost)
        // ==========================
        private sealed class Win32Host : HwndHost
        {
            public IntPtr Handle { get; private set; }

            protected override HandleRef BuildWindowCore(HandleRef hwndParent)
            {
                Handle = CreateWindowEx(
                    0, "static", "",
                    WS_CHILD | WS_VISIBLE,
                    0, 0, 1, 1,
                    hwndParent.Handle,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero);

                return new HandleRef(this, Handle);
            }

            protected override void DestroyWindowCore(HandleRef hwnd)
            {
                if (hwnd.Handle != IntPtr.Zero)
                    DestroyWindow(hwnd.Handle);
            }

            [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
            private static extern IntPtr CreateWindowEx(
                int exStyle,
                string className,
                string windowName,
                int style,
                int x, int y, int width, int height,
                IntPtr hwndParent,
                IntPtr hMenu,
                IntPtr hInstance,
                IntPtr lpParam);

            [DllImport("user32.dll", SetLastError = true)]
            private static extern bool DestroyWindow(IntPtr hwnd);
        }
    }
}
