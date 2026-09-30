using System;
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
    /// Moteur Flash intégré : PommeFlashHost.exe (voir flash-engine/), qui charge le module Flash
    /// de l'utilisateur et affiche le contenu dans une fenêtre que l'onglet loge comme celle de
    /// Basilisk. Échanges : événements JSON sur sa sortie standard, commandes sur son entrée.
    /// Le processus est enfermé dans un job Windows : il ne survit jamais à PommeBrowser.
    /// </summary>
    [SupportedOSPlatform("windows")]
    sealed class FlashHostProcess : ILegacyBrowser
    {
        static readonly List<FlashHostProcess> Running = new();
        static nint _job;

        readonly Process _process;
        // Écritures sur l'entrée de l'hôte (réponses aux scripts, fermeture) : une à la fois.
        readonly SemaphoreSlim _input = new(1, 1);
        nint _window;
        bool _closed;
        bool _exited;

        FlashHostProcess(Process process)
        {
            _process = process;
        }

        /// <summary>
        /// Hôte livré avec PommeBrowser pour ce module : un processus ne charge que les modules de
        /// son architecture (flash\PommeFlashHost.exe en 64 bits, flash\x86\PommeFlashHost.exe en 32 bits).
        /// </summary>
        public static string ExecutablePath(string module)
            => FlashModuleSearch.Is32BitModuleName(module)
                ? Path.Combine(AppContext.BaseDirectory, "flash", "x86", "PommeFlashHost.exe")
                : Path.Combine(AppContext.BaseDirectory, "flash", "PommeFlashHost.exe");

        /// <summary>Un hôte est livré dans cette compilation (au moins en 64 bits).</summary>
        public static bool IsAvailable => File.Exists(Path.Combine(AppContext.BaseDirectory, "flash", "PommeFlashHost.exe"));

        /// <summary>L'hôte de l'architecture de ce module est livré.</summary>
        public static bool IsAvailableFor(string module) => File.Exists(ExecutablePath(module));

        public event Action? Exited;

        /// <summary>Le contenu demande une page (cible _blank, _self…).</summary>
        public event Action<Uri, string>? NavigateRequested;

        /// <summary>
        /// Script à exécuter dans la page (ExternalInterface.call, adresse javascript:). Avec un
        /// numéro, l'hôte attend la réponse (<see cref="ReplyScript"/>) ; sans, aucune.
        /// </summary>
        public event Action<int?, string>? ScriptRequested;

        public static FlashHostProcess Start(FlashContent content, string module, bool isPrivate)
        {
            string executable = ExecutablePath(module);
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
            AssignToJob(process);
            process.EnableRaisingEvents = true;

            var host = new FlashHostProcess(process);
            lock (Running)
                Running.Add(host);
            process.Exited += (_, _) => Dispatcher.UIThread.Post(host.OnExited);
            _ = Task.Run(host.ReadEventsAsync);
            _ = Task.Run(host.DrainErrorsAsync);
            RuntimeLogBuffer.Append($"[Flash] Moteur intégré lancé (PID {process.Id}) : {content.Swf.GetLeftPart(UriPartial.Path)} avec {Path.GetFileName(module)}");
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
                        Dispatcher.UIThread.Post(() =>
                        {
                            if (ScriptRequested is { } handler)
                                handler(id, evaluated);
                            else
                                ReplyScript(id, false, null);
                        });
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

        /// <summary>Réponse à un script de la page : « result &lt;id&gt; {"ok":…,"value":…} ».</summary>
        public void ReplyScript(int id, bool ok, string? value)
        {
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

        void OnExited()
        {
            if (_exited)
                return;
            _exited = true;
            lock (Running)
                Running.Remove(this);
            try
            {
                if (_process.HasExited)
                    RuntimeLogBuffer.Append($"[Flash] Moteur intégré arrêté (code {_process.ExitCode}).");
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
