using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MyHomelabBrowser.classes;

namespace PommeBrowser.Core
{
    /// <summary>
    /// Arrêt brutal de PommeBrowser (plantage dans du code natif, arrêt forcé) : rien ne peut le
    /// consigner sur le moment. Une session en cours laisse une marque, retirée quand le processus
    /// se termine normalement ; la fin du journal de la session est recopiée régulièrement. Au
    /// démarrage suivant, une marque restée signale l'arrêt : il est consigné dans errors.log
    /// avec cette fin de journal et, sous Windows, les entrées du journal d'événements sur
    /// PommeBrowser (module fautif, code d'erreur, pile d'appels de .NET).
    /// </summary>
    public static class CrashWatch
    {
        /// <summary>Fin du journal recopiée (caractères).</summary>
        const int TailChars = 24 * 1024;

        /// <summary>Début du journal gardé en plus (démarrage : exécutable, mises à jour…).</summary>
        const int HeadChars = 3 * 1024;

        /// <summary>Interface sans réponse au-delà de ce délai : noté au journal (gel).</summary>
        static readonly TimeSpan HangDelay = TimeSpan.FromSeconds(5);

        /// <summary>
        /// Opération en cours sur le fil de l'interface qui peut attendre un autre processus
        /// (appel d'un lecteur Flash, fenêtre logée…) : notée si l'interface se fige.
        /// </summary>
        public static string? Activity { get; set; }

        static readonly TimeSpan SaveInterval = TimeSpan.FromSeconds(5);
        static readonly object Gate = new();
        static Timer? _timer;
        static string? _saved;

        static string MarkerPath => AppPaths.SharedData("session.running");

        static string TailPath => AppPaths.SharedData("session-tail.log");

        /// <summary>
        /// Début de session : arrêt brutal de la précédente consigné (tâche rendue, en arrière-plan),
        /// marque posée.
        /// </summary>
        public static Task Start()
        {
            Task report = Task.CompletedTask;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(MarkerPath)!);
                if (File.Exists(MarkerPath))
                {
                    DateTime started = File.GetLastWriteTime(MarkerPath);
                    string? tail = File.Exists(TailPath) ? File.ReadAllText(TailPath) : null;
                    DateTime lastSeen = File.Exists(TailPath) ? File.GetLastWriteTime(TailPath) : started;
                    // Journal d'événements de Windows lu à part : le démarrage n'attend pas.
                    report = Task.Run(() => ReportPreviousCrash(started, lastSeen, tail));
                }
                File.WriteAllText(MarkerPath, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                File.Delete(TailPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return report;
            }
            if (Interlocked.Exchange(ref _started, 1) == 0)
                AppDomain.CurrentDomain.ProcessExit += (_, _) => Stop();
            _timer?.Dispose();
            _timer = new Timer(_ => SaveTail(), null, SaveInterval, SaveInterval);
            return report;
        }

        static int _started;

        /// <summary>Fin normale du processus (y compris relance pour un profil ou une mise à jour) : marque retirée.</summary>
        public static void Stop()
        {
            _timer?.Dispose();
            _timer = null;
            try
            {
                File.Delete(MarkerPath);
                File.Delete(TailPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        /// <summary>
        /// Veille du fil de l'interface : s'il ne répond plus pendant <see cref="HangDelay"/>, le gel
        /// est noté (avec l'opération en cours) et la fin du journal recopiée aussitôt ; son retour
        /// aussi. Un arrêt brutal qui suit un gel n'est donc pas pris pour un plantage.
        /// </summary>
        public static void WatchInterface()
        {
            if (Interlocked.Exchange(ref _watching, 1) != 0)
                return;
            var thread = new Thread(() =>
            {
                while (true)
                {
                    using var answered = new ManualResetEventSlim();
                    Avalonia.Threading.Dispatcher.UIThread.Post(answered.Set, Avalonia.Threading.DispatcherPriority.Send);
                    if (!answered.Wait(HangDelay))
                    {
                        var clock = System.Diagnostics.Stopwatch.StartNew();
                        RuntimeLogBuffer.Append($"[Interface] Ne répond plus depuis {HangDelay.TotalSeconds:0} s" +
                                                (Activity is { } activity ? " (en cours : " + activity + ")." : "."));
                        SaveTail();
                        answered.Wait();
                        RuntimeLogBuffer.Append($"[Interface] De nouveau active après {clock.Elapsed.TotalSeconds + HangDelay.TotalSeconds:0} s.");
                        SaveTail();
                    }
                    Thread.Sleep(2000);
                }
            })
            {
                IsBackground = true,
                Name = "Veille de l'interface"
            };
            thread.Start();
        }

        static int _watching;

        static void SaveTail()
        {
            try
            {
                string snapshot = RuntimeLogBuffer.GetSnapshot();
                // Début de la session (démarrage) et fin : les chargements d'un jeu Flash ne le noient pas.
                string tail = snapshot.Length > HeadChars + TailChars
                    ? snapshot[..HeadChars] + Environment.NewLine + "[…]" + Environment.NewLine + snapshot[^TailChars..]
                    : snapshot;
                lock (Gate)
                {
                    if (tail == _saved)
                        return;
                    File.WriteAllText(TailPath, tail);
                    _saved = tail;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        static void ReportPreviousCrash(DateTime started, DateTime lastSeen, string? tail)
        {
            var text = new StringBuilder()
                .Append("La session ouverte le ").Append(started.ToString("yyyy-MM-dd HH:mm:ss"))
                .Append(" s'est arrêtée sans fermeture normale (vers ").Append(lastSeen.ToString("HH:mm:ss")).AppendLine(").");
            if (OperatingSystem.IsWindows() && WindowsEvents(started) is { Length: > 0 } events)
                text.AppendLine("--- Journal d'événements de Windows ---").AppendLine(events);
            // Outil de mise à jour (Velopack) actif pendant la session : il peut arrêter PommeBrowser.
            if (UpdaterLog(started) is { Length: > 0 } updater)
                text.AppendLine("--- Journal de Velopack (fin) ---").AppendLine(updater);
            if (!string.IsNullOrWhiteSpace(tail))
            {
                // Début coupé au milieu d'une ligne : on repart de la ligne suivante.
                int newline = tail.IndexOf('\n');
                string lines = tail.Length >= TailChars && newline >= 0 ? tail[(newline + 1)..] : tail;
                text.AppendLine("--- Fin du journal de la session ---").Append(lines);
            }
            ErrorLog.WriteText("Arrêt brutal de la session précédente", text.ToString());
        }

        /// <summary>
        /// Fin du journal de Velopack (Velopack.log, à côté du dossier de l'application installée)
        /// s'il a été écrit pendant la session : son outil d'installation arrête de force les
        /// processus du dossier de l'application.
        /// </summary>
        static string? UpdaterLog(DateTime since)
        {
            try
            {
                string current = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string?[] folders = { current, Path.GetDirectoryName(current) };
                foreach (string folder in folders.OfType<string>())
                {
                    if (!Directory.Exists(folder))
                        continue;
                    foreach (string file in Directory.EnumerateFiles(folder, "*.log"))
                    {
                        if (!Path.GetFileName(file).StartsWith("velopack", StringComparison.OrdinalIgnoreCase) ||
                            File.GetLastWriteTime(file) < since.AddMinutes(-2))
                            continue;
                        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        stream.Position = Math.Max(0, stream.Length - 8 * 1024);
                        using var reader = new StreamReader(stream, Encoding.UTF8);
                        string tail = reader.ReadToEnd();
                        int newline = stream.Length > 8 * 1024 ? tail.IndexOf('\n') : -1;
                        return file + Environment.NewLine + (newline >= 0 ? tail[(newline + 1)..] : tail).TrimEnd();
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
            }
            return null;
        }

        /// <summary>
        /// Entrées récentes du journal Application de Windows sur PommeBrowser (erreur
        /// d'application, erreur de .NET, rapport d'erreurs), depuis le début de la session.
        /// </summary>
        static string? WindowsEvents(DateTime since)
        {
            try
            {
                long milliseconds = Math.Clamp((long)(DateTime.Now - since).TotalMilliseconds + 60_000, 60_000, 7L * 24 * 3600 * 1000);
                // Plantage, erreur de .NET, rapport d'erreurs, et gel (« ne répond pas », fermé par Windows).
                string query = "*[System[(Provider[@Name='Application Error'] or Provider[@Name='.NET Runtime'] or " +
                               "Provider[@Name='Windows Error Reporting'] or Provider[@Name='Application Hang']) and " +
                               $"TimeCreated[timediff(@SystemTime) <= {milliseconds}]]]";
                var start = new ProcessStartInfo("wevtutil")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8
                };
                foreach (string argument in new[] { "qe", "Application", "/q:" + query, "/f:text", "/rd:true", "/c:20" })
                    start.ArgumentList.Add(argument);
                using Process? process = Process.Start(start);
                if (process == null)
                    return null;
                Task<string> output = process.StandardOutput.ReadToEndAsync();
                if (!process.WaitForExit(10_000))
                {
                    process.Kill();
                    return null;
                }
                // Programme de PommeBrowser (MyHomelabBrowser.exe une fois installé), ses lecteurs
                // Flash et le moteur web (WebView2), dont l'arrêt fait aussi tomber les pages.
                string[] names =
                {
                    Path.GetFileName(Environment.ProcessPath) ?? "MyHomelabBrowser.exe", "MyHomelabBrowser",
                    "pommebrowser", "PommeFlashHost", "msedgewebview2"
                };
                string[] entries = output.Result.Split("Event[", StringSplitOptions.RemoveEmptyEntries);
                string relevant = string.Join(Environment.NewLine, entries
                    .Where(entry => names.Any(name => entry.Contains(name, StringComparison.OrdinalIgnoreCase)))
                    .Take(6)
                    .Select(entry => "Event[" + entry.Trim()));
                return relevant.Length > 12_000 ? relevant[..12_000] + "…" : relevant;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or AggregateException)
            {
                return null;
            }
        }
    }
}
