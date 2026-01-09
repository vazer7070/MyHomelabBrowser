using Microsoft.Web.WebView2.Wpf;
using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace MyHomelabBrowser
{
    public partial class DetachedWindow : Window
    {
        public event Action<WebView2>? RequestRedock;

        readonly WebView2 _web;

        // -------------------------
        // Mouse hook
        // -------------------------
        private const int WH_MOUSE_LL = 14;
        private const int WM_MOUSEMOVE = 0x0200;
        private const int WM_LBUTTONUP = 0x0202;

        private static IntPtr _mouseHook = IntPtr.Zero;
        private static LowLevelMouseProc? _mouseProc;
        readonly MainWindow _main;

        bool _draggingWindow = true;

        private delegate IntPtr LowLevelMouseProc(
            int nCode,
            IntPtr wParam,
            IntPtr lParam
        );

        public DetachedWindow(MainWindow main, WebView2 web)
        {
            InitializeComponent();
            _main = main; 
            _web = web;
            Host.Content = web;

            Loaded += OnLoaded;
            Closed += OnClosed;
        }

        // -------------------------
        // Lifecycle
        // -------------------------
        void OnLoaded(object? sender, RoutedEventArgs e)
        {
            // activer le hook global
            _mouseProc = MouseHookCallback;
            _mouseHook = SetWindowsHookEx(
                WH_MOUSE_LL,
                _mouseProc,
                IntPtr.Zero, // GLOBAL
                0
            );
        }

        void OnClosed(object? sender, EventArgs e)
        {
            if (_mouseHook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_mouseHook);
                _mouseHook = IntPtr.Zero;
            }

            if (Owner is MainWindow main)
            {
                main.HideDockIndicator();
                main.EndDockingMode();
            }
        }

        // -------------------------
        // Mouse hook callback
        // -------------------------
        private IntPtr MouseHookCallback(
            int nCode,
            IntPtr wParam,
            IntPtr lParam
        )
        {
            if (nCode < 0)
                return CallNextHookEx(_mouseHook, nCode, wParam, lParam);

            if (wParam == (IntPtr)WM_MOUSEMOVE)
            {
                POINT p;
                GetCursorPos(out p);

                Dispatcher.Invoke(() =>
                {
                    // 1️⃣ la fenêtre suit la souris
                    if (_draggingWindow)
                    {
                        Left = p.X - Width / 2;
                        Top = p.Y - 10;
                    }

                    var main = _main;


                    var ownerTop = main.PointToScreen(new Point(0, 0)).Y;
                    double y = p.Y - ownerTop;

                    if (y >= 0 && y <= 50)
                        main.ShowDockIndicator();
                    else
                        main.HideDockIndicator();
                });
            }

            if (wParam == (IntPtr)WM_LBUTTONUP)
            {
                Dispatcher.Invoke(() =>
                {
                    _draggingWindow = false;

                    var main = _main;


                    if (main.IsDockIndicatorVisible)
                    {
                        main.HideDockIndicator();
                        main.EndDockingMode();

                        RequestRedock?.Invoke(_web);
                        Close();
                    }
                });
            }

            return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
        }

        // -------------------------
        // Win32
        // -------------------------
        [DllImport("user32.dll")]
        static extern IntPtr SetWindowsHookEx(
            int idHook,
            LowLevelMouseProc lpfn,
            IntPtr hMod,
            uint dwThreadId
        );

        [DllImport("user32.dll")]
        static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        static extern IntPtr CallNextHookEx(
            IntPtr hhk,
            int nCode,
            IntPtr wParam,
            IntPtr lParam
        );

        [DllImport("user32.dll")]
        static extern bool GetCursorPos(out POINT lpPoint);

        [StructLayout(LayoutKind.Sequential)]
        struct POINT
        {
            public int X;
            public int Y;
        }
    }
}
