using System;
using System.Threading;

namespace PommeBrowser.Linux.Ui
{
    /// <summary>Retour sur le fil de l'interface (boucle principale de GLib).</summary>
    static class MainThread
    {
        static SynchronizationContext? _context;

        public static void Initialize() => _context = SynchronizationContext.Current;

        public static void Post(Action action)
        {
            if (_context == null)
                action();
            else
                _context.Post(_ => action(), null);
        }
    }
}
