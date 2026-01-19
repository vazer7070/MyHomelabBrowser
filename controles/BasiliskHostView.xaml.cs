using System;
using System.Windows;
using System.Windows.Controls;

namespace MyHomelabBrowser.controles
{
    public partial class BasiliskHostView : UserControl
    {
        public event Action? RetryRequested;
        public event Action? SettingsRequested;
        public ExternalWindowDock? GetHost() => Host;


        private IntPtr _pendingHwnd = IntPtr.Zero;

        public BasiliskHostView()
        {
            InitializeComponent();

            RetryBtn.Click += (_, _) => RetryRequested?.Invoke();
            SettingsBtn.Click += (_, _) => SettingsRequested?.Invoke();

            Loaded += (_, _) =>
            {
                if (_pendingHwnd != IntPtr.Zero)
                {
                    System.Diagnostics.Debug.WriteLine($"[BASILISK VIEW] Loaded pending={_pendingHwnd} HostHandle={Host.HostHandle}");

                    Host.Attach(_pendingHwnd);
                    _pendingHwnd = IntPtr.Zero;
                }
            };

            Unloaded += (_, _) =>
            {
                try { Host.DetachExternalWindow(); } catch { }
                _pendingHwnd = IntPtr.Zero;
            };

            SizeChanged += (_, _) =>
            {
                try { Host.RefreshLayout(); } catch { }
            };

            IsVisibleChanged += (_, _) =>
            {
                try { Host.RefreshLayout(); } catch { }
            };

        }

        public IntPtr GetHostHandle() => Host.HostHandle;

        public void AttachExternalWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;

            if (!IsLoaded)
            {
                _pendingHwnd = hwnd;
                return;
            }

            Host.Attach(hwnd);
            _pendingHwnd = IntPtr.Zero;
        }

        public void DetachExternalWindow()
        {
            _pendingHwnd = IntPtr.Zero;
            Host.DetachExternalWindow();
        }


        public void SetUrl(string url) => UrlText.Text = url;
        public void SetStatus(string text) => StatusText.Text = text;
        public void ShowOverlay(bool visible) => Overlay.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }
}
