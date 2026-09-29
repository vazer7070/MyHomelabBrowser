using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace PommeBrowser.Engine.Apple
{
    /// <summary>
    /// Appels au runtime Objective-C (macOS) : messages, chaînes, classes créées à l'exécution
    /// (délégués de WKWebView) et blocs (fonctions de rappel des API de WebKit). Tout appel a
    /// lieu sur le fil principal, qui est celui de l'interface d'Avalonia sous macOS.
    /// </summary>
    [SupportedOSPlatform("macos")]
    static unsafe class ObjC
    {
        const string Objc = "/usr/lib/libobjc.A.dylib";
        const string System = "/usr/lib/libSystem.dylib";
        const string Foundation = "/System/Library/Frameworks/Foundation.framework/Foundation";
        const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
        const string Security = "/System/Library/Frameworks/Security.framework/Security";

        [DllImport(Objc)] public static extern nint objc_getClass(string name);
        [DllImport(Objc)] public static extern nint objc_getProtocol(string name);
        [DllImport(Objc)] static extern nint sel_registerName(string name);
        [DllImport(Objc)] public static extern nint objc_allocateClassPair(nint superclass, string name, nint extraBytes);
        [DllImport(Objc)] public static extern void objc_registerClassPair(nint cls);
        [DllImport(Objc)] [return: MarshalAs(UnmanagedType.I1)] public static extern bool class_addMethod(nint cls, nint selector, nint implementation, string types);
        [DllImport(Objc)] [return: MarshalAs(UnmanagedType.I1)] public static extern bool class_addProtocol(nint cls, nint protocol);
        [DllImport(Objc)] public static extern nint object_getClass(nint obj);

        [DllImport(Objc, EntryPoint = "objc_msgSend")] public static extern nint Send(nint receiver, nint selector);
        [DllImport(Objc, EntryPoint = "objc_msgSend")] public static extern nint Send(nint receiver, nint selector, nint a);
        [DllImport(Objc, EntryPoint = "objc_msgSend")] public static extern nint Send(nint receiver, nint selector, nint a, nint b);
        [DllImport(Objc, EntryPoint = "objc_msgSend")] public static extern nint Send(nint receiver, nint selector, nint a, nint b, nint c);
        [DllImport(Objc, EntryPoint = "objc_msgSend")] public static extern nint Send(nint receiver, nint selector, nint a, nint b, nint c, nint d);
        [DllImport(Objc, EntryPoint = "objc_msgSend")] public static extern nint SendLong(nint receiver, nint selector, long a);
        [DllImport(Objc, EntryPoint = "objc_msgSend")] public static extern nint SendLongObject(nint receiver, nint selector, long a, nint b);
        [DllImport(Objc, EntryPoint = "objc_msgSend")] public static extern nint SendObjectLongBool(nint receiver, nint selector, nint a, long b, [MarshalAs(UnmanagedType.I1)] bool c, nint d);
        [DllImport(Objc, EntryPoint = "objc_msgSend")] public static extern void SendBool(nint receiver, nint selector, [MarshalAs(UnmanagedType.I1)] bool value);
        [DllImport(Objc, EntryPoint = "objc_msgSend")] public static extern void SendDouble(nint receiver, nint selector, double value);
        [DllImport(Objc, EntryPoint = "objc_msgSend")] public static extern nint SendObjectDouble(nint receiver, nint selector, double value);
        [DllImport(Objc, EntryPoint = "objc_msgSend")] [return: MarshalAs(UnmanagedType.I1)] public static extern bool GetBool(nint receiver, nint selector);
        [DllImport(Objc, EntryPoint = "objc_msgSend")] [return: MarshalAs(UnmanagedType.I1)] public static extern bool GetBool(nint receiver, nint selector, nint a);
        [DllImport(Objc, EntryPoint = "objc_msgSend")] public static extern double GetDouble(nint receiver, nint selector);
        [DllImport(Objc, EntryPoint = "objc_msgSend")] public static extern long GetLong(nint receiver, nint selector);
        [DllImport(Objc, EntryPoint = "objc_msgSend")] public static extern nint SendRect(nint receiver, nint selector, CGRect rect, ulong style, ulong backing, [MarshalAs(UnmanagedType.I1)] bool defer);
        [DllImport(Objc, EntryPoint = "objc_msgSend")] public static extern nint SendRectObject(nint receiver, nint selector, CGRect rect, nint a);

        [DllImport(System)] static extern nint _Block_copy(nint block);
        [DllImport(System)] static extern void _Block_release(nint block);

        [DllImport(CoreFoundation)] public static extern void CFRelease(nint obj);
        [DllImport(CoreFoundation)] public static extern long CFDataGetLength(nint data);
        [DllImport(CoreFoundation)] public static extern byte* CFDataGetBytePtr(nint data);

        [DllImport(Security)] [return: MarshalAs(UnmanagedType.I1)] public static extern bool SecTrustEvaluateWithError(nint trust, out nint error);
        [DllImport(Security)] public static extern long SecTrustGetCertificateCount(nint trust);
        [DllImport(Security)] public static extern nint SecTrustGetCertificateAtIndex(nint trust, long index);
        [DllImport(Security)] public static extern nint SecCertificateCopyData(nint certificate);

        [StructLayout(LayoutKind.Sequential)]
        public struct CGRect
        {
            public double X, Y, Width, Height;
        }

        static readonly Dictionary<string, nint> Selectors = new();

        public static nint Sel(string name)
        {
            if (!Selectors.TryGetValue(name, out nint selector))
                Selectors[name] = selector = sel_registerName(name);
            return selector;
        }

        public static nint Class(string name) => objc_getClass(name);

        public static bool RespondsTo(nint obj, string selector)
            => obj != 0 && GetBool(obj, Sel("respondsToSelector:"), Sel(selector));

        public static bool IsKindOf(nint obj, string className)
            => obj != 0 && GetBool(obj, Sel("isKindOfClass:"), Class(className));

        public static nint Retain(nint obj) => obj == 0 ? 0 : Send(obj, Sel("retain"));

        public static void Release(nint obj)
        {
            if (obj != 0)
                Send(obj, Sel("release"));
        }

        // ---------------------------------------------------------------
        // Chaînes et objets de Foundation
        // ---------------------------------------------------------------

        /// <summary>NSString autolibérée (valable pendant l'appel en cours).</summary>
        public static nint String(string? value)
        {
            if (value == null)
                return 0;
            byte[] bytes = Encoding.UTF8.GetBytes(value + "\0");
            fixed (byte* pointer = bytes)
                return Send(Class("NSString"), Sel("stringWithUTF8String:"), (nint)pointer);
        }

        public static string? ToManaged(nint nsString)
        {
            if (nsString == 0)
                return null;
            nint utf8 = Send(nsString, Sel("UTF8String"));
            return utf8 == 0 ? null : Marshal.PtrToStringUTF8(utf8);
        }

        /// <summary>Texte d'un objet quelconque : la chaîne elle-même, ou sa description.</summary>
        public static string? Describe(nint obj)
        {
            if (obj == 0 || IsKindOf(obj, "NSNull"))
                return null;
            return IsKindOf(obj, "NSString") ? ToManaged(obj) : ToManaged(Send(obj, Sel("description")));
        }

        public static nint Url(string value) => Send(Class("NSURL"), Sel("URLWithString:"), String(value));

        public static nint FileUrl(string path) => Send(Class("NSURL"), Sel("fileURLWithPath:"), String(path));

        public static string? UrlString(nint nsUrl) => nsUrl == 0 ? null : ToManaged(Send(nsUrl, Sel("absoluteString")));

        public static byte[] Data(nint cfData)
        {
            if (cfData == 0)
                return Array.Empty<byte>();
            long length = CFDataGetLength(cfData);
            var bytes = new byte[length];
            Marshal.Copy((nint)CFDataGetBytePtr(cfData), bytes, 0, (int)length);
            return bytes;
        }

        /// <summary>Constante NSString exportée par un framework (WKWebsiteDataTypeDiskCache…).</summary>
        public static nint Constant(string library, string symbol)
        {
            try
            {
                nint handle = NativeLibrary.Load(library);
                return NativeLibrary.TryGetExport(handle, symbol, out nint address) ? *(nint*)address : 0;
            }
            catch (DllNotFoundException)
            {
                return 0;
            }
        }

        // ---------------------------------------------------------------
        // Classes créées à l'exécution
        // ---------------------------------------------------------------

        /// <summary>Nouvelle classe dérivée de NSObject qui adopte les protocoles donnés.</summary>
        public static nint DefineClass(string name, string[] protocols, params (string Selector, nint Implementation, string Types)[] methods)
        {
            nint existing = objc_getClass(name);
            if (existing != 0)
                return existing;

            nint cls = objc_allocateClassPair(Class("NSObject"), name, 0);
            foreach (string protocol in protocols)
            {
                nint handle = objc_getProtocol(protocol);
                if (handle != 0)
                    class_addProtocol(cls, handle);
            }
            foreach ((string selector, nint implementation, string types) in methods)
                class_addMethod(cls, Sel(selector), implementation, types);
            objc_registerClassPair(cls);
            return cls;
        }

        public static nint New(nint cls) => Send(Send(cls, Sel("alloc")), Sel("init"));

        // ---------------------------------------------------------------
        // Blocs
        // ---------------------------------------------------------------

        const int BlockIsGlobal = 1 << 28;
        const int BlockHasSignature = 1 << 30;

        static nint _globalBlockClass;

        [StructLayout(LayoutKind.Sequential)]
        struct BlockLiteral
        {
            public nint Isa;
            public int Flags;
            public int Reserved;
            public nint Invoke;
            public nint Descriptor;
            public nint Context;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct BlockDescriptor
        {
            public nuint Reserved;
            public nuint Size;
            public nint Signature;
        }

        /// <summary>
        /// Bloc à passer à une API qui rappelle une fois (fin d'une opération). Bloc « global » :
        /// WebKit peut le copier sans le déplacer ; il est libéré par <see cref="FreeBlock"/>,
        /// depuis la fonction de rappel. Le contexte (objet géré) se lit avec <see cref="BlockContext{T}"/>.
        /// </summary>
        public static nint CreateBlock(nint invoke, string signature, object context)
        {
            if (_globalBlockClass == 0)
                _globalBlockClass = NativeLibrary.GetExport(NativeLibrary.Load(System), "_NSConcreteGlobalBlock");

            var descriptor = (BlockDescriptor*)NativeMemory.AllocZeroed((nuint)sizeof(BlockDescriptor));
            descriptor->Size = (nuint)sizeof(BlockLiteral);
            descriptor->Signature = Marshal.StringToCoTaskMemUTF8(signature);

            var block = (BlockLiteral*)NativeMemory.AllocZeroed((nuint)sizeof(BlockLiteral));
            block->Isa = _globalBlockClass;
            block->Flags = BlockIsGlobal | BlockHasSignature;
            block->Invoke = invoke;
            block->Descriptor = (nint)descriptor;
            block->Context = GCHandle.ToIntPtr(GCHandle.Alloc(context));
            return (nint)block;
        }

        public static T? BlockContext<T>(nint block) where T : class
        {
            nint context = ((BlockLiteral*)block)->Context;
            return context == 0 ? null : GCHandle.FromIntPtr(context).Target as T;
        }

        public static void FreeBlock(nint block)
        {
            var literal = (BlockLiteral*)block;
            if (literal->Context != 0)
                GCHandle.FromIntPtr(literal->Context).Free();
            var descriptor = (BlockDescriptor*)literal->Descriptor;
            Marshal.FreeCoTaskMem(descriptor->Signature);
            NativeMemory.Free(descriptor);
            NativeMemory.Free(literal);
        }

        /// <summary>Garde un bloc reçu de WebKit pour l'appeler plus tard (réponse asynchrone).</summary>
        public static nint KeepBlock(nint block) => block == 0 ? 0 : _Block_copy(block);

        public static void DropBlock(nint block)
        {
            if (block != 0)
                _Block_release(block);
        }

        static nint BlockFunction(nint block) => ((BlockLiteral*)block)->Invoke;

        public static void CallBlock(nint block)
        {
            if (block != 0)
                ((delegate* unmanaged[Cdecl]<nint, void>)BlockFunction(block))(block);
        }

        public static void CallBlock(nint block, nint a)
        {
            if (block != 0)
                ((delegate* unmanaged[Cdecl]<nint, nint, void>)BlockFunction(block))(block, a);
        }

        public static void CallBlock(nint block, long a)
        {
            if (block != 0)
                ((delegate* unmanaged[Cdecl]<nint, long, void>)BlockFunction(block))(block, a);
        }

        public static void CallBlock(nint block, long a, nint b)
        {
            if (block != 0)
                ((delegate* unmanaged[Cdecl]<nint, long, nint, void>)BlockFunction(block))(block, a, b);
        }

        public static void CallBlockBool(nint block, bool value)
        {
            if (block != 0)
                ((delegate* unmanaged[Cdecl]<nint, byte, void>)BlockFunction(block))(block, value ? (byte)1 : (byte)0);
        }
    }
}
