using System.Collections.Concurrent;

namespace PommeFlash.Host
{
    /// <summary>
    /// Fil de la fenêtre, le seul d'où le module est appelé (NPAPI n'est pas multifil) : les
    /// autres fils (réseau, commandes, appels asynchrones du module) y déposent leur travail.
    /// </summary>
    static class UiThread
    {
        static readonly ConcurrentQueue<Action> Pending = new();
        static volatile IHostDisplay? _display;

        /// <summary>Fenêtre et boucle de messages de l'hôte, une fois créées.</summary>
        public static IHostDisplay Display => _display ?? throw new InvalidOperationException("Fenêtre de l'hôte pas encore créée.");

        /// <summary>Sur le fil du module, une fois la fenêtre créée.</summary>
        public static void Attach(IHostDisplay display)
        {
            _display = display;
            if (!Pending.IsEmpty)
                display.Wake();
        }

        /// <summary>Depuis n'importe quel fil.</summary>
        public static void Post(Action action)
        {
            Pending.Enqueue(action);
            _display?.Wake();
        }

        public static void RunPending()
        {
            while (Pending.TryDequeue(out Action? action))
                Run(action);
        }

        /// <summary>Plus tard, sur ce fil.</summary>
        public static void Delay(uint milliseconds, Action action) => Display.Delay(milliseconds, action);

        public static void Run(Action action)
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
