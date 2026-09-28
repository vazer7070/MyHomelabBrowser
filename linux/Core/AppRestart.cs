using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace PommeBrowser.Linux.Core
{
    /// <summary>
    /// Relance de PommeBrowser (changement de profil, mise à jour). Le moteur web ouvre les
    /// données d'un seul profil par processus : le plus sûr est de repartir d'un processus neuf.
    /// Un petit script attend la fin du processus actuel (une seule instance à la fois) puis
    /// relance l'AppImage, ou l'exécutable quand PommeBrowser tourne depuis les sources.
    /// </summary>
    public static class AppRestart
    {
        /// <summary>Attente d'au plus 30 s, puis relance quoi qu'il arrive.</summary>
        public const string WaitScript =
            "pid=$1; shift; i=0; " +
            "while kill -0 \"$pid\" 2>/dev/null && [ \"$i\" -lt 150 ]; do sleep 0.2; i=$((i+1)); done; " +
            "exec \"$@\"";

        /// <summary>
        /// Commande de relance. L'AppImage d'origine ($APPIMAGE) passe avant l'exécutable, qui vit
        /// dans un montage temporaire démonté à la sortie. Une AppImage mise à jour est ainsi relancée
        /// dans sa nouvelle version.
        /// </summary>
        public static IReadOnlyList<string> RelaunchCommand(string? appImage, string? processPath, string? entryAssembly)
        {
            if (!string.IsNullOrWhiteSpace(appImage))
                return new[] { appImage };

            if (string.IsNullOrWhiteSpace(processPath))
                throw new InvalidOperationException("Exécutable introuvable.");

            // Lancé par « dotnet pommebrowser.dll » : l'hôte .NET a besoin de la bibliothèque.
            if (Path.GetFileNameWithoutExtension(processPath) == "dotnet" && !string.IsNullOrWhiteSpace(entryAssembly))
                return new[] { processPath, entryAssembly };

            return new[] { processPath };
        }

        public static ProcessStartInfo HelperStartInfo(int pid, IReadOnlyList<string> command)
        {
            var start = new ProcessStartInfo("/bin/sh") { UseShellExecute = false };
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add(WaitScript);
            start.ArgumentList.Add("pommebrowser-restart");
            start.ArgumentList.Add(pid.ToString(System.Globalization.CultureInfo.InvariantCulture));
            foreach (string part in command)
                start.ArgumentList.Add(part);
            return start;
        }

        /// <summary>Prépare la relance ; le processus actuel doit se terminer ensuite.</summary>
        public static void Schedule()
        {
            IReadOnlyList<string> command = RelaunchCommand(
                Environment.GetEnvironmentVariable("APPIMAGE"),
                Environment.ProcessPath,
                typeof(AppRestart).Assembly.Location);

            using Process? helper = Process.Start(HelperStartInfo(Environment.ProcessId, command));
            if (helper == null)
                throw new InvalidOperationException("Relance impossible.");
        }
    }
}
