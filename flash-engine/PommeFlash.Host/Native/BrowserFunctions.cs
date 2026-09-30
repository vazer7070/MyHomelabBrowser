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
                    (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, short>)&GetUrl,
                    (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, uint, nint, byte, short>)&PostUrl,
                    (nint)(delegate* unmanaged[Cdecl]<nint, nint, short>)&RequestRead,
                    (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, short>)&NewStream,
                    (nint)(delegate* unmanaged[Cdecl]<nint, nint, int, nint, int>)&Write,
                    (nint)(delegate* unmanaged[Cdecl]<nint, nint, short, short>)&DestroyStream,
                    (nint)(delegate* unmanaged[Cdecl]<nint, nint, void>)&Status,
                    (nint)(delegate* unmanaged[Cdecl]<nint, nint>)&UserAgent,
                    (nint)(delegate* unmanaged[Cdecl]<uint, nint>)&MemAlloc,
                    (nint)(delegate* unmanaged[Cdecl]<nint, void>)&MemFree,
                    (nint)(delegate* unmanaged[Cdecl]<uint, uint>)&MemFlush,
                    (nint)(delegate* unmanaged[Cdecl]<byte, void>)&ReloadPlugins,
                    (nint)(delegate* unmanaged[Cdecl]<nint>)&GetJavaEnv,
                    (nint)(delegate* unmanaged[Cdecl]<nint, nint>)&GetJavaPeer,
                    (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, short>)&GetUrlNotify,
                    (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, uint, nint, byte, nint, short>)&PostUrlNotify,
                    (nint)(delegate* unmanaged[Cdecl]<nint, int, nint, short>)&GetValue,
                    (nint)(delegate* unmanaged[Cdecl]<nint, int, nint, short>)&SetValue,
                    (nint)(delegate* unmanaged[Cdecl]<nint, nint, void>)&InvalidateRect,
                    (nint)(delegate* unmanaged[Cdecl]<nint, nint, void>)&InvalidateRegion,
                    (nint)(delegate* unmanaged[Cdecl]<nint, void>)&ForceRedraw,
                    (nint)(delegate* unmanaged[Cdecl]<nint, nint>)&GetStringIdentifier,
                    (nint)(delegate* unmanaged[Cdecl]<nint, int, nint*, void>)&GetStringIdentifiers,
                    (nint)(delegate* unmanaged[Cdecl]<int, nint>)&GetIntIdentifier,
                    (nint)(delegate* unmanaged[Cdecl]<nint, byte>)&IdentifierIsString,
                    (nint)(delegate* unmanaged[Cdecl]<nint, nint>)&Utf8FromIdentifier,
                    (nint)(delegate* unmanaged[Cdecl]<nint, int>)&IntFromIdentifier,
                    (nint)(delegate* unmanaged[Cdecl]<nint, NPClass*, nint>)&CreateObject,
                    (nint)(delegate* unmanaged[Cdecl]<nint, nint>)&RetainObject,
                    (nint)(delegate* unmanaged[Cdecl]<nint, void>)&ReleaseObject,
                    (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, NPVariant*, uint, NPVariant*, byte>)&Invoke,
                    (nint)(delegate* unmanaged[Cdecl]<nint, nint, NPVariant*, uint, NPVariant*, byte>)&InvokeDefault,
                    (nint)(delegate* unmanaged[Cdecl]<nint, nint, NPString*, NPVariant*, byte>)&Evaluate,
                    (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, NPVariant*, byte>)&GetProperty,
                    (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, NPVariant*, byte>)&SetProperty,
                    (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, byte>)&RemoveProperty,
                    (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, byte>)&HasProperty,
                    (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, byte>)&HasMethod,
                    (nint)(delegate* unmanaged[Cdecl]<NPVariant*, void>)&ReleaseVariantValue,
                    (nint)(delegate* unmanaged[Cdecl]<nint, nint, void>)&SetException,
                    (nint)(delegate* unmanaged[Cdecl]<nint, byte, void>)&PushPopupsEnabledState,
                    (nint)(delegate* unmanaged[Cdecl]<nint, void>)&PopPopupsEnabledState,
                    (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint*, uint*, byte>)&Enumerate,
                    (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&PluginThreadAsyncCall,
                    (nint)(delegate* unmanaged[Cdecl]<nint, nint, NPVariant*, uint, NPVariant*, byte>)&Construct,
                    (nint)(delegate* unmanaged[Cdecl]<nint, int, nint, nint*, uint*, short>)&GetValueForUrl,
                    (nint)(delegate* unmanaged[Cdecl]<nint, int, nint, nint, uint, short>)&SetValueForUrl,
                    (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, int, nint, nint, nint*, uint*, nint*, uint*, short>)&GetAuthenticationInfo,
                    (nint)(delegate* unmanaged[Cdecl]<nint, uint, byte, nint, uint>)&ScheduleTimer,
                    (nint)(delegate* unmanaged[Cdecl]<nint, uint, void>)&UnscheduleTimer,
                    (nint)(delegate* unmanaged[Cdecl]<nint, nint, short>)&PopUpContextMenu,
                    (nint)(delegate* unmanaged[Cdecl]<nint, double, double, int, double*, double*, int, byte>)&ConvertPoint,
                    (nint)(delegate* unmanaged[Cdecl]<nint, nint, byte, byte>)&HandleEvent,
                    (nint)(delegate* unmanaged[Cdecl]<nint, int, byte>)&UnfocusInstance,
                    (nint)(delegate* unmanaged[Cdecl]<nint, nint, byte, void>)&UrlRedirectResponse,
                    (nint)(delegate* unmanaged[Cdecl]<nint, nint, int, nint, nint, short>)&InitAsyncSurface,
                    (nint)(delegate* unmanaged[Cdecl]<nint, nint, short>)&FinalizeAsyncSurface,
                    (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&SetCurrentAsyncSurface
                };
                if (functions.Length != PointerCount)
                    throw new InvalidOperationException("Table NPNetscapeFuncs incomplète.");

                // En-tête (taille, version) puis les pointeurs, alignés : 472 octets en 64 bits, 236 en 32 bits.
                int header = sizeof(nint);
                int size = header + PointerCount * sizeof(nint);
                nint table = NpMemory.AllocZeroed((nuint)size);
                *(ushort*)table = (ushort)size;
                *(ushort*)(table + 2) = (ushort)((Np.VersionMajor << 8) | Np.VersionMinor);
                for (int i = 0; i < functions.Length; i++)
                    *(nint*)(table + header + i * sizeof(nint)) = functions[i];
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

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static short GetUrl(nint npp, nint url, nint target)
            => Guard("NPN_GetURL", () => Instance(npp)?.RequestUrl(NpMemory.ReadUtf8(url), NpMemory.ReadUtf8(target), null, notify: false, 0) ?? Np.InvalidInstanceError);

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static short GetUrlNotify(nint npp, nint url, nint target, nint notifyData)
            => Guard("NPN_GetURLNotify", () => Instance(npp)?.RequestUrl(NpMemory.ReadUtf8(url), NpMemory.ReadUtf8(target), null, notify: true, notifyData) ?? Np.InvalidInstanceError);

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static short PostUrl(nint npp, nint url, nint target, uint length, nint buffer, byte file)
            => Guard("NPN_PostURL", () => Instance(npp)?.RequestUrl(NpMemory.ReadUtf8(url), NpMemory.ReadUtf8(target), PostData.Read(buffer, length, file != 0), notify: false, 0) ?? Np.InvalidInstanceError);

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static short PostUrlNotify(nint npp, nint url, nint target, uint length, nint buffer, byte file, nint notifyData)
            => Guard("NPN_PostURLNotify", () => Instance(npp)?.RequestUrl(NpMemory.ReadUtf8(url), NpMemory.ReadUtf8(target), PostData.Read(buffer, length, file != 0), notify: true, notifyData) ?? Np.InvalidInstanceError);

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static short RequestRead(nint stream, nint ranges) => Np.StreamNotSeekable;

        /// <summary>Flux écrits par le module vers le navigateur : non pris en charge.</summary>
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static short NewStream(nint npp, nint type, nint target, nint stream) => Np.GenericError;

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static int Write(nint npp, nint stream, int length, nint buffer) => -1;

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static short DestroyStream(nint npp, nint stream, short reason)
            => Guard("NPN_DestroyStream", () => Instance(npp)?.CancelStream(stream, reason) ?? Np.InvalidInstanceError);

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
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

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static nint UserAgent(nint npp) => PluginInstance.UserAgentPointer;

        // ---------------------------------------------------------------
        // Mémoire
        // ---------------------------------------------------------------

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static nint MemAlloc(uint size) => NpMemory.Alloc(size);

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static void MemFree(nint pointer) => NpMemory.Free(pointer);

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static uint MemFlush(uint size) => 0;

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static void ReloadPlugins(byte reloadPages)
        {
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static nint GetJavaEnv() => 0;

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static nint GetJavaPeer(nint npp) => 0;

        // ---------------------------------------------------------------
        // Valeurs
        // ---------------------------------------------------------------

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static short GetValue(nint npp, int variable, nint value)
            => Guard("NPN_GetValue", () => PluginInstance.GetBrowserValue(Instance(npp), (NPNVariable)variable, value));

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static short SetValue(nint npp, int variable, nint value)
            => Guard("NPN_SetValue", () => Instance(npp)?.SetPluginValue((NPPVariable)variable, value) ?? Np.InvalidInstanceError);

        // Dessin sans fenêtre : non utilisé (le module dessine dans sa fenêtre).
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static void InvalidateRect(nint npp, nint rect)
        {
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static void InvalidateRegion(nint npp, nint region)
        {
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static void ForceRedraw(nint npp)
        {
        }

        // ---------------------------------------------------------------
        // Identifiants
        // ---------------------------------------------------------------

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static nint GetStringIdentifier(nint name)
            => name == 0 ? 0 : NpIdentifiers.FromString(NpMemory.ReadUtf8(name)!);

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static void GetStringIdentifiers(nint names, int count, nint* identifiers)
        {
            for (int i = 0; i < count; i++)
            {
                nint name = ((nint*)names)[i];
                identifiers[i] = name == 0 ? 0 : NpIdentifiers.FromString(NpMemory.ReadUtf8(name)!);
            }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static nint GetIntIdentifier(int value) => NpIdentifiers.FromInt(value);

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static byte IdentifierIsString(nint identifier) => NpIdentifiers.IsString(identifier) ? (byte)1 : (byte)0;

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static nint Utf8FromIdentifier(nint identifier)
            => NpIdentifiers.ToName(identifier) is { } name ? NpMemory.Utf8(name) : 0;

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static int IntFromIdentifier(nint identifier)
            => NpIdentifiers.IsString(identifier) ? int.MinValue : NpIdentifiers.ToInt(identifier);

        // ---------------------------------------------------------------
        // Objets scriptables
        // ---------------------------------------------------------------

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static nint CreateObject(nint npp, NPClass* type) => NpObjects.Create(npp, type);

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static nint RetainObject(nint obj) => NpObjects.Retain(obj);

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static void ReleaseObject(nint obj) => NpObjects.Release(obj);

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static byte Invoke(nint npp, nint obj, nint name, NPVariant* args, uint count, NPVariant* result)
        {
            if (result != null)
                *result = default;
            return NpObjects.Invoke(obj, name, args, count, result) ? (byte)1 : (byte)0;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static byte InvokeDefault(nint npp, nint obj, NPVariant* args, uint count, NPVariant* result)
        {
            if (result != null)
                *result = default;
            return NpObjects.InvokeDefault(obj, args, count, result) ? (byte)1 : (byte)0;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
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

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static byte GetProperty(nint npp, nint obj, nint name, NPVariant* result)
        {
            if (result != null)
                *result = default;
            return NpObjects.GetProperty(obj, name, result) ? (byte)1 : (byte)0;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static byte SetProperty(nint npp, nint obj, nint name, NPVariant* value)
            => NpObjects.SetProperty(obj, name, value) ? (byte)1 : (byte)0;

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static byte RemoveProperty(nint npp, nint obj, nint name) => NpObjects.RemoveProperty(obj, name) ? (byte)1 : (byte)0;

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static byte HasProperty(nint npp, nint obj, nint name) => NpObjects.HasProperty(obj, name) ? (byte)1 : (byte)0;

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static byte HasMethod(nint npp, nint obj, nint name) => NpObjects.HasMethod(obj, name) ? (byte)1 : (byte)0;

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static void ReleaseVariantValue(NPVariant* variant) => NpVariants.Release(variant);

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static void SetException(nint obj, nint message)
            => HostChannel.Log("Exception signalée par le module : " + NpMemory.ReadUtf8(message));

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static void PushPopupsEnabledState(nint npp, byte enabled) => Instance(npp)?.PushPopups(enabled != 0);

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static void PopPopupsEnabledState(nint npp) => Instance(npp)?.PopPopups();

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static byte Enumerate(nint npp, nint obj, nint* identifiers, uint* count)
            => NpObjects.Enumerate(obj, identifiers, count) ? (byte)1 : (byte)0;

        /// <summary>Seule fonction appelable depuis un autre fil que celui du module.</summary>
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static void PluginThreadAsyncCall(nint npp, nint function, nint userData)
        {
            if (function == 0)
                return;
            UiThread.Post(() =>
            {
                if (PluginInstance.FromNpp(npp) is { IsAlive: true })
                    ((delegate* unmanaged[Cdecl]<nint, void>)function)(userData);
            });
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static byte Construct(nint npp, nint obj, NPVariant* args, uint count, NPVariant* result)
        {
            if (result != null)
                *result = default;
            return NpObjects.Construct(obj, args, count, result) ? (byte)1 : (byte)0;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static short GetValueForUrl(nint npp, int variable, nint url, nint* value, uint* length)
            => Guard("NPN_GetValueForURL", () => Instance(npp)?.GetValueForUrl((NPNURLVariable)variable, NpMemory.ReadUtf8(url), value, length) ?? Np.InvalidInstanceError);

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static short SetValueForUrl(nint npp, int variable, nint url, nint value, uint length) => Np.GenericError;

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static short GetAuthenticationInfo(nint npp, nint protocol, nint host, int port, nint scheme, nint realm,
            nint* username, uint* userLength, nint* password, uint* passwordLength) => Np.GenericError;

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
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

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static void UnscheduleTimer(nint npp, uint id) => Instance(npp)?.UnscheduleTimer(id);

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static short PopUpContextMenu(nint npp, nint menu) => Np.GenericError;

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static byte ConvertPoint(nint npp, double x, double y, int from, double* toX, double* toY, int to) => 0;

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static byte HandleEvent(nint npp, nint nativeEvent, byte handled) => 0;

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static byte UnfocusInstance(nint npp, int direction) => 0;

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static void UrlRedirectResponse(nint npp, nint notifyData, byte allow)
        {
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static short InitAsyncSurface(nint npp, nint size, int format, nint initData, nint surface) => Np.GenericError;

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static short FinalizeAsyncSurface(nint npp, nint surface) => Np.GenericError;

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static void SetCurrentAsyncSurface(nint npp, nint surface, nint changed)
        {
        }
    }
}
