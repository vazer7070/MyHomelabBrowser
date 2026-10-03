using System.Runtime.InteropServices;

namespace PommeFlash.Host.Native
{
    // Types de l'interface NPAPI (Netscape Plugin API), dispositions de Windows (64 et 32 bits) et de
    // Linux x64. Écrits d'après la spécification publique : npapi.h, npfunctions.h et npruntime.h de Mozilla.

    /// <summary>Instance de module : pdata appartient au module, ndata au navigateur.</summary>
    [StructLayout(LayoutKind.Sequential)]
    struct NPP_t
    {
        public nint pdata;
        public nint ndata;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct NPRect
    {
        public ushort top;
        public ushort left;
        public ushort bottom;
        public ushort right;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct NPWindow
    {
        public nint window;
        public int x;
        public int y;
        public uint width;
        public uint height;
        public NPRect clipRect;
        public int type;
    }

    /// <summary>NPWindow des systèmes Unix : ws_info (NPSetWindowCallbackStruct) avant le type.</summary>
    [StructLayout(LayoutKind.Sequential)]
    struct NPWindowUnix
    {
        public nint window;
        public int x;
        public int y;
        public uint width;
        public uint height;
        public NPRect clipRect;
        public nint ws_info;
        public int type;
    }

    /// <summary>Affichage X11 donné au module avec sa fenêtre (NPWindow.ws_info, Unix).</summary>
    [StructLayout(LayoutKind.Sequential)]
    struct NPSetWindowCallbackStruct
    {
        public int type;
        public nint display;
        public nint visual;
        public nuint colormap;
        public uint depth;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct NPStream
    {
        public nint pdata;
        public nint ndata;
        public nint url;
        public uint end;
        public uint lastmodified;
        public nint notifyData;
        public nint headers;
    }

    [StructLayout(LayoutKind.Sequential)]
    unsafe struct NPObject
    {
        public NPClass* _class;
        public uint referenceCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    unsafe struct NPClass
    {
        public uint structVersion;
        public nint allocate;
        public nint deallocate;
        public nint invalidate;
        public nint hasMethod;
        public nint invoke;
        public nint invokeDefault;
        public nint hasProperty;
        public nint getProperty;
        public nint setProperty;
        public nint removeProperty;
        public nint enumerate;
        public nint construct;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct NPString
    {
        public nint UTF8Characters;
        public uint UTF8Length;
    }

    enum NPVariantType
    {
        Void = 0,
        Null = 1,
        Bool = 2,
        Int32 = 3,
        Double = 4,
        String = 5,
        Object = 6
    }

    /// <summary>
    /// Valeur NPAPI : type, puis la valeur à 8 octets du début. Sa taille suit celle des pointeurs
    /// (NPString) : 24 octets en 64 bits, 16 en 32 bits ; les tableaux d'arguments en dépendent.
    /// </summary>
    [StructLayout(LayoutKind.Explicit)]
    struct NPVariant
    {
        [FieldOffset(0)] public NPVariantType type;
        [FieldOffset(8)] public byte boolValue;
        [FieldOffset(8)] public int intValue;
        [FieldOffset(8)] public double doubleValue;
        [FieldOffset(8)] public NPString stringValue;
        [FieldOffset(8)] public nint objectValue;
    }

    /// <summary>Fonctions du module (NPP_*), remplies par NP_GetEntryPoints.</summary>
    [StructLayout(LayoutKind.Sequential)]
    struct NPPluginFuncs
    {
        public ushort size;
        public ushort version;
        public nint newp;
        public nint destroy;
        public nint setwindow;
        public nint newstream;
        public nint destroystream;
        public nint asfile;
        public nint writeready;
        public nint write;
        public nint print;
        public nint @event;
        public nint urlnotify;
        public nint javaClass;
        public nint getvalue;
        public nint setvalue;
        public nint gotfocus;
        public nint lostfocus;
        public nint urlredirectnotify;
        public nint clearsitedata;
        public nint getsiteswithdata;
        public nint didComposite;
    }

    static class Np
    {
        // Version de l'interface annoncée au module (celle de Firefox 52, donc de Basilisk).
        public const ushort VersionMajor = 0;
        public const ushort VersionMinor = 29;

        public const short NoError = 0;
        public const short GenericError = 1;
        public const short InvalidInstanceError = 2;
        public const short InvalidFuncTableError = 3;
        public const short ModuleLoadFailedError = 4;
        public const short IncompatibleVersionError = 8;
        public const short InvalidParam = 9;
        public const short InvalidUrl = 10;
        public const short StreamNotSeekable = 13;

        public const short ReasonDone = 0;
        public const short ReasonNetworkError = 1;
        public const short ReasonUserBreak = 2;

        public const ushort StreamNormal = 1;
        public const ushort StreamSeek = 2;
        public const ushort StreamAsFile = 3;
        public const ushort StreamAsFileOnly = 4;

        public const ushort ModeEmbed = 1;
        public const int WindowTypeWindow = 1;

        /// <summary>NPSetWindowCallbackStruct.type (NP_SETWINDOW).</summary>
        public const int SetWindow = 1;

        /// <summary>Boîte à outils offerte au module Linux (NPNVToolkit) : GTK 2.</summary>
        public const int ToolkitGtk2 = 2;

        public const uint ClassStructVersion = 3;
    }

    /// <summary>Questions du module au navigateur (NPN_GetValue).</summary>
    enum NPNVariable
    {
        XDisplay = 1,
        XtAppContext = 2,
        NetscapeWindow = 3,
        JavascriptEnabledBool = 4,
        AsdEnabledBool = 5,
        IsOfflineBool = 6,
        ServiceManager = 10,
        DOMElement = 11,
        DOMWindow = 12,
        Toolkit = 13,
        SupportsXEmbedBool = 14,
        WindowNPObject = 15,
        PluginElementNPObject = 16,
        SupportsWindowless = 17,
        PrivateModeBool = 18,
        SupportsAdvancedKeyHandling = 21,
        DocumentOrigin = 22,
        CSSZoomFactor = 23,
        ContentsScaleFactor = 1001,
        SupportsAsyncBitmapSurfaceBool = 2007,
        SupportsAsyncWindowsDXGISurfaceBool = 2008,
        PreferredDXGIAdapter = 2009,
        MuteAudioBool = 4000
    }

    /// <summary>Valeurs du module (NPP_GetValue / NPN_SetValue).</summary>
    enum NPPVariable
    {
        PluginNameString = 1,
        PluginDescriptionString = 2,
        PluginWindowBool = 3,
        PluginTransparentBool = 4,
        PluginScriptableNPObject = 15,
        PluginWantsAllNetworkStreams = 18,
        PluginCancelSrcStream = 20,
        PluginUsesDOMForCursorBool = 22,
        PluginDrawingModel = 1000,
        PluginIsPlayingAudio = 4000
    }

    enum NPNURLVariable
    {
        Cookie = 501,
        Proxy = 502
    }
}
