using Microsoft.Web.WebView2.Wpf;
using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using static MyHomelabBrowser.MainWindow;

namespace MyHomelabBrowser
{
    public partial class DetachedWindow : Window
    {
        readonly WebView2 _web;
        readonly MainWindow _main;

        bool _closing;
        bool _pendingRedock;
        bool _canDock;
        readonly WebTabContent _state;

        public Action<WebTabContent>? RequestRedock { get; set; }

        //public Action<WebView2> RequestRedock { get; internal set; }

        public DetachedWindow(MainWindow main, WebTabContent state)
        {
            InitializeComponent();

            _main = main;
            _state = state;
            _web = state.Web;

            Loaded += OnLoaded;
            Closed += OnClosed;

            LocationChanged += (_, _) => UpdateDockPreview();
        }


        // =====================================================
        // LIFECYCLE
        // =====================================================

        void OnLoaded(object? sender, RoutedEventArgs e)
        {
            if (_state.IsLegacyExternal && _state.LegacyHwnd != IntPtr.Zero)
            {
                _state.LegacyHost ??= new MyHomelabBrowser.controles.ExternalWindowDock();

                DetachFromParent(_state.LegacyHost);
                Host.Content = _state.LegacyHost;

               
                _state.LegacyHost.Bind(_state.LegacyHwnd);
                _state.LegacyHost.ShowDock();

                Dispatcher.BeginInvoke(() =>
                {
                    _state.LegacyHost.UpdateDockPosition();
                }, DispatcherPriority.Loaded);
            }
            else
            {
                DetachFromParent(_state.Web);
                Host.Content = _state.Web;
            }
        }


        void OnClosed(object? sender, EventArgs e)
        {
            _closing = true;

            _main.HideDockIndicator();
            _main.EndDockingMode();

            if (_pendingRedock)
            {
                _pendingRedock = false;

                _main.Dispatcher.BeginInvoke(() =>
                {
                    // Redock du state complet : le moteur reste vivant.
                    RequestRedock?.Invoke(_state);
                });
                return;
            }

            // Une fenêtre détachée fermée sans redock doit détruire son WebView2,
            // son moniteur Ruffle et son éventuel processus Basilisk.
            Host.Content = null;
            _ = _main.ShutdownDetachedTabAsync(_state);
        }


        // =====================================================
        // DOCK PREVIEW (FANTÔME)
        // =====================================================

        void UpdateDockPreview()
        {
            if (_closing)
                return;

            var thisRect = new Rect(Left, Top, ActualWidth, ActualHeight);

            var mainTopLeft = _main.PointToScreen(new Point(0, 0));
            var mainRect = new Rect(
                mainTopLeft.X,
                mainTopLeft.Y,
                _main.ActualWidth,
                60
            );

            _canDock = thisRect.IntersectsWith(mainRect);

            if (_canDock)
                _main.ShowDockIndicator();
            else
                _main.HideDockIndicator();
        }

        // =====================================================
        // UI ACTIONS
        // =====================================================

        void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            try { DragMove(); } catch { }
        }

        void Redock_Click(object sender, RoutedEventArgs e)
        {
            // 🔒 VERROU GLOBAL
            if (_closing)
                return;

            _closing = true;
            _pendingRedock = true;

            // couper toute animation / preview
            _main.HideDockIndicator();

            // libérer le WebView
            Host.Content = null;

            // fermer d'abord, redock ensuite (OnClosed)
            Close();
        }

        // =====================================================
        // UTILS
        // =====================================================

        static void DetachFromParent(UIElement element)
        {
            if (element == null) return;

            var parent = VisualTreeHelper.GetParent(element);

            if (parent is ContentControl cc)
            {
                if (ReferenceEquals(cc.Content, element))
                    cc.Content = null;
                return;
            }

            if (parent is Decorator d)
            {
                if (ReferenceEquals(d.Child, element))
                    d.Child = null;
                return;
            }

            if (parent is Panel p)
            {
                p.Children.Remove(element);
                return;
            }
        }
    }
}
