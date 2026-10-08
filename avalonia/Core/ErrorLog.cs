using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using MyHomelabBrowser.classes;

namespace PommeBrowser.Core
{
    /// <summary>
    /// Erreurs imprévues : journal en mémoire (rapports, page Diagnostic) et fichier errors.log des
    /// données communes, qui survit à un arrêt brutal. Le filet de sécurité du fil de l'interface
    /// est posé par BrowserApp (voir BrowserApp.OnUnhandledException).
    /// </summary>
    public static class ErrorLog
    {
        const long MaxBytes = 512 * 1024;
        static readonly object Gate = new();

        /// <summary>Fichier du journal (errors.log dans les données communes à tous les profils).</summary>
        public static string FilePath => AppPaths.SharedData("errors.log");

        /// <summary>Erreurs hors du fil de l'interface : tâches oubliées et arrêt fatal.</summary>
        public static void InstallProcessHandlers()
        {
            AppDomain.CurrentDomain.UnhandledException += (_, e) => Write("Erreur fatale", e.ExceptionObject as Exception);
            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                Write("Tâche", e.Exception);
                e.SetObserved();
            };
        }

        /// <summary>
        /// Fin du journal (au plus <paramref name="maxBytes"/> octets, à partir d'un début de ligne),
        /// pour les rapports ; null s'il est vide ou illisible.
        /// </summary>
        public static string? ReadTail(int maxBytes)
        {
            try
            {
                lock (Gate)
                {
                    string path = FilePath;
                    if (!File.Exists(path))
                        return null;
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    long start = Math.Max(0, stream.Length - maxBytes);
                    stream.Position = start;
                    using var reader = new StreamReader(stream, Encoding.UTF8);
                    string text = reader.ReadToEnd();
                    // Début coupé au milieu d'une ligne (ou d'un caractère) : on repart de la ligne suivante.
                    if (start > 0 && text.IndexOf('\n') is var newline and >= 0)
                        text = text[(newline + 1)..];
                    return text.Length > 0 ? text : null;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        /// <summary>Consigne une erreur : type, code, message, pile d'appels et erreurs internes.</summary>
        public static void Write(string context, Exception? exception)
        {
            if (exception == null)
                return;
            RuntimeLogBuffer.Append($"[{context}] {exception.GetType().Name} (0x{exception.HResult:X8}) : {exception.Message}");
            Append(context, exception.ToString());
        }

        /// <summary>Consigne un événement décrit par un texte (arrêt brutal de la session précédente…).</summary>
        public static void WriteText(string context, string text)
        {
            RuntimeLogBuffer.Append($"[{context}] voir errors.log");
            Append(context, text.TrimEnd());
        }

        static void Append(string context, string details)
        {
            var entry = new StringBuilder()
                .Append("=== ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append(" · ").Append(context)
                .Append(" · PommeBrowser ").Append(typeof(ErrorLog).Assembly.GetName().Version)
                .Append(" · ").AppendLine(Environment.OSVersion.VersionString)
                .AppendLine(details)
                .AppendLine();
            try
            {
                lock (Gate)
                {
                    string path = FilePath;
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    // Journal borné : au-delà, les entrées les plus anciennes sont abandonnées.
                    if (File.Exists(path) && new FileInfo(path).Length > MaxBytes)
                    {
                        string text = File.ReadAllText(path);
                        File.WriteAllText(path, text[(text.Length / 2)..]);
                    }
                    File.AppendAllText(path, entry.ToString());
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Journal impossible à écrire : l'erreur reste dans le journal en mémoire.
            }
        }
    }
}
