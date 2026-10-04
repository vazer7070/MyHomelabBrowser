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

        static void SaveTail()
        {
            try
            {
                string snapshot = RuntimeLogBuffer.GetSnapshot();
                string tail = snapshot.Length > TailChars ? snapshot[^TailChars..] : snapshot;
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
        /// Entrées récentes du journal Application de Windows sur PommeBrowser (erreur
        /// d'application, erreur de .NET, rapport d'erreurs), depuis le début de la session.
        /// </summary>
        static string? WindowsEvents(DateTime since)
        {
            try
            {
                long milliseconds = Math.Clamp((long)(DateTime.Now - since).TotalMilliseconds + 60_000, 60_000, 7L * 24 * 3600 * 1000);
                string query = "*[System[(Provider[@Name='Application Error'] or Provider[@Name='.NET Runtime'] or " +
                               $"Provider[@Name='Windows Error Reporting']) and TimeCreated[timediff(@SystemTime) <= {milliseconds}]]]";
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
                string[] entries = output.Result.Split("Event[", StringSplitOptions.RemoveEmptyEntries);
                string relevant = string.Join(Environment.NewLine, entries
                    .Where(entry => entry.Contains("pommebrowser", StringComparison.OrdinalIgnoreCase) ||
                                    entry.Contains("PommeFlashHost", StringComparison.OrdinalIgnoreCase))
                    .Take(4)
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
