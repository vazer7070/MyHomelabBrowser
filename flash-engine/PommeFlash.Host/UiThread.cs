using System.Collections.Concurrent;
using PommeFlash.Host.Native;

namespace PommeFlash.Host
{
    /// <summary>
    /// Fil de la fenêtre, le seul d'où le module est appelé (NPAPI n'est pas multifil) : les
    /// autres fils (réseau, commandes, appels asynchrones du module) y déposent leur travail.
    /// </summary>
    static class UiThread
    {
        public const uint RunMessage = Win32.WM_APP + 1;

        static readonly ConcurrentQueue<Action> Pending = new();
        static readonly Dictionary<nuint, Action> Delayed = new();
        static nuint _nextDelay = 1;

        public static nint Window { get; private set; }

        public static uint ThreadId { get; private set; }

        public static bool IsCurrent => Win32.GetCurrentThreadId() == ThreadId;

        public static void Attach(nint window)
        {
            Window = window;
            ThreadId = Win32.GetCurrentThreadId();
            if (!Pending.IsEmpty)
                Win32.PostMessageW(window, RunMessage, 0, 0);
        }

        /// <summary>Depuis n'importe quel fil.</summary>
        public static void Post(Action action)
        {
            Pending.Enqueue(action);
            if (Window != 0)
                Win32.PostMessageW(Window, RunMessage, 0, 0);
        }

        public static void RunPending()
        {
            while (Pending.TryDequeue(out Action? action))
                Run(action);
        }

        /// <summary>Plus tard, sur ce fil (minuteries 1 à 999 ; celles du module commencent à 1000).</summary>
        public static void Delay(uint milliseconds, Action action)
        {
            nuint id = _nextDelay;
            _nextDelay = _nextDelay >= 999 ? 1 : _nextDelay + 1;
            Delayed[id] = action;
            Win32.SetTimer(Window, id, milliseconds, 0);
        }

        /// <summary>Minuterie arrivée à échéance : vrai si elle venait de <see cref="Delay"/>.</summary>
        public static bool RunDelayed(nuint id)
        {
            if (!Delayed.Remove(id, out Action? action))
                return false;
            Win32.KillTimer(Window, id);
            Run(action);
            return true;
        }

        static void Run(Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                HostChannel.Error(ex.GetType().Name + " : " + ex.Message);
            }
        }
    }
}
