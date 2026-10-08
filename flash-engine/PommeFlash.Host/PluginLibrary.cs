using System.Runtime.InteropServices;
using PommeFlash.Host.Native;

namespace PommeFlash.Host
{
    /// <summary>
    /// Module NPAPI chargé dans l'hôte : NP_GetEntryPoints puis NP_Initialize sous Windows,
    /// NP_Initialize seul sous Linux (il remplit aussi la table du module), et ses fonctions NPP_*.
    /// </summary>
    sealed unsafe class PluginLibrary
    {
        readonly nint _module;
        readonly NPPluginFuncs* _funcs;

        PluginLibrary(nint module, NPPluginFuncs* funcs)
        {
            _module = module;
            _funcs = funcs;
        }

        public string Path { get; private init; } = string.Empty;

        public static PluginLibrary Load(string path)
        {
            var funcs = (NPPluginFuncs*)NpMemory.AllocZeroed((nuint)sizeof(NPPluginFuncs));
            funcs->size = (ushort)sizeof(NPPluginFuncs);
            nint module;
            short error;
            if (OperatingSystem.IsWindows())
            {
                // Les bibliothèques voisines du module sont cherchées dans son dossier.
                module = Win32.LoadLibraryExW(path, 0, Win32.LOAD_WITH_ALTERED_SEARCH_PATH);
                if (module == 0)
                    throw new InvalidOperationException($"Module Flash impossible à charger (erreur {Marshal.GetLastPInvokeError()}) : {path}");

                nint getEntryPoints = Win32.GetProcAddress(module, "NP_GetEntryPoints");
                nint initialize = Win32.GetProcAddress(module, "NP_Initialize");
                if (getEntryPoints == 0 || initialize == 0)
                    throw new InvalidOperationException("Ce fichier n'est pas un module NPAPI (NP_GetEntryPoints ou NP_Initialize absent).");

                error = ((delegate* unmanaged[Stdcall]<NPPluginFuncs*, short>)getEntryPoints)(funcs);
                if (error != Np.NoError)
                    throw new InvalidOperationException($"NP_GetEntryPoints a échoué ({error}).");
                error = ((delegate* unmanaged[Stdcall]<nint, short>)initialize)(BrowserFunctions.Table);
            }
            else
            {
                try
                {
                    module = NativeLibrary.Load(path);
                }
                catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException)
                {
                    // Le message de dlopen nomme la bibliothèque système manquante (GTK 2, NSS…).
                    throw new InvalidOperationException($"Module Flash impossible à charger : {ex.Message}");
                }
                if (!NativeLibrary.TryGetExport(module, "NP_Initialize", out nint initialize))
                    throw new InvalidOperationException("Ce fichier n'est pas un module NPAPI (NP_Initialize absent).");
                error = ((delegate* unmanaged[Cdecl]<nint, NPPluginFuncs*, short>)initialize)(BrowserFunctions.Table, funcs);
            }
            if (error != Np.NoError)
                throw new InvalidOperationException($"NP_Initialize a échoué ({error}).");

            return new PluginLibrary(module, funcs) { Path = path };
        }

        /// <summary>Fin du module (après la destruction des instances). La bibliothèque reste chargée.</summary>
        public void Shutdown()
        {
            if (OperatingSystem.IsWindows())
            {
                nint shutdown = Win32.GetProcAddress(_module, "NP_Shutdown");
                if (shutdown != 0)
                    ((delegate* unmanaged[Stdcall]<short>)shutdown)();
            }
            else if (NativeLibrary.TryGetExport(_module, "NP_Shutdown", out nint shutdown))
            {
                ((delegate* unmanaged[Cdecl]<short>)shutdown)();
            }
        }

        public short New(nint mimeType, NPP_t* npp, ushort mode, short argc, nint* argn, nint* argv)
            => _funcs->newp == 0 ? Np.InvalidFuncTableError
                : ((delegate* unmanaged[Cdecl]<nint, NPP_t*, ushort, short, nint*, nint*, nint, short>)_funcs->newp)(mimeType, npp, mode, argc, argn, argv, 0);

        public short Destroy(NPP_t* npp)
        {
            if (_funcs->destroy == 0)
                return Np.NoError;
            nint saved = 0;
            short error = ((delegate* unmanaged[Cdecl]<NPP_t*, nint*, short>)_funcs->destroy)(npp, &saved);
            // Données « sauvegardées » : sans usage ici, libérées.
            if (saved != 0)
            {
                NpMemory.Free(*(nint*)(saved + sizeof(nint)));
                NpMemory.Free(saved);
            }
            return error;
        }

        /// <summary><paramref name="window"/> : NPWindow, ou NPWindowUnix sous Linux.</summary>
        public short SetWindow(NPP_t* npp, nint window)
            => _funcs->setwindow == 0 ? Np.NoError : ((delegate* unmanaged[Cdecl]<NPP_t*, nint, short>)_funcs->setwindow)(npp, window);

        public short NewStream(NPP_t* npp, nint mimeType, NPStream* stream, bool seekable, ushort* type)
            => ((delegate* unmanaged[Cdecl]<NPP_t*, nint, NPStream*, byte, ushort*, short>)_funcs->newstream)(npp, mimeType, stream, seekable ? (byte)1 : (byte)0, type);

        public short DestroyStream(NPP_t* npp, NPStream* stream, short reason)
            => _funcs->destroystream == 0 ? Np.NoError : ((delegate* unmanaged[Cdecl]<NPP_t*, NPStream*, short, short>)_funcs->destroystream)(npp, stream, reason);

        public void StreamAsFile(NPP_t* npp, NPStream* stream, nint path)
        {
            if (_funcs->asfile != 0)
                ((delegate* unmanaged[Cdecl]<NPP_t*, NPStream*, nint, void>)_funcs->asfile)(npp, stream, path);
        }

        public int WriteReady(NPP_t* npp, NPStream* stream)
            => _funcs->writeready == 0 ? int.MaxValue : ((delegate* unmanaged[Cdecl]<NPP_t*, NPStream*, int>)_funcs->writeready)(npp, stream);

        public int Write(NPP_t* npp, NPStream* stream, int offset, int length, byte* buffer)
            => _funcs->write == 0 ? length : ((delegate* unmanaged[Cdecl]<NPP_t*, NPStream*, int, int, byte*, int>)_funcs->write)(npp, stream, offset, length, buffer);

        public void UrlNotify(NPP_t* npp, nint url, short reason, nint notifyData)
        {
            if (_funcs->urlnotify != 0)
                ((delegate* unmanaged[Cdecl]<NPP_t*, nint, short, nint, void>)_funcs->urlnotify)(npp, url, reason, notifyData);
        }

        public short GetValue(NPP_t* npp, NPPVariable variable, void* value)
            => _funcs->getvalue == 0 ? Np.GenericError : ((delegate* unmanaged[Cdecl]<NPP_t*, int, void*, short>)_funcs->getvalue)(npp, (int)variable, value);

        public bool HasNewStream => _funcs->newstream != 0;

        /// <summary>Le module veut être consulté avant chaque redirection de ses chargements notifiés.</summary>
        public bool HandlesRedirects => _funcs->urlredirectnotify != 0;

        public void UrlRedirectNotify(NPP_t* npp, nint url, int status, nint notifyData)
        {
            if (_funcs->urlredirectnotify != 0)
                ((delegate* unmanaged[Cdecl]<NPP_t*, nint, int, nint, void>)_funcs->urlredirectnotify)(npp, url, status, notifyData);
        }
    }
}
