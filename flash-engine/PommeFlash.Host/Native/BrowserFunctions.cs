using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace PommeFlash.Host.Native
{
    /// <summary>
    /// Fonctions du navigateur offertes au module (NPNetscapeFuncs), dans l'ordre de npfunctions.h.
    /// Chacune retrouve l'instance concernée par son NPP, puis la laisse répondre. Aucune
    /// exception ne doit remonter dans le module : elles sont journalisées.
    /// </summary>
    static unsafe class BrowserFunctions
    {
        const int PointerCount = 58;
        static nint _table;

        /// <summary>Table remplie une fois, passée à NP_Initialize.</summary>
        public static nint Table
        {
            get
            {
                if (_table != 0)
                    return _table;

                nint[] functions =
                {
                    (nint)(delegate* unmanaged<nint, nint, nint, short>)&GetUrl,
                    (nint)(delegate* unmanaged<nint, nint, nint, uint, nint, byte, short>)&PostUrl,
                    (nint)(delegate* unmanaged<nint, nint, short>)&RequestRead,
                    (nint)(delegate* unmanaged<nint, nint, nint, nint, short>)&NewStream,
                    (nint)(delegate* unmanaged<nint, nint, int, nint, int>)&Write,
                    (nint)(delegate* unmanaged<nint, nint, short, short>)&DestroyStream,
                    (nint)(delegate* unmanaged<nint, nint, void>)&Status,
                    (nint)(delegate* unmanaged<nint, nint>)&UserAgent,
                    (nint)(delegate* unmanaged<uint, nint>)&MemAlloc,
                    (nint)(delegate* unmanaged<nint, void>)&MemFree,
                    (nint)(delegate* unmanaged<uint, uint>)&MemFlush,
                    (nint)(delegate* unmanaged<byte, void>)&ReloadPlugins,
                    (nint)(delegate* unmanaged<nint>)&GetJavaEnv,
                    (nint)(delegate* unmanaged<nint, nint>)&GetJavaPeer,
                    (nint)(delegate* unmanaged<nint, nint, nint, nint, short>)&GetUrlNotify,
                    (nint)(delegate* unmanaged<nint, nint, nint, uint, nint, byte, nint, short>)&PostUrlNotify,
                    (nint)(delegate* unmanaged<nint, int, nint, short>)&GetValue,
                    (nint)(delegate* unmanaged<nint, int, nint, short>)&SetValue,
                    (nint)(delegate* unmanaged<nint, nint, void>)&InvalidateRect,
                    (nint)(delegate* unmanaged<nint, nint, void>)&InvalidateRegion,
                    (nint)(delegate* unmanaged<nint, void>)&ForceRedraw,
                    (nint)(delegate* unmanaged<nint, nint>)&GetStringIdentifier,
                    (nint)(delegate* unmanaged<nint, int, nint*, void>)&GetStringIdentifiers,
                    (nint)(delegate* unmanaged<int, nint>)&GetIntIdentifier,
                    (nint)(delegate* unmanaged<nint, byte>)&IdentifierIsString,
                    (nint)(delegate* unmanaged<nint, nint>)&Utf8FromIdentifier,
                    (nint)(delegate* unmanaged<nint, int>)&IntFromIdentifier,
                    (nint)(delegate* unmanaged<nint, NPClass*, nint>)&CreateObject,
                    (nint)(delegate* unmanaged<nint, nint>)&RetainObject,
                    (nint)(delegate* unmanaged<nint, void>)&ReleaseObject,
                    (nint)(delegate* unmanaged<nint, nint, nint, NPVariant*, uint, NPVariant*, byte>)&Invoke,
                    (nint)(delegate* unmanaged<nint, nint, NPVariant*, uint, NPVariant*, byte>)&InvokeDefault,
                    (nint)(delegate* unmanaged<nint, nint, NPString*, NPVariant*, byte>)&Evaluate,
                    (nint)(delegate* unmanaged<nint, nint, nint, NPVariant*, byte>)&GetProperty,
                    (nint)(delegate* unmanaged<nint, nint, nint, NPVariant*, byte>)&SetProperty,
                    (nint)(delegate* unmanaged<nint, nint, nint, byte>)&RemoveProperty,
                    (nint)(delegate* unmanaged<nint, nint, nint, byte>)&HasProperty,
                    (nint)(delegate* unmanaged<nint, nint, nint, byte>)&HasMethod,
                    (nint)(delegate* unmanaged<NPVariant*, void>)&ReleaseVariantValue,
                    (nint)(delegate* unmanaged<nint, nint, void>)&SetException,
                    (nint)(delegate* unmanaged<nint, byte, void>)&PushPopupsEnabledState,
                    (nint)(delegate* unmanaged<nint, void>)&PopPopupsEnabledState,
                    (nint)(delegate* unmanaged<nint, nint, nint*, uint*, byte>)&Enumerate,
                    (nint)(delegate* unmanaged<nint, nint, nint, void>)&PluginThreadAsyncCall,
                    (nint)(delegate* unmanaged<nint, nint, NPVariant*, uint, NPVariant*, byte>)&Construct,
                    (nint)(delegate* unmanaged<nint, int, nint, nint*, uint*, short>)&GetValueForUrl,
                    (nint)(delegate* unmanaged<nint, int, nint, nint, uint, short>)&SetValueForUrl,
                    (nint)(delegate* unmanaged<nint, nint, nint, int, nint, nint, nint*, uint*, nint*, uint*, short>)&GetAuthenticationInfo,
                    (nint)(delegate* unmanaged<nint, uint, byte, nint, uint>)&ScheduleTimer,
                    (nint)(delegate* unmanaged<nint, uint, void>)&UnscheduleTimer,
                    (nint)(delegate* unmanaged<nint, nint, short>)&PopUpContextMenu,
                    (nint)(delegate* unmanaged<nint, double, double, int, double*, double*, int, byte>)&ConvertPoint,
                    (nint)(delegate* unmanaged<nint, nint, byte, byte>)&HandleEvent,
                    (nint)(delegate* unmanaged<nint, int, byte>)&UnfocusInstance,
                    (nint)(delegate* unmanaged<nint, nint, byte, void>)&UrlRedirectResponse,
                    (nint)(delegate* unmanaged<nint, nint, int, nint, nint, short>)&InitAsyncSurface,
                    (nint)(delegate* unmanaged<nint, nint, short>)&FinalizeAsyncSurface,
                    (nint)(delegate* unmanaged<nint, nint, nint, void>)&SetCurrentAsyncSurface
                };
                if (functions.Length != PointerCount)
                    throw new InvalidOperationException("Table NPNetscapeFuncs incomplète.");

                int size = 8 + PointerCount * sizeof(nint);
                nint table = NpMemory.AllocZeroed((nuint)size);
                *(ushort*)table = (ushort)size;
                *(ushort*)(table + 2) = (ushort)((Np.VersionMajor << 8) | Np.VersionMinor);
                for (int i = 0; i < functions.Length; i++)
                    *(nint*)(table + 8 + i * sizeof(nint)) = functions[i];
                _table = table;
                return table;
            }
        }

        static short Guard(string name, Func<short> action)
        {
            try
            {
                return action();
            }
            catch (Exception ex)
            {
                HostChannel.Error($"{name} : {ex.GetType().Name} : {ex.Message}");
                return Np.GenericError;
            }
        }

        static byte GuardBool(string name, Func<bool> action)
        {
            try
            {
                return action() ? (byte)1 : (byte)0;
            }
            catch (Exception ex)
            {
                HostChannel.Error($"{name} : {ex.GetType().Name} : {ex.Message}");
                return 0;
            }
        }

        static PluginInstance? Instance(nint npp) => PluginInstance.FromNpp(npp);

        // ---------------------------------------------------------------
        // Réseau
        // ---------------------------------------------------------------

        [UnmanagedCallersOnly]
        static short GetUrl(nint npp, nint url, nint target)
            => Guard("NPN_GetURL", () => Instance(npp)?.RequestUrl(NpMemory.ReadUtf8(url), NpMemory.ReadUtf8(target), null, notify: false, 0) ?? Np.InvalidInstanceError);

        [UnmanagedCallersOnly]
        static short GetUrlNotify(nint npp, nint url, nint target, nint notifyData)
            => Guard("NPN_GetURLNotify", () => Instance(npp)?.RequestUrl(NpMemory.ReadUtf8(url), NpMemory.ReadUtf8(target), null, notify: true, notifyData) ?? Np.InvalidInstanceError);

        [UnmanagedCallersOnly]
        static short PostUrl(nint npp, nint url, nint target, uint length, nint buffer, byte file)
            => Guard("NPN_PostURL", () => Instance(npp)?.RequestUrl(NpMemory.ReadUtf8(url), NpMemory.ReadUtf8(target), PostData.Read(buffer, length, file != 0), notify: false, 0) ?? Np.InvalidInstanceError);

        [UnmanagedCallersOnly]
        static short PostUrlNotify(nint npp, nint url, nint target, uint length, nint buffer, byte file, nint notifyData)
            => Guard("NPN_PostURLNotify", () => Instance(npp)?.RequestUrl(NpMemory.ReadUtf8(url), NpMemory.ReadUtf8(target), PostData.Read(buffer, length, file != 0), notify: true, notifyData) ?? Np.InvalidInstanceError);

        [UnmanagedCallersOnly]
        static short RequestRead(nint stream, nint ranges) => Np.StreamNotSeekable;

        /// <summary>Flux écrits par le module vers le navigateur : non pris en charge.</summary>
        [UnmanagedCallersOnly]
        static short NewStream(nint npp, nint type, nint target, nint stream) => Np.GenericError;

        [UnmanagedCallersOnly]
        static int Write(nint npp, nint stream, int length, nint buffer) => -1;

        [UnmanagedCallersOnly]
        static short DestroyStream(nint npp, nint stream, short reason)
            => Guard("NPN_DestroyStream", () => Instance(npp)?.CancelStream(stream, reason) ?? Np.InvalidInstanceError);

        [UnmanagedCallersOnly]
        static void Status(nint npp, nint message)
        {
            try
            {
                HostChannel.Send("status", ("text", NpMemory.ReadUtf8(message) ?? string.Empty));
            }
            catch (Exception ex)
            {
                HostChannel.Error("NPN_Status : " + ex.Message);
            }
        }

        [UnmanagedCallersOnly]
        static nint UserAgent(nint npp) => PluginInstance.UserAgentPointer;

        // ---------------------------------------------------------------
        // Mémoire
        // ---------------------------------------------------------------

        [UnmanagedCallersOnly]
        static nint MemAlloc(uint size) => NpMemory.Alloc(size);

        [UnmanagedCallersOnly]
        static void MemFree(nint pointer) => NpMemory.Free(pointer);

        [UnmanagedCallersOnly]
        static uint MemFlush(uint size) => 0;

        [UnmanagedCallersOnly]
        static void ReloadPlugins(byte reloadPages)
        {
        }

        [UnmanagedCallersOnly]
        static nint GetJavaEnv() => 0;

        [UnmanagedCallersOnly]
        static nint GetJavaPeer(nint npp) => 0;

        // ---------------------------------------------------------------
        // Valeurs
        // ---------------------------------------------------------------

        [UnmanagedCallersOnly]
        static short GetValue(nint npp, int variable, nint value)
            => Guard("NPN_GetValue", () => PluginInstance.GetBrowserValue(Instance(npp), (NPNVariable)variable, value));

        [UnmanagedCallersOnly]
        static short SetValue(nint npp, int variable, nint value)
            => Guard("NPN_SetValue", () => Instance(npp)?.SetPluginValue((NPPVariable)variable, value) ?? Np.InvalidInstanceError);

        // Dessin sans fenêtre : non utilisé (le module dessine dans sa fenêtre).
        [UnmanagedCallersOnly]
        static void InvalidateRect(nint npp, nint rect)
        {
        }

        [UnmanagedCallersOnly]
        static void InvalidateRegion(nint npp, nint region)
        {
        }

        [UnmanagedCallersOnly]
        static void ForceRedraw(nint npp)
        {
        }

        // ---------------------------------------------------------------
        // Identifiants
        // ---------------------------------------------------------------

        [UnmanagedCallersOnly]
        static nint GetStringIdentifier(nint name)
            => name == 0 ? 0 : NpIdentifiers.FromString(NpMemory.ReadUtf8(name)!);

        [UnmanagedCallersOnly]
        static void GetStringIdentifiers(nint names, int count, nint* identifiers)
        {
            for (int i = 0; i < count; i++)
            {
                nint name = ((nint*)names)[i];
                identifiers[i] = name == 0 ? 0 : NpIdentifiers.FromString(NpMemory.ReadUtf8(name)!);
            }
        }

        [UnmanagedCallersOnly]
        static nint GetIntIdentifier(int value) => NpIdentifiers.FromInt(value);

        [UnmanagedCallersOnly]
        static byte IdentifierIsString(nint identifier) => NpIdentifiers.IsString(identifier) ? (byte)1 : (byte)0;

        [UnmanagedCallersOnly]
        static nint Utf8FromIdentifier(nint identifier)
            => NpIdentifiers.ToName(identifier) is { } name ? NpMemory.Utf8(name) : 0;

        [UnmanagedCallersOnly]
        static int IntFromIdentifier(nint identifier)
            => NpIdentifiers.IsString(identifier) ? int.MinValue : NpIdentifiers.ToInt(identifier);

        // ---------------------------------------------------------------
        // Objets scriptables
        // ---------------------------------------------------------------

        [UnmanagedCallersOnly]
        static nint CreateObject(nint npp, NPClass* type) => NpObjects.Create(npp, type);

        [UnmanagedCallersOnly]
        static nint RetainObject(nint obj) => NpObjects.Retain(obj);

        [UnmanagedCallersOnly]
        static void ReleaseObject(nint obj) => NpObjects.Release(obj);

        [UnmanagedCallersOnly]
        static byte Invoke(nint npp, nint obj, nint name, NPVariant* args, uint count, NPVariant* result)
        {
            if (result != null)
                *result = default;
            return NpObjects.Invoke(obj, name, args, count, result) ? (byte)1 : (byte)0;
        }

        [UnmanagedCallersOnly]
        static byte InvokeDefault(nint npp, nint obj, NPVariant* args, uint count, NPVariant* result)
        {
            if (result != null)
                *result = default;
            return NpObjects.InvokeDefault(obj, args, count, result) ? (byte)1 : (byte)0;
        }

        [UnmanagedCallersOnly]
        static byte Evaluate(nint npp, nint obj, NPString* script, NPVariant* result)
        {
            if (result != null)
                *result = default;
            string code = script == null ? string.Empty : NpMemory.ReadUtf8(script->UTF8Characters, script->UTF8Length);
            object? value = null;
            byte ok = GuardBool("NPN_Evaluate", () => Instance(npp)?.Evaluate(code, out value) == true);
            if (ok != 0 && result != null)
                NpVariants.Write(result, value);
            return ok;
        }

        [UnmanagedCallersOnly]
        static byte GetProperty(nint npp, nint obj, nint name, NPVariant* result)
        {
            if (result != null)
                *result = default;
            return NpObjects.GetProperty(obj, name, result) ? (byte)1 : (byte)0;
        }

        [UnmanagedCallersOnly]
        static byte SetProperty(nint npp, nint obj, nint name, NPVariant* value)
            => NpObjects.SetProperty(obj, name, value) ? (byte)1 : (byte)0;

        [UnmanagedCallersOnly]
        static byte RemoveProperty(nint npp, nint obj, nint name) => NpObjects.RemoveProperty(obj, name) ? (byte)1 : (byte)0;

        [UnmanagedCallersOnly]
        static byte HasProperty(nint npp, nint obj, nint name) => NpObjects.HasProperty(obj, name) ? (byte)1 : (byte)0;

        [UnmanagedCallersOnly]
        static byte HasMethod(nint npp, nint obj, nint name) => NpObjects.HasMethod(obj, name) ? (byte)1 : (byte)0;

        [UnmanagedCallersOnly]
        static void ReleaseVariantValue(NPVariant* variant) => NpVariants.Release(variant);

        [UnmanagedCallersOnly]
        static void SetException(nint obj, nint message)
            => HostChannel.Log("Exception signalée par le module : " + NpMemory.ReadUtf8(message));

        [UnmanagedCallersOnly]
        static void PushPopupsEnabledState(nint npp, byte enabled) => Instance(npp)?.PushPopups(enabled != 0);

        [UnmanagedCallersOnly]
        static void PopPopupsEnabledState(nint npp) => Instance(npp)?.PopPopups();

        [UnmanagedCallersOnly]
        static byte Enumerate(nint npp, nint obj, nint* identifiers, uint* count)
            => NpObjects.Enumerate(obj, identifiers, count) ? (byte)1 : (byte)0;

        /// <summary>Seule fonction appelable depuis un autre fil que celui du module.</summary>
        [UnmanagedCallersOnly]
        static void PluginThreadAsyncCall(nint npp, nint function, nint userData)
        {
            if (function == 0)
                return;
            UiThread.Post(() =>
            {
                if (PluginInstance.FromNpp(npp) is { IsAlive: true })
                    ((delegate* unmanaged<nint, void>)function)(userData);
            });
        }

        [UnmanagedCallersOnly]
        static byte Construct(nint npp, nint obj, NPVariant* args, uint count, NPVariant* result)
        {
            if (result != null)
                *result = default;
            return NpObjects.Construct(obj, args, count, result) ? (byte)1 : (byte)0;
        }

        [UnmanagedCallersOnly]
        static short GetValueForUrl(nint npp, int variable, nint url, nint* value, uint* length)
            => Guard("NPN_GetValueForURL", () => Instance(npp)?.GetValueForUrl((NPNURLVariable)variable, NpMemory.ReadUtf8(url), value, length) ?? Np.InvalidInstanceError);

        [UnmanagedCallersOnly]
        static short SetValueForUrl(nint npp, int variable, nint url, nint value, uint length) => Np.GenericError;

        [UnmanagedCallersOnly]
        static short GetAuthenticationInfo(nint npp, nint protocol, nint host, int port, nint scheme, nint realm,
            nint* username, uint* userLength, nint* password, uint* passwordLength) => Np.GenericError;

        [UnmanagedCallersOnly]
        static uint ScheduleTimer(nint npp, uint interval, byte repeat, nint function)
        {
            try
            {
                return Instance(npp)?.ScheduleTimer(interval, repeat != 0, function) ?? 0;
            }
            catch (Exception ex)
            {
                HostChannel.Error("NPN_ScheduleTimer : " + ex.Message);
                return 0;
            }
        }

        [UnmanagedCallersOnly]
        static void UnscheduleTimer(nint npp, uint id) => Instance(npp)?.UnscheduleTimer(id);

        [UnmanagedCallersOnly]
        static short PopUpContextMenu(nint npp, nint menu) => Np.GenericError;

        [UnmanagedCallersOnly]
        static byte ConvertPoint(nint npp, double x, double y, int from, double* toX, double* toY, int to) => 0;

        [UnmanagedCallersOnly]
        static byte HandleEvent(nint npp, nint nativeEvent, byte handled) => 0;

        [UnmanagedCallersOnly]
        static byte UnfocusInstance(nint npp, int direction) => 0;

        [UnmanagedCallersOnly]
        static void UrlRedirectResponse(nint npp, nint notifyData, byte allow)
        {
        }

        [UnmanagedCallersOnly]
        static short InitAsyncSurface(nint npp, nint size, int format, nint initData, nint surface) => Np.GenericError;

        [UnmanagedCallersOnly]
        static short FinalizeAsyncSurface(nint npp, nint surface) => Np.GenericError;

        [UnmanagedCallersOnly]
        static void SetCurrentAsyncSurface(nint npp, nint surface, nint changed)
        {
        }
    }
}
