using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using PommeFlash.Host.Native;

namespace PommeFlash.Host
{
    /// <summary>
    /// Diagnostic : fenêtres que le module ouvre (boîtes de dialogue de Flash, messages de mise à
    /// jour…) et programmes qu'il lance (FlashUtil, FlashPlayerPlugin…). Chacun est noté une fois
    /// dans le journal, avec son texte : ce que l'utilisateur voit s'y retrouve, et l'on sait si
    /// un message vient de Flash lui-même ou du contenu (qui dessine les siens dans sa fenêtre).
    /// </summary>
    [SupportedOSPlatform("windows")]
    static unsafe class WindowWatch
    {
        // Fenêtres du module : signalées dès leur affichage (évènement EVENT_OBJECT_SHOW). Programmes
        // lancés et leurs fenêtres : relevés toutes les cinq secondes (ce relevé rattrape aussi une
        // fenêtre du module que l'évènement n'aurait pas signalée).
        const uint IntervalMs = 5000;
        const int MaxWindows = 30;
        const int MaxTexts = 8;

        static readonly HashSet<nint> SeenWindows = new();
        static readonly HashSet<uint> SeenProcesses = new();
        static readonly Dictionary<uint, string> ProcessNames = new();
        static readonly List<nint> Found = new();
        static uint _self;
        static nint _hook;

        public static void Start()
        {
            _self = Win32.GetCurrentProcessId();
            List<Win32.ProcessInfo> processes = Win32.Processes();
            // Programmes de Flash déjà présents : service d'aide de la version chinoise, mises à jour…
            string[] flash = processes
                .Where(p => p.Id != _self && p.Name.StartsWith("Flash", StringComparison.OrdinalIgnoreCase))
                .Select(p => p.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            HostChannel.Log("Programmes Flash déjà en cours : " + (flash.Length > 0 ? string.Join(", ", flash) : "aucun") +
                            $" ({processes.Count} processus vus)");
            // Sur le fil du module, qui a une boucle de messages : l'évènement y est remis.
            _hook = Win32.SetWinEventHook(Win32.EVENT_OBJECT_SHOW, Win32.EVENT_OBJECT_SHOW, 0, &OnShow, _self, 0, Win32.WINEVENT_OUTOFCONTEXT);
            UiThread.Delay(IntervalMs, Scan);
        }

        /// <summary>Fenêtre de ce processus affichée : notée si c'est une fenêtre principale (boîte de dialogue…).</summary>
        [UnmanagedCallersOnly]
        static void OnShow(nint hook, uint kind, nint window, int objectId, int childId, uint thread, uint time)
        {
            if (window == 0 || objectId != Win32.OBJID_WINDOW || childId != 0 || Win32.GetAncestor(window, Win32.GA_ROOT) != window)
                return;
            // Décrite après l'évènement : sa description envoie des messages (WM_GETTEXT).
            UiThread.Post(() => Consider(window, _self));
        }

        static void Consider(nint window, uint process)
        {
            if (SeenWindows.Count >= MaxWindows || window == UiThread.Display.Frame || !Win32.IsWindowVisible(window) || !SeenWindows.Add(window))
                return;
            Describe(window, process);
        }

        static void Scan()
        {
            try
            {
                HashSet<uint> watched = WatchedProcesses();
                Found.Clear();
                Win32.EnumWindows(&Collect, 0);
                foreach (nint window in Found.ToList())
                {
                    if (SeenWindows.Count >= MaxWindows)
                        break;
                    uint process;
                    Win32.GetWindowThreadProcessId(window, &process);
                    if (watched.Contains(process))
                        Consider(window, process);
                }
            }
            finally
            {
                UiThread.Delay(IntervalMs, Scan);
            }
        }

        /// <summary>Ce processus et ceux qu'il a lancés (et leurs enfants) ; les nouveaux sont notés.</summary>
        static HashSet<uint> WatchedProcesses()
        {
            var watched = new HashSet<uint> { _self };
            List<Win32.ProcessInfo> processes = Win32.Processes();
            bool added = true;
            while (added)
            {
                added = false;
                foreach (Win32.ProcessInfo process in processes)
                {
                    if (watched.Contains(process.ParentId) && watched.Add(process.Id))
                    {
                        added = true;
                        ProcessNames[process.Id] = process.Name;
                        // conhost.exe : console de l'hôte lui-même (programme console), pas de Flash.
                        if (SeenProcesses.Add(process.Id) && !process.Name.Equals("conhost.exe", StringComparison.OrdinalIgnoreCase))
                            HostChannel.Log($"Programme lancé par le module : {process.Name} (processus {process.Id})");
                    }
                }
            }
            return watched;
        }

        static void Describe(nint window, uint process)
        {
            string title = Text(window);
            string kind = ClassName(window);
            Found.Clear();
            Win32.EnumChildWindows(window, &Collect, 0);
            var texts = new List<string>();
            foreach (nint child in Found)
            {
                if (texts.Count >= MaxTexts)
                    break;
                if (!Win32.IsWindowVisible(child))
                    continue;
                string text = Text(child);
                if (text.Length > 0 && !texts.Contains(text))
                    texts.Add(HostChannel.Excerpt(text, 200));
            }
            string owner = process == _self ? "le module" : ProcessNames.GetValueOrDefault(process, "processus " + process);
            HostChannel.Log($"Fenêtre ouverte par {owner} : « {HostChannel.Excerpt(title, 120)} » (classe {kind})" +
                            (texts.Count > 0 ? " — " + string.Join(" | ", texts.Select(t => "« " + t + " »")) : string.Empty));
        }

        /// <summary>Texte d'une fenêtre, même d'un autre fil ou processus (WM_GETTEXT, sans attendre une fenêtre bloquée).</summary>
        static string Text(nint window)
        {
            const int length = 512;
            char* buffer = stackalloc char[length];
            nuint copied;
            if (Win32.SendMessageTimeoutW(window, Win32.WM_GETTEXT, length, (nint)buffer, Win32.SMTO_ABORTIFHUNG, 300, &copied) == 0)
                return string.Empty;
            return new string(buffer, 0, (int)Math.Min(copied, length - 1)).Replace('\r', ' ').Replace('\n', ' ').Trim();
        }

        static string ClassName(nint window)
        {
            char* buffer = stackalloc char[128];
            int count = Win32.GetClassNameW(window, buffer, 128);
            return count > 0 ? new string(buffer, 0, count) : "?";
        }

        [UnmanagedCallersOnly]
        static int Collect(nint window, nint parameter)
        {
            Found.Add(window);
            return 1;
        }
    }
}
