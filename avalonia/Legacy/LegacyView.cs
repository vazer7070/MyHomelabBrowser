using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform;
using Avalonia.Threading;
using MyHomelabBrowser.classes;

namespace PommeBrowser.Legacy
{
    /// <summary>
    /// Basilisk affiché dans l'onglet : sa fenêtre principale est logée dans celle de la vue
    /// (Windows, et Linux sous X11 ou XWayland). Le clavier suit le focus d'Avalonia, comme pour
    /// les pages web : Basilisk ne l'a que lorsque la vue a le focus (clic dans la page,
    /// sélection de l'onglet).
    /// </summary>
    public sealed class LegacyView : NativeControlHost
    {
        static readonly TimeSpan SearchTimeout = TimeSpan.FromSeconds(20);

        readonly X11Dock? _x11;
        readonly Win32Dock? _win32;
        CancellationTokenSource? _search;
        nint _window;
        bool _hasHost;

        public LegacyView()
        {
            Focusable = true;
            if (OperatingSystem.IsLinux() && X11Embed.Instance is { } x11)
            {
                _x11 = new X11Dock(x11);
                _x11.Clicked += () => Dispatcher.UIThread.Post(OnClicked);
                _x11.Shortcut += (key, modifiers) => Dispatcher.UIThread.Post(() => ShortcutPressed?.Invoke(key, modifiers));
            }
            else if (OperatingSystem.IsWindows())
            {
                _win32 = new Win32Dock();
                _win32.Clicked += OnClicked;
                _win32.Shortcut += (key, modifiers) => ShortcutPressed?.Invoke(key, modifiers);
            }
            GotFocus += (_, _) => SyncKeyboard();
            LostFocus += (_, _) => Dispatcher.UIThread.Post(() => SyncKeyboard());
        }

        /// <summary>Basilisk peut être logé dans un onglet sur ce système (sinon il garde sa fenêtre).</summary>
        public static bool IsSupported
            => OperatingSystem.IsWindows() || (OperatingSystem.IsLinux() && X11Embed.Instance != null);

        /// <summary>La fenêtre de Basilisk est dans l'onglet.</summary>
        public event Action? Docked;

        /// <summary>Fenêtre de Basilisk introuvable (il reste dans sa propre fenêtre, s'il en a une).</summary>
        public event Action? DockFailed;

        /// <summary>Clic dans Basilisk : l'onglet devient actif et la vue prend le focus.</summary>
        public event Action? Activated;

        /// <summary>Raccourci des onglets ou de la fenêtre tapé dans Basilisk.</summary>
        public event Action<Key, KeyModifiers>? ShortcutPressed;

        public bool IsDocked { get; private set; }

        /// <summary>Attend la fenêtre principale de Basilisk, puis la loge dans la vue.</summary>
        public void Attach(ILegacyBrowser browser)
        {
            _search?.Cancel();
            var search = new CancellationTokenSource();
            _search = search;
            _ = SearchAsync(browser, search.Token);
        }

        async Task SearchAsync(ILegacyBrowser browser, CancellationToken cancellation)
        {
            var clock = Stopwatch.StartNew();
            while (!cancellation.IsCancellationRequested && clock.Elapsed < SearchTimeout && !browser.HasExited)
            {
                nint window = await FindWindowAsync(browser).ConfigureAwait(true);
                if (cancellation.IsCancellationRequested)
                    return;
                if (window != 0)
                {
                    _window = window;
                    if (_hasHost)
                        DockNow();
                    return;
                }
                await Task.Delay(120, cancellation).ContinueWith(_ => { }, TaskScheduler.Default).ConfigureAwait(true);
            }
            if (!cancellation.IsCancellationRequested && !browser.HasExited)
            {
                RuntimeLogBuffer.Append("[Basilisk] Fenêtre introuvable : Basilisk reste dans sa propre fenêtre.");
                DockFailed?.Invoke();
            }
        }

        Task<nint> FindWindowAsync(ILegacyBrowser browser)
        {
            if (OperatingSystem.IsLinux() && _x11 != null)
            {
                // Fenêtre annoncée par le programme (moteur Flash intégré) ; sinon cherchée parmi les siennes (Basilisk).
                nint announced = browser.FindWindow();
                return announced != 0 ? Task.FromResult(announced) : _x11.FindClientWindowAsync(ProcessTree(browser.ProcessIds));
            }
            return Task.Run(browser.FindWindow);
        }

        /// <summary>Processus de Basilisk et leurs enfants (lanceur qui passe la main au navigateur).</summary>
        static IReadOnlySet<int> ProcessTree(IEnumerable<int> roots)
        {
            var all = new HashSet<int>();
            var pending = new Queue<int>(roots);
            while (pending.Count > 0 && all.Count < 64)
            {
                int pid = pending.Dequeue();
                if (!all.Add(pid))
                    continue;
                try
                {
                    foreach (string task in Directory.EnumerateDirectories($"/proc/{pid}/task"))
                    {
                        string children = Path.Combine(task, "children");
                        if (!File.Exists(children))
                            continue;
                        foreach (string child in File.ReadAllText(children).Split(' ', StringSplitOptions.RemoveEmptyEntries))
                        {
                            if (int.TryParse(child, out int id))
                                pending.Enqueue(id);
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Processus terminé entre-temps.
                }
            }
            return all;
        }

        void DockNow()
        {
            nint window = _window;
            if (OperatingSystem.IsLinux() && _x11 != null)
            {
                _x11.DockAsync(window).ContinueWith(task =>
                    Dispatcher.UIThread.Post(() => OnDocked(task.IsCompletedSuccessfully && task.Result)));
            }
            else if (_win32 != null && OperatingSystem.IsWindows())
            {
                OnDocked(_win32.Dock(window));
            }
        }

        void OnDocked(bool docked)
        {
            if (!docked)
            {
                DockFailed?.Invoke();
                return;
            }
            bool first = !IsDocked;
            IsDocked = true;
            if (first)
                Docked?.Invoke();
            SyncKeyboard(force: true);
        }

        void OnClicked()
        {
            Activated?.Invoke();
            if (!IsFocused)
                Focus();
            // La fenêtre de PommeBrowser s'active avec ce clic, et cette activation (ou la page
            // web) peut reprendre le clavier juste après : il est redonné une fois tout cela passé.
            Dispatcher.UIThread.Post(() => SyncKeyboard(), DispatcherPriority.Background);
            DispatcherTimer.RunOnce(() => SyncKeyboard(), TimeSpan.FromMilliseconds(200));
        }

        /// <summary>
        /// La page donne le focus à l'élément du contenu (élément.focus(), comme pour un greffon de
        /// navigateur) : le clavier va au lecteur, s'il est affiché.
        /// </summary>
        public void TakeKeyboard()
        {
            // Contenu caché ou minuscule (préchargement d'un jeu) : il ne prend pas le clavier.
            if (!IsDocked || !IsEffectivelyVisible || Bounds.Width < 16 || Bounds.Height < 16)
                return;
            if (!IsFocused)
                Focus();
            Dispatcher.UIThread.Post(() => SyncKeyboard(), DispatcherPriority.Background);
        }

        /// <summary>
        /// Clavier à Basilisk si la vue a le focus et qu'elle est affichée, sinon à la fenêtre.
        /// <paramref name="force"/> : la fenêtre vient d'être activée (le gestionnaire de fenêtres
        /// lui a donné le clavier), il est rendu à Basilisk si la vue a le focus.
        /// </summary>
        public void SyncKeyboard(bool force = false)
        {
            if (!IsDocked)
                return;
            bool page = IsKeyboardFocusWithin && IsEffectivelyVisible;
            nint topLevel = TopLevel.GetTopLevel(this)?.TryGetPlatformHandle()?.Handle ?? 0;
            if (OperatingSystem.IsLinux() && _x11 != null)
                _x11.PostKeyboard(page, topLevel);
            else if (_win32 != null && OperatingSystem.IsWindows())
            {
                // Aucun élément d'Avalonia n'a le focus : Windows l'a donné à une fenêtre enfant
                // (clic dans Basilisk) et Avalonia l'a perdu avec la fenêtre. Le clavier y reste.
                if (!page && IsEffectivelyVisible && TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() == null)
                    return;
                _win32.SetKeyboard(page, topLevel);
            }
        }

        protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
        {
            nint host = 0;
            string descriptor = parent.HandleDescriptor ?? string.Empty;
            if (OperatingSystem.IsLinux() && _x11 != null && descriptor == "XID")
            {
                host = _x11.CreateHostAsync(parent.Handle).GetAwaiter().GetResult();
            }
            else if (_win32 != null && OperatingSystem.IsWindows() && descriptor == "HWND")
            {
                host = _win32.CreateHost(parent.Handle);
            }

            if (host == 0)
                return base.CreateNativeControlCore(parent);

            _hasHost = true;
            if (_window != 0)
                Dispatcher.UIThread.Post(DockNow);
            return new PlatformHandle(host, descriptor);
        }

        protected override void DestroyNativeControlCore(IPlatformHandle control)
        {
            if (!_hasHost)
            {
                base.DestroyNativeControlCore(control);
                return;
            }
            _hasHost = false;
            IsDocked = false;
            if (OperatingSystem.IsLinux() && _x11 != null)
                _x11.DestroyAsync().GetAwaiter().GetResult();
            else if (_win32 != null && OperatingSystem.IsWindows())
                _win32.Destroy();
        }

        /// <summary>
        /// Fenêtre logée à une position donnée dans la vue (pixels de l'écran), plus grande qu'elle
        /// s'il le faut : la vue n'en montre que la partie qui la recouvre. Null : elle remplit la vue.
        /// Moteur Flash intégré à sa place dans la page (Windows, Linux sous X11).
        /// </summary>
        public void PlaceClient((int X, int Y, int Width, int Height)? placement)
        {
            if (_win32 != null && OperatingSystem.IsWindows())
                _win32.SetClientPlacement(placement);
            else if (OperatingSystem.IsLinux() && _x11 != null)
                _x11.PostPlacement(placement);
        }

        /// <summary>La vue passe devant les autres vues natives de la fenêtre (la page web qu'elle recouvre).</summary>
        public void BringToFront()
        {
            if (_win32 != null && OperatingSystem.IsWindows())
                _win32.BringToFront();
            else if (OperatingSystem.IsLinux() && _x11 != null)
                _x11.BringToFront(TopLevel.GetTopLevel(this)?.TryGetPlatformHandle() is { HandleDescriptor: "XID" } handle ? handle.Handle : 0);
        }

        /// <summary>Onglet fermé ou page quittée : plus de recherche de fenêtre.</summary>
        public void Detach()
        {
            _search?.Cancel();
            _search = null;
            _window = 0;
        }
    }
}
