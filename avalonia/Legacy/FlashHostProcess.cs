using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using MyHomelabBrowser.classes;
using PommeBrowser.Engine;

namespace PommeBrowser.Legacy
{
    /// <summary>
    /// Moteur Flash intégré : PommeFlashHost (voir flash-engine/), qui charge le module Flash de
    /// l'utilisateur et affiche le contenu dans une fenêtre que l'onglet loge comme celle de
    /// Basilisk. Échanges : événements JSON sur sa sortie standard, commandes sur son entrée.
    /// Il ne survit jamais à PommeBrowser : job Windows, ou PR_SET_PDEATHSIG sous Linux (dans l'hôte).
    /// </summary>
    sealed class FlashHostProcess : ILegacyBrowser
    {
        static readonly List<FlashHostProcess> Running = new();
        static nint _job;

        readonly Process _process;
        // Écritures sur l'entrée de l'hôte (réponses aux scripts, fermeture) : une à la fois.
        readonly SemaphoreSlim _input = new(1, 1);
        Task _reading = Task.CompletedTask;
        // Scripts de la page que l'hôte attend (eval), et appels de la page vers le contenu en cours.
        readonly ConcurrentDictionary<int, byte> _pendingScripts = new();
        readonly Dictionary<int, CallSlot> _calls = new();
        int _nextCall;
        volatile bool _calling;
        volatile bool _ready;
        nint _window;
        bool _closed;
        bool _exited;

        FlashHostProcess(Process process, string module)
        {
            _process = process;
            Module = module;
        }

        /// <summary>Module Flash chargé par cet hôte.</summary>
        public string Module { get; }

        /// <summary>
        /// L'hôte s'est arrêté seul avant d'afficher le contenu : module impossible à charger,
        /// contenu refusé, plantage au démarrage. Un autre module peut prendre le relais.
        /// </summary>
        public bool FailedToStart { get; private set; }

        /// <summary>
        /// Hôte livré avec PommeBrowser pour ce module : un processus ne charge que les modules de
        /// son architecture, lue dans le fichier (Windows : flash\PommeFlashHost.exe en 64 bits,
        /// flash\x86\PommeFlashHost.exe en 32 bits ; Linux : flash/PommeFlashHost, 64 bits).
        /// </summary>
        public static string ExecutablePath(string module) => ExecutablePath(FlashModuleSearch.Is32Bit(module));

        static string ExecutablePath(bool is32Bit)
        {
            string flash = Path.Combine(AppContext.BaseDirectory, "flash");
            if (!OperatingSystem.IsWindows())
                return Path.Combine(flash, "PommeFlashHost");
            return is32Bit ? Path.Combine(flash, "x86", "PommeFlashHost.exe") : Path.Combine(flash, "PommeFlashHost.exe");
        }

        /// <summary>Un hôte est livré dans cette compilation (au moins en 64 bits).</summary>
        public static bool IsAvailable => File.Exists(ExecutablePath(false));

        /// <summary>L'hôte de l'architecture de ce module est livré (Linux : 64 bits seulement).</summary>
        public static bool IsAvailableFor(string module)
            => (OperatingSystem.IsWindows() || !FlashModuleSearch.Is32Bit(module)) && File.Exists(ExecutablePath(module));

        public event Action? Exited;

        /// <summary>Le contenu demande une page (cible _blank, _self…).</summary>
        public event Action<Uri, string>? NavigateRequested;

        /// <summary>
        /// Script à exécuter dans la page (ExternalInterface.call, adresse javascript:). Avec un
        /// numéro, l'hôte attend la réponse (<see cref="Reply"/>) ; sans, aucune.
        /// </summary>
        public event Action<int?, string>? ScriptRequested;

        /// <summary>
        /// Cookies de la page pour une adresse que le module charge (numéro de la question,
        /// adresse, HttpOnly compris) : réponse par <see cref="Reply"/>, avec l'en-tête Cookie.
        /// </summary>
        public event Action<int, Uri, bool>? CookiesRequested;

        /// <summary>Cookie à enregistrer dans la page : en-tête Set-Cookie reçu (vrai) ou posé par le module.</summary>
        public event Action<Uri, string, bool>? CookieReceived;

        public static FlashHostProcess Start(FlashContent content, string module, bool isPrivate)
        {
            bool is32Bit = FlashModuleSearch.Is32Bit(module);
            string executable = ExecutablePath(is32Bit);
            var start = new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = Path.GetDirectoryName(executable)!,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (string argument in Arguments(content, module, isPrivate))
                start.ArgumentList.Add(argument);

            Process process = Process.Start(start) ?? throw new InvalidOperationException("PommeFlashHost ne démarre pas.");
            if (OperatingSystem.IsWindows())
                AssignToJob(process);

            var host = new FlashHostProcess(process, module);
            lock (Running)
                Running.Add(host);
            host._reading = Task.Run(host.ReadEventsAsync);
            _ = Task.Run(host.DrainErrorsAsync);
            // Abonnement avant la surveillance : un hôte déjà arrêté (module refusé) est signalé aussitôt.
            process.Exited += (_, _) => _ = host.OnProcessExitedAsync();
            process.EnableRaisingEvents = true;
            RuntimeLogBuffer.Append($"[Flash] Moteur intégré lancé (PID {process.Id}, {(is32Bit ? 32 : 64)} bits) : {content.Swf.GetLeftPart(UriPartial.Path)} avec {Path.GetFileName(module)}");
            return host;
        }

        static IEnumerable<string> Arguments(FlashContent content, string module, bool isPrivate)
        {
            yield return "--plugin";
            yield return module;
            yield return "--swf";
            yield return content.Swf.AbsoluteUri;
            yield return "--page";
            yield return content.Page.AbsoluteUri;
            yield return "--width";
            yield return content.Width.ToString(CultureInfo.InvariantCulture);
            yield return "--height";
            yield return content.Height.ToString(CultureInfo.InvariantCulture);
            if (content.FlashVars != null)
            {
                yield return "--flashvars";
                yield return content.FlashVars;
            }
            if (content.Id is { Length: > 0 } id)
            {
                yield return "--id";
                yield return id;
            }
            foreach ((string name, string value) in content.Params)
            {
                yield return "--param";
                yield return name + "=" + value;
            }
            if (isPrivate)
                yield return "--private";
            // Cookies de la page donnés au lecteur, et ceux qu'il reçoit gardés dans la page (WebView2, WebKitGTK).
            yield return "--share-cookies";
            // Fenêtre cachée jusqu'à ce que l'onglet la loge.
            yield return "--hidden";
        }

        async Task ReadEventsAsync()
        {
            try
            {
                while (await _process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
                {
                    if (line.StartsWith('{'))
                        OnEvent(line);
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
            }
        }

        async Task DrainErrorsAsync()
        {
            try
            {
                while (await _process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
                    RuntimeLogBuffer.Append("[Flash] " + line);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
            }
        }

        void OnEvent(string line)
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(line);
                JsonElement root = document.RootElement;
                string? name = root.TryGetProperty("event", out JsonElement kind) ? kind.GetString() : null;
                switch (name)
                {
                    case "ready" when root.TryGetProperty("window", out JsonElement window) && window.TryGetInt64(out long handle):
                        _window = (nint)handle;
                        _ready = true;
                        break;
                    case "navigate" when root.TryGetProperty("url", out JsonElement url) &&
                                         Uri.TryCreate(url.GetString(), UriKind.Absolute, out Uri? target) &&
                                         (target.Scheme == Uri.UriSchemeHttp || target.Scheme == Uri.UriSchemeHttps):
                        string frame = root.TryGetProperty("target", out JsonElement value) ? value.GetString() ?? "_blank" : "_blank";
                        Dispatcher.UIThread.Post(() => NavigateRequested?.Invoke(target, frame));
                        break;
                    case "log":
                        RuntimeLogBuffer.Append("[Flash] " + (root.TryGetProperty("message", out JsonElement message) ? message.GetString() : line));
                        break;
                    case "eval" when root.TryGetProperty("id", out JsonElement number) && number.TryGetInt32(out int id):
                        string evaluated = Text(root, "code");
                        if (_calling)
                        {
                            // La page attend la réponse du contenu : elle ne peut pas exécuter de script
                            // maintenant. Refus immédiat, sans quoi chacun attendrait l'autre.
                            Reply(id, false, null);
                            RuntimeLogBuffer.Append("[Flash] Script du contenu refusé pendant un appel de la page vers le contenu.");
                            break;
                        }
                        _pendingScripts[id] = 0;
                        Dispatcher.UIThread.Post(() =>
                        {
                            if (ScriptRequested is { } handler)
                                handler(id, evaluated);
                            else
                                Reply(id, false, null);
                        });
                        break;
                    case "cookies" when root.TryGetProperty("id", out JsonElement question) && question.TryGetInt32(out int cookieQuestion):
                        bool withHttpOnly = root.TryGetProperty("http", out JsonElement httpOnly) && httpOnly.ValueKind == JsonValueKind.True;
                        if (!Uri.TryCreate(Text(root, "url"), UriKind.Absolute, out Uri? cookieUrl))
                        {
                            Reply(cookieQuestion, false, null);
                            break;
                        }
                        Dispatcher.UIThread.Post(() =>
                        {
                            if (CookiesRequested is { } handler)
                                handler(cookieQuestion, cookieUrl, withHttpOnly);
                            else
                                Reply(cookieQuestion, false, null);
                        });
                        break;
                    case "called" when root.TryGetProperty("id", out JsonElement callId) && callId.TryGetInt32(out int answered):
                        lock (_calls)
                        {
                            if (_calls.TryGetValue(answered, out CallSlot? slot))
                            {
                                slot.Ok = root.TryGetProperty("ok", out JsonElement callOk) && callOk.ValueKind == JsonValueKind.True;
                                slot.Value = root.TryGetProperty("value", out JsonElement callValue) && callValue.ValueKind == JsonValueKind.String ? callValue.GetString() : null;
                                slot.Done.Set();
                            }
                        }
                        break;
                    case "set-cookie" when Uri.TryCreate(Text(root, "url"), UriKind.Absolute, out Uri? receivedFrom):
                        string received = Text(root, "cookie");
                        bool fromHttp = root.TryGetProperty("http", out JsonElement viaHttp) && viaHttp.ValueKind == JsonValueKind.True;
                        Dispatcher.UIThread.Post(() => CookieReceived?.Invoke(receivedFrom, received, fromHttp));
                        break;
                    case "script":
                        // Adresse javascript: : exécutée dans la page si elle vise la page elle-même.
                        string code = Text(root, "code");
                        string? targetWindow = root.TryGetProperty("target", out JsonElement named) ? named.GetString() : null;
                        if (string.IsNullOrEmpty(targetWindow) || targetWindow is "_self" or "_top" or "_parent")
                            Dispatcher.UIThread.Post(() => ScriptRequested?.Invoke(null, code));
                        else
                            RuntimeLogBuffer.Append($"[Flash] Adresse javascript: pour la fenêtre « {targetWindow} » ignorée.");
                        break;
                }
            }
            catch (JsonException)
            {
            }
        }

        static string Text(JsonElement root, string name)
            => root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

        /// <summary>Réponse à une question de l'hôte (script de la page, cookies) : « result &lt;id&gt; {"ok":…,"value":…} ».</summary>
        public void Reply(int id, bool ok, string? value)
        {
            _pendingScripts.TryRemove(id, out _);
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteBoolean("ok", ok);
                if (value == null)
                    writer.WriteNull("value");
                else
                    writer.WriteString("value", value);
                writer.WriteEndObject();
            }
            _ = WriteAsync("result " + id.ToString(CultureInfo.InvariantCulture) + " " + Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length));
        }

        /// <summary>Une ligne pour l'hôte ; faux s'il ne lit plus son entrée.</summary>
        async Task<bool> WriteAsync(string line)
        {
            await _input.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_process.HasExited)
                    return false;
                await _process.StandardInput.WriteLineAsync(line).ConfigureAwait(false);
                await _process.StandardInput.FlushAsync().ConfigureAwait(false);
                return true;
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
            {
                return false;
            }
            finally
            {
                _input.Release();
            }
        }

        sealed class CallSlot
        {
            public readonly ManualResetEvent Done = new(false);
            public bool Ok;
            public string? Value;
        }

        /// <summary>
        /// Appel de la page vers le contenu (CallFunction d'une fonction déclarée par
        /// ExternalInterface.addCallback), sur le fil de l'interface et de façon synchrone, comme
        /// un greffon de navigateur : la page attend la réponse (code JavaScript à évaluer).
        /// Pendant l'attente, seuls les messages que Windows envoie d'autres processus sont traités
        /// (la fenêtre du lecteur est logée dans celle de PommeBrowser). Null si l'hôte refuse,
        /// ne répond pas à temps, ou attend lui-même un script de la page (chacun attendrait l'autre).
        /// </summary>
        public string? CallFunction(string request, TimeSpan timeout)
        {
            if (HasExited)
                return null;
            if (!_pendingScripts.IsEmpty)
            {
                RuntimeLogBuffer.Append("[Flash] Appel de la page vers le contenu refusé : le contenu attend un script de la page.");
                return null;
            }

            int id = Interlocked.Increment(ref _nextCall);
            var slot = new CallSlot();
            lock (_calls)
                _calls[id] = slot;
            _calling = true;
            try
            {
                using var buffer = new MemoryStream();
                using (var writer = new Utf8JsonWriter(buffer))
                {
                    writer.WriteStartObject();
                    writer.WriteString("request", request);
                    writer.WriteEndObject();
                }
                string line = "call " + id.ToString(CultureInfo.InvariantCulture) + " " + Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
                if (!WriteLine(line, timeout))
                    return null;
                bool answered = OperatingSystem.IsWindows() ? WaitPumpingSentMessages(slot.Done, timeout) : slot.Done.WaitOne(timeout);
                if (!answered)
                {
                    RuntimeLogBuffer.Append($"[Flash] Le contenu n'a pas répondu à un appel de la page après {timeout.TotalSeconds:0} s.");
                    return null;
                }
                return slot.Ok ? slot.Value : null;
            }
            finally
            {
                _calling = false;
                lock (_calls)
                    _calls.Remove(id);
                slot.Done.Dispose();
            }
        }

        /// <summary>Une ligne pour l'hôte, sans quitter le fil appelant ; faux s'il ne lit plus son entrée.</summary>
        bool WriteLine(string line, TimeSpan timeout)
        {
            if (!_input.Wait(timeout))
                return false;
            try
            {
                if (_process.HasExited)
                    return false;
                _process.StandardInput.WriteLine(line);
                _process.StandardInput.Flush();
                return true;
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
            {
                return false;
            }
            finally
            {
                _input.Release();
            }
        }

        /// <summary>
        /// Attente sur le fil de l'interface : les messages envoyés (SendMessage) par d'autres fils ou
        /// processus sont traités, ceux de l'application restent en file.
        /// </summary>
        [SupportedOSPlatform("windows")]
        static bool WaitPumpingSentMessages(WaitHandle signal, TimeSpan timeout)
        {
            nint[] handles = { signal.SafeWaitHandle.DangerousGetHandle() };
            var clock = Stopwatch.StartNew();
            while (true)
            {
                long remaining = (long)(timeout - clock.Elapsed).TotalMilliseconds;
                if (remaining <= 0)
                    return signal.WaitOne(0);
                uint result = MsgWaitForMultipleObjectsEx(1, handles, (uint)remaining, QsSendMessage, 0);
                if (result == WaitObject0)
                    return true;
                if (result != WaitObject0 + 1)
                    return signal.WaitOne(0);
                PeekMessageW(out _, 0, 0, 0, PmNoRemove | PmQsSendMessage);
            }
        }

        const uint QsSendMessage = 0x0040;
        const uint PmNoRemove = 0x0000;
        const uint PmQsSendMessage = QsSendMessage << 16;
        const uint WaitObject0 = 0;

        [StructLayout(LayoutKind.Sequential)]
        struct Msg
        {
            public nint Hwnd;
            public uint Message;
            public nuint WParam;
            public nint LParam;
            public uint Time;
            public int X;
            public int Y;
            public uint Private;
        }

        [DllImport("user32.dll")]
        static extern uint MsgWaitForMultipleObjectsEx(uint count, nint[] handles, uint milliseconds, uint wakeMask, uint flags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool PeekMessageW(out Msg message, nint hwnd, uint min, uint max, uint remove);

        public bool HasExited => _exited || _process.HasExited;

        public IEnumerable<int> ProcessIds => new[] { _process.Id };

        /// <summary>Fenêtre annoncée par l'hôte (événement ready), 0 avant.</summary>
        public nint FindWindow() => HasExited ? 0 : _window;

        public void SetBackground(bool background)
        {
            try
            {
                if (!_process.HasExited)
                    _process.PriorityClass = background ? ProcessPriorityClass.BelowNormal : ProcessPriorityClass.Normal;
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
            {
            }
        }

        /// <summary>Fin demandée (« close ») puis forcée après un court délai.</summary>
        public async void Close()
        {
            if (_closed)
                return;
            _closed = true;
            try
            {
                if (await WriteAsync("close").ConfigureAwait(true))
                    await Task.WhenAny(_process.WaitForExitAsync(), Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
            {
            }
            try
            {
                if (!_process.HasExited)
                    _process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
            {
            }
            OnExited();
        }

        /// <summary>Fin du processus : ses dernières lignes (ready, erreur) sont lues avant de conclure.</summary>
        async Task OnProcessExitedAsync()
        {
            await Task.WhenAny(_reading, Task.Delay(TimeSpan.FromSeconds(1))).ConfigureAwait(false);
            Dispatcher.UIThread.Post(OnExited);
        }

        void OnExited()
        {
            if (_exited)
                return;
            _exited = true;
            lock (Running)
                Running.Remove(this);
            FailedToStart = !_closed && !_ready;
            try
            {
                if (_process.HasExited)
                    RuntimeLogBuffer.Append($"[Flash] Moteur intégré arrêté (code {_process.ExitCode}){(FailedToStart ? " avant d'afficher le contenu" : string.Empty)} : {Path.GetFileName(Module)}.");
            }
            catch (InvalidOperationException)
            {
            }
            Exited?.Invoke();
        }

        public static void CloseAll()
        {
            List<FlashHostProcess> all;
            lock (Running)
                all = new List<FlashHostProcess>(Running);
            foreach (FlashHostProcess host in all)
                host.Close();
        }

        // ---------------------------------------------------------------
        // Job Windows : les hôtes disparaissent avec PommeBrowser
        // ---------------------------------------------------------------

        [SupportedOSPlatform("windows")]
        static void AssignToJob(Process process)
        {
            try
            {
                if (_job == 0)
                {
                    nint job = CreateJobObjectW(0, null);
                    if (job == 0)
                        return;
                    var limits = new JobObjectExtendedLimitInformation();
                    limits.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;
                    if (!SetInformationJobObject(job, JobObjectExtendedLimitInformationClass, ref limits, Marshal.SizeOf<JobObjectExtendedLimitInformation>()))
                        return;
                    _job = job;
                }
                AssignProcessToJobObject(_job, process.Handle);
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                RuntimeLogBuffer.Append("[Flash] Job Windows : " + ex.Message);
            }
        }

        const int JobObjectExtendedLimitInformationClass = 9;
        const uint JobObjectLimitKillOnJobClose = 0x2000;

        [StructLayout(LayoutKind.Sequential)]
        struct JobObjectBasicLimitInformation
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public nuint MinimumWorkingSetSize;
            public nuint MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public nuint Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct IoCounters
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct JobObjectExtendedLimitInformation
        {
            public JobObjectBasicLimitInformation BasicLimitInformation;
            public IoCounters IoInfo;
            public nuint ProcessMemoryLimit;
            public nuint JobMemoryLimit;
            public nuint PeakProcessMemoryUsed;
            public nuint PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern nint CreateJobObjectW(nint attributes, string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool SetInformationJobObject(nint job, int infoClass, ref JobObjectExtendedLimitInformation info, int length);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool AssignProcessToJobObject(nint job, nint process);
    }
}
