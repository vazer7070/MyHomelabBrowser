using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace MyHomelabBrowser.classes.Flash
{
    public class LegacyDockContext
    {
        public Process Process = null!;
        public IntPtr Hwnd = IntPtr.Zero;
        public DispatcherTimer RetryTimer = null!;
        public FrameworkElement DockTarget = null!;
        public bool Embedded = false;
    }

}
