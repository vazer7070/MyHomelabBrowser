using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using PommeBrowser.Linux.Core;

namespace PommeBrowser.Core
{
    /// <summary>
    /// Relance de PommeBrowser (changement de profil, langue, mise à jour). Le moteur web ouvre
    /// les données d'un seul profil par processus : le plus sûr est de repartir d'un processus
    /// neuf. Le nouveau processus attend la fin de l'actuel (--wait-pid) avant de démarrer.
    /// </summary>
    public static class Relauncher
    {
        public const string WaitArgument = "--wait-pid";

        /// <summary>Lance le nouveau processus ; l'actuel doit se terminer ensuite.</summary>
        public static void Schedule()
        {
            IReadOnlyList<string> command = AppRestart.RelaunchCommand(
                OperatingSystem.IsLinux() ? Environment.GetEnvironmentVariable("APPIMAGE") : null,
                Environment.ProcessPath,
                typeof(Relauncher).Assembly.Location);

            var start = new ProcessStartInfo(command[0]) { UseShellExecute = false };
            for (int i = 1; i < command.Count; i++)
                start.ArgumentList.Add(command[i]);
            start.ArgumentList.Add(WaitArgument);
            start.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));

            using Process? process = Process.Start(start);
            if (process == null)
                throw new InvalidOperationException("Relance impossible.");
        }

        /// <summary>
        /// Démarrage : si l'instance précédente se ferme encore, l'attendre (30 s au plus).
        /// Renvoie les arguments sans --wait-pid.
        /// </summary>
        public static string[] WaitForPrevious(string[] args)
        {
            int index = Array.IndexOf(args, WaitArgument);
            if (index < 0 || index + 1 >= args.Length)
                return args;

            if (int.TryParse(args[index + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int pid) && pid != Environment.ProcessId)
            {
                try
                {
                    using Process previous = Process.GetProcessById(pid);
                    previous.WaitForExit(30_000);
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // Déjà terminé.
                }
                // Laisse le temps au système de libérer les verrous (données du moteur).
                Thread.Sleep(200);
            }

            var rest = new List<string>(args);
            rest.RemoveRange(index, 2);
            return rest.ToArray();
        }
    }
}
