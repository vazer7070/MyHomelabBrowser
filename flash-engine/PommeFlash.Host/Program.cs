using System.Reflection;
using PommeFlash.Host;
using PommeFlash.Host.Native;

[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows")]

namespace PommeFlash.Host
{
    /// <summary>
    /// Hôte du moteur Flash intégré. Codes de sortie : 0 fin normale, 2 paramètres invalides,
    /// 3 système non pris en charge, 4 module Flash impossible à charger, 5 contenu refusé par le module.
    /// </summary>
    static class Program
    {
        static PluginLibrary? _library;
        static PluginInstance? _instance;
        static bool _closing;

        static int Main(string[] args)
        {
            if (!OperatingSystem.IsWindows())
            {
                Console.Error.WriteLine("PommeFlashHost ne fonctionne que sous Windows.");
                return 3;
            }

            HostOptions options;
            try
            {
                options = HostOptions.Parse(args);
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException)
            {
                HostChannel.Error(ex.Message);
                return 2;
            }

            string version = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";
            HostChannel.Log($"PommeFlashHost {version}, module {Path.GetFileName(options.PluginPath)} ({ModuleVersion(options.PluginPath)})");
            HostChannel.Log($"Contenu {options.Swf.GetLeftPart(UriPartial.Path)}, page {options.Page.GetLeftPart(UriPartial.Path)}, {options.Width}×{options.Height}");
            PluginInstance.SetUserAgent(options.UserAgent);

            try
            {
                HostWindow.Create(options);
                _library = PluginLibrary.Load(options.PluginPath);
            }
            catch (Exception ex)
            {
                HostChannel.Error(ex.Message);
                return 4;
            }

            try
            {
                _instance = new PluginInstance(_library, options);
                HostWindow.Attach(_instance, Close);
                _instance.Create();
                (int width, int height) = HostWindow.ClientSize(HostWindow.PluginWindow);
                _instance.SetWindow(HostWindow.PluginWindow, width, height);
                _instance.StartSource();
            }
            catch (Exception ex)
            {
                HostChannel.Error(ex.Message);
                return 5;
            }

            HostChannel.Send("ready", ("window", (long)HostWindow.Frame));
            HostChannel.StartReading(
                command => UiThread.Post(() => OnCommand(command)),
                () => UiThread.Post(Close));

            int code = HostWindow.Run();
            try
            {
                _library.Shutdown();
            }
            catch (Exception ex)
            {
                HostChannel.Error("NP_Shutdown : " + ex.Message);
            }
            HostChannel.Send("exit", ("code", code));
            return code;
        }

        static void OnCommand(string command)
        {
            switch (command)
            {
                case "close":
                    Close();
                    break;
                default:
                    HostChannel.Log("Commande inconnue : " + command);
                    break;
            }
        }

        /// <summary>Version du module (ressource du fichier), pour le journal.</summary>
        static string ModuleVersion(string path)
        {
            try
            {
                System.Diagnostics.FileVersionInfo info = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
                string version = info.FileVersion?.Replace(',', '.').Replace(" ", string.Empty) ?? "version inconnue";
                return string.IsNullOrEmpty(info.ProductName) ? version : info.ProductName + " " + version;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return "version illisible";
            }
        }

        /// <summary>Fin : l'instance est détruite avant sa fenêtre (NPP_Destroy, puis la fenêtre).</summary>
        static void Close()
        {
            if (_closing)
                return;
            _closing = true;
            try
            {
                _instance?.Destroy();
            }
            catch (Exception ex)
            {
                HostChannel.Error("NPP_Destroy : " + ex.Message);
            }
            Win32.DestroyWindow(HostWindow.Frame);
        }
    }
}
