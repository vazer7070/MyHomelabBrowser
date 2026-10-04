using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace PommeBrowser.Core
{
    /// <summary>
    /// Une seule instance de PommeBrowser par utilisateur, comme Firefox et Chrome : un nouveau
    /// lancement (lien ouvert depuis une autre application, fichier, icône) transmet ses adresses
    /// à l'instance déjà ouverte, qui les ouvre dans des onglets, puis s'arrête. Sans cela, il
    /// démarrerait un second navigateur sur le même profil.
    /// La première instance est désignée par un verrou que le système rend à la fin du processus
    /// (mutex nommé sous Windows, verrou de fichier ailleurs) ; le canal est réservé à
    /// l'utilisateur : tube nommé CurrentUserOnly (Windows), socket Unix dans un dossier 0700.
    /// </summary>
    public sealed class SingleInstance : IDisposable
    {
        const string Greeting = "pommebrowser-instance/1";
        const int MaxMessageBytes = 256 * 1024;
        const int MaxTargets = 64;
        static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(4);
        static readonly TimeSpan ExchangeTimeout = TimeSpan.FromSeconds(5);

        readonly string _name;
        readonly object _gate = new();
        readonly List<IReadOnlyList<string>> _queued = new();
        readonly CancellationTokenSource _stop = new();
        Action<IReadOnlyList<string>>? _handler;
        // Windows : fil qui garde le mutex ; ailleurs : verrou de fichier et socket.
        Thread? _owner;
        ManualResetEventSlim? _release;
        FileStream? _lock;
        Socket? _listener;
        string? _socketPath;

        SingleInstance(string name) => _name = name;

        /// <summary>
        /// Première instance : écoute les suivantes (<see cref="Attach"/> reçoit leurs adresses).
        /// Instance suivante : transmet <paramref name="targets"/> et renvoie null (le programme
        /// s'arrête). Si l'instance ouverte ne répond pas, ou si le canal est indisponible, le
        /// lancement continue comme avant (instance sans écoute).
        /// </summary>
        public static SingleInstance? Claim(string name, IReadOnlyList<string> targets, Action<string>? log = null)
        {
            var instance = new SingleInstance(name);
            // Deux lancements simultanés : l'un obtient le verrou, l'autre transmet, après que le
            // premier a ouvert son canal (quelques essais).
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    if (instance.TryOwn())
                    {
                        instance.Listen();
                        return instance;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SocketException or PlatformNotSupportedException)
                {
                    log?.Invoke("Instance unique indisponible : " + ex.Message);
                    instance.Dispose();
                    return new SingleInstance(name);
                }

                if (Forward(name, targets, out string? problem))
                {
                    instance.Dispose();
                    return null;
                }
                log?.Invoke("Instance ouverte injoignable : " + problem);
                Thread.Sleep(150);
            }
            // L'instance ouverte ne répond pas : démarrage indépendant, comme avant.
            return instance;
        }

        /// <summary>
        /// Gestionnaire des adresses reçues d'autres lancements (sur un fil à part) ; celles reçues
        /// avant, pendant le démarrage, lui sont remises aussitôt.
        /// </summary>
        public void Attach(Action<IReadOnlyList<string>> handler)
        {
            List<IReadOnlyList<string>> queued;
            lock (_gate)
            {
                _handler = handler;
                queued = new List<IReadOnlyList<string>>(_queued);
                _queued.Clear();
            }
            foreach (IReadOnlyList<string> targets in queued)
                handler(targets);
        }

        void Deliver(IReadOnlyList<string> targets)
        {
            Action<IReadOnlyList<string>>? handler;
            lock (_gate)
            {
                handler = _handler;
                if (handler == null)
                {
                    _queued.Add(targets);
                    return;
                }
            }
            handler(targets);
        }

        /// <summary>Nom du canal pour ce dossier de données et cet utilisateur.</summary>
        public static string ChannelName(string dataRoot)
        {
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(Environment.UserName + "\n" + Path.GetFullPath(dataRoot)));
            return "pommebrowser-" + Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
        }

        // ---------------------------------------------------------------
        // Première instance
        // ---------------------------------------------------------------

        bool TryOwn()
        {
            if (OperatingSystem.IsWindows())
                return TryOwnMutex();

            string directory = UnixDirectory(_name);
            string lockPath = Path.Combine(directory, "instance.lock");
            try
            {
                // FileShare.None : verrou exclusif (flock), rendu par le système à la fin du processus.
                _lock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                return false;
            }
            _socketPath = Path.Combine(directory, "instance.sock");
            return true;
        }

        [SupportedOSPlatform("windows")]
        bool TryOwnMutex()
        {
            // Le mutex appartient à un fil : un fil à part le garde jusqu'à la fin.
            bool owned = false;
            var decided = new ManualResetEventSlim();
            var release = new ManualResetEventSlim();
            var thread = new Thread(() =>
            {
                using var mutex = new Mutex(false, @"Local\" + _name);
                try
                {
                    owned = mutex.WaitOne(0);
                }
                catch (AbandonedMutexException)
                {
                    // L'instance précédente s'est arrêtée sans le rendre : il est à nous.
                    owned = true;
                }
                decided.Set();
                if (!owned)
                    return;
                release.Wait();
                mutex.ReleaseMutex();
            })
            {
                IsBackground = true,
                Name = "Instance unique"
            };
            thread.Start();
            decided.Wait();
            decided.Dispose();
            if (!owned)
            {
                release.Dispose();
                return false;
            }
            _owner = thread;
            _release = release;
            return true;
        }

        void Listen()
        {
            if (!OperatingSystem.IsWindows())
            {
                // Le verrou est à nous : une socket restée là vient d'une instance arrêtée.
                File.Delete(_socketPath!);
                _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                _listener.Bind(new UnixDomainSocketEndPoint(_socketPath!));
                File.SetUnixFileMode(_socketPath!, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                _listener.Listen(8);
            }
            var thread = new Thread(AcceptLoop)
            {
                IsBackground = true,
                Name = "Instance unique : liens reçus"
            };
            thread.Start();
        }

        void AcceptLoop()
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    if (OperatingSystem.IsWindows())
                    {
                        using NamedPipeServerStream pipe = CreatePipe();
                        pipe.WaitForConnectionAsync(_stop.Token).GetAwaiter().GetResult();
                        Serve(pipe);
                    }
                    else
                    {
                        using Socket client = _listener!.Accept();
                        client.ReceiveTimeout = (int)ExchangeTimeout.TotalMilliseconds;
                        client.SendTimeout = (int)ExchangeTimeout.TotalMilliseconds;
                        using var stream = new NetworkStream(client);
                        Serve(stream);
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (Exception ex) when (ex is IOException or SocketException or InvalidOperationException)
                {
                    // Un lancement qui abandonne en route n'arrête pas l'écoute.
                    if (_stop.IsCancellationRequested)
                        return;
                }
            }
        }

        [SupportedOSPlatform("windows")]
        NamedPipeServerStream CreatePipe()
            => new(_name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        /// <summary>Échange avec un lancement : salut (numéro du processus), adresses, accusé.</summary>
        void Serve(Stream stream)
        {
            var writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = true };
            writer.WriteLine(Greeting + " " + Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            string? line = ReadLine(stream);
            if (line == null || ParseRequest(line) is not { } targets)
                return;
            writer.WriteLine("ok");
            Deliver(targets);
        }

        // ---------------------------------------------------------------
        // Instance suivante
        // ---------------------------------------------------------------

        static bool Forward(string name, IReadOnlyList<string> targets, out string? problem)
        {
            problem = null;
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
                    pipe.Connect((int)ConnectTimeout.TotalMilliseconds);
                    return Exchange(pipe, targets, out problem);
                }

                string socketPath = Path.Combine(UnixDirectory(name), "instance.sock");
                using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified)
                {
                    ReceiveTimeout = (int)ExchangeTimeout.TotalMilliseconds,
                    SendTimeout = (int)ExchangeTimeout.TotalMilliseconds
                };
                var clock = Stopwatch.StartNew();
                while (true)
                {
                    try
                    {
                        socket.Connect(new UnixDomainSocketEndPoint(socketPath));
                        break;
                    }
                    catch (SocketException) when (clock.Elapsed < ConnectTimeout)
                    {
                        // L'instance ouverte démarre encore : son canal arrive.
                        Thread.Sleep(100);
                    }
                }
                using var stream = new NetworkStream(socket);
                return Exchange(stream, targets, out problem);
            }
            catch (Exception ex) when (ex is IOException or SocketException or TimeoutException or UnauthorizedAccessException)
            {
                problem = ex.Message;
                return false;
            }
        }

        static bool Exchange(Stream stream, IReadOnlyList<string> targets, out string? problem)
        {
            problem = null;
            string? greeting = ReadLine(stream);
            if (greeting == null || !greeting.StartsWith(Greeting + " ", StringComparison.Ordinal))
            {
                problem = "réponse inattendue";
                return false;
            }
            // Windows : la fenêtre de l'instance ouverte a le droit de passer au premier plan.
            if (OperatingSystem.IsWindows() &&
                int.TryParse(greeting.AsSpan(Greeting.Length + 1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int pid))
            {
                AllowSetForegroundWindow(pid);
            }
            var writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = true };
            writer.WriteLine(FormatRequest(targets));
            if (ReadLine(stream) != "ok")
            {
                problem = "adresses non reçues";
                return false;
            }
            return true;
        }

        // ---------------------------------------------------------------
        // Messages
        // ---------------------------------------------------------------

        /// <summary>Demande d'ouverture : {"open":[…]} sur une ligne.</summary>
        public static string FormatRequest(IReadOnlyList<string> targets)
            => JsonSerializer.Serialize(new Dictionary<string, IReadOnlyList<string>> { ["open"] = targets });

        /// <summary>Adresses d'une demande ; null si elle est invalide. Données d'un autre processus : bornées.</summary>
        public static IReadOnlyList<string>? ParseRequest(string line)
        {
            if (line.Length > MaxMessageBytes)
                return null;
            try
            {
                using JsonDocument document = JsonDocument.Parse(line);
                if (document.RootElement.ValueKind != JsonValueKind.Object ||
                    !document.RootElement.TryGetProperty("open", out JsonElement open) || open.ValueKind != JsonValueKind.Array)
                {
                    return null;
                }
                var targets = new List<string>();
                foreach (JsonElement item in open.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 and <= 8192 } target && targets.Count < MaxTargets)
                        targets.Add(target);
                }
                return targets;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>Une ligne (UTF-8), sans dépasser la taille maximale ; null à la fin du flux.</summary>
        static string? ReadLine(Stream stream)
        {
            var bytes = new List<byte>(256);
            Span<byte> one = stackalloc byte[1];
            while (bytes.Count <= MaxMessageBytes)
            {
                int read = stream.Read(one);
                if (read == 0)
                    return bytes.Count > 0 ? Encoding.UTF8.GetString(bytes.ToArray()) : null;
                if (one[0] == (byte)'\n')
                    return Encoding.UTF8.GetString(bytes.ToArray());
                bytes.Add(one[0]);
            }
            return null;
        }

        /// <summary>
        /// Dossier du canal sous Linux et macOS, réservé à l'utilisateur (0700) : celui de la
        /// session (XDG_RUNTIME_DIR), sinon le dossier temporaire de l'utilisateur. Un dossier
        /// existant qui ne serait pas réservé à l'utilisateur est refusé.
        /// </summary>
        [UnsupportedOSPlatform("windows")]
        static string UnixDirectory(string name)
        {
            string? runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
            string root = !string.IsNullOrEmpty(runtime) && Directory.Exists(runtime) ? runtime : Path.GetTempPath();
            string directory = Path.Combine(root, name);
            // Une socket Unix a un chemin limité (108 octets sous Linux, 104 sous macOS).
            if (Encoding.UTF8.GetByteCount(Path.Combine(directory, "instance.sock")) > 100)
                directory = Path.Combine("/tmp", name);

            const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            Directory.CreateDirectory(directory, Private);
            if (File.GetUnixFileMode(directory) != Private)
                throw new UnauthorizedAccessException("Dossier du canal non réservé à l'utilisateur : " + directory);
            return directory;
        }

        public void Dispose()
        {
            _stop.Cancel();
            try
            {
                _listener?.Dispose();
                if (_listener != null && _socketPath != null)
                    File.Delete(_socketPath);
            }
            catch (IOException)
            {
            }
            _lock?.Dispose();
            _release?.Set();
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool AllowSetForegroundWindow(int processId);
    }
}
