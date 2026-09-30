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

        /// <summary>Consigne une erreur : type, code, message, pile d'appels et erreurs internes.</summary>
        public static void Write(string context, Exception? exception)
        {
            if (exception == null)
                return;
            RuntimeLogBuffer.Append($"[{context}] {exception.GetType().Name} (0x{exception.HResult:X8}) : {exception.Message}");

            var entry = new StringBuilder()
                .Append("=== ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append(" · ").Append(context)
                .Append(" · PommeBrowser ").Append(typeof(ErrorLog).Assembly.GetName().Version)
                .Append(" · ").AppendLine(Environment.OSVersion.VersionString)
                .AppendLine(exception.ToString())
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
