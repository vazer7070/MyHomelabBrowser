using System.Runtime.InteropServices;

namespace PommeFlash.Host.Native
{
    /// <summary>Fonctions Win32 utilisées par l'hôte (fenêtres, messages, minuteries, bibliothèques).</summary>
    static unsafe partial class Win32
    {
        const string User32 = "user32.dll";
        const string Kernel32 = "kernel32.dll";

        public const uint WM_DESTROY = 0x0002;
        public const uint WM_SIZE = 0x0005;
        public const uint WM_SETFOCUS = 0x0007;
        public const uint WM_CLOSE = 0x0010;
        public const uint WM_ERASEBKGND = 0x0014;
        public const uint WM_TIMER = 0x0113;
        public const uint WM_APP = 0x8000;

        public const uint WS_OVERLAPPEDWINDOW = 0x00CF0000;
        public const uint WS_CHILD = 0x40000000;
        public const uint WS_VISIBLE = 0x10000000;
        public const uint WS_CLIPCHILDREN = 0x02000000;
        public const uint WS_CLIPSIBLINGS = 0x04000000;

        public const int SW_SHOW = 5;
        public const int CW_USEDEFAULT = unchecked((int)0x80000000);
        public const uint LOAD_WITH_ALTERED_SEARCH_PATH = 0x00000008;

        [StructLayout(LayoutKind.Sequential)]
        public struct WNDCLASSEXW
        {
            public uint cbSize;
            public uint style;
            public nint lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public nint hInstance;
            public nint hIcon;
            public nint hCursor;
            public nint hbrBackground;
            public nint lpszMenuName;
            public nint lpszClassName;
            public nint hIconSm;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MSG
        {
            public nint hwnd;
            public uint message;
            public nuint wParam;
            public nint lParam;
            public uint time;
            public int ptX;
            public int ptY;
            public uint lPrivate;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int left;
            public int top;
            public int right;
            public int bottom;
        }

        [LibraryImport(User32, SetLastError = true)]
        public static partial ushort RegisterClassExW(WNDCLASSEXW* wc);

        [LibraryImport(User32, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        public static partial nint CreateWindowExW(uint exStyle, string className, string? windowName, uint style,
            int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

        [LibraryImport(User32)]
        public static partial nint DefWindowProcW(nint hwnd, uint msg, nuint wParam, nint lParam);

        [LibraryImport(User32)]
        public static partial int GetMessageW(MSG* msg, nint hwnd, uint min, uint max);

        [LibraryImport(User32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool TranslateMessage(MSG* msg);

        [LibraryImport(User32)]
        public static partial nint DispatchMessageW(MSG* msg);

        [LibraryImport(User32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool PostMessageW(nint hwnd, uint msg, nuint wParam, nint lParam);

        [LibraryImport(User32)]
        public static partial void PostQuitMessage(int exitCode);

        [LibraryImport(User32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool DestroyWindow(nint hwnd);

        [LibraryImport(User32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool MoveWindow(nint hwnd, int x, int y, int width, int height, [MarshalAs(UnmanagedType.Bool)] bool repaint);

        [LibraryImport(User32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool GetClientRect(nint hwnd, RECT* rect);

        [LibraryImport(User32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool ShowWindow(nint hwnd, int command);

        [LibraryImport(User32)]
        public static partial nuint SetTimer(nint hwnd, nuint id, uint elapse, nint callback);

        [LibraryImport(User32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool KillTimer(nint hwnd, nuint id);

        [LibraryImport(User32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool AdjustWindowRectEx(RECT* rect, uint style, [MarshalAs(UnmanagedType.Bool)] bool menu, uint exStyle);

        [LibraryImport(User32)]
        public static partial nint SetFocus(nint hwnd);

        [LibraryImport(User32)]
        public static partial nint GetWindow(nint hwnd, uint command);

        [LibraryImport(User32)]
        public static partial nint LoadCursorW(nint instance, nint name);

        [LibraryImport("gdi32.dll")]
        public static partial nint GetStockObject(int kind);

        [LibraryImport(Kernel32, StringMarshalling = StringMarshalling.Utf16)]
        public static partial nint GetModuleHandleW(string? name);

        [LibraryImport(Kernel32, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        public static partial nint LoadLibraryExW(string path, nint file, uint flags);

        [LibraryImport(Kernel32, StringMarshalling = StringMarshalling.Utf8)]
        public static partial nint GetProcAddress(nint module, string name);

        [LibraryImport(Kernel32)]
        public static partial uint GetCurrentThreadId();

        public const uint GW_CHILD = 5;
        public const nint IDC_ARROW = 32512;
    }
}
