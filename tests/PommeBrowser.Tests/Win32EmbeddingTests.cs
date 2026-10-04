using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using PommeBrowser.Legacy;
using Xunit.Abstractions;

namespace PommeBrowser.Tests;

/// <summary>
/// Fenêtre d'un autre fil logée par SetParent (lecteur Flash, Basilisk) : elle ne reçoit le
/// clavier qu'une fois sa saisie reliée à celle de PommeBrowser.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class Win32EmbeddingTests(ITestOutputHelper output)
{
    [WindowsFact]
    public void A_window_of_another_thread_docked_by_SetParent_gets_the_keyboard_once_its_input_is_linked()
    {
        nint top = CreateWindowExW(0, "STATIC", "PommeBrowser (test)", WsOverlappedWindow | WsVisible, 0, 0, 400, 300, 0, 0, 0, 0);
        nint host = CreateWindowExW(0, "STATIC", null, WsChild | WsVisible, 0, 0, 300, 200, top, 0, 0, 0);
        Assert.NotEqual(0, top);
        Assert.NotEqual(0, host);
        using var other = new OtherThreadWindow();
        try
        {
            Win32Embedding.MakeChild(other.Window, host);

            SetFocus(other.Window);
            bool linkedByWindows = GetFocus() == other.Window;
            output.WriteLine(linkedByWindows
                ? "Après SetParent, Windows a déjà relié la saisie des deux fils."
                : "Après SetParent, la saisie des deux fils n'est pas reliée : le clavier n'irait pas à la fenêtre logée.");

            uint linked = Win32Embedding.LinkInput(other.Window, out int error);
            Assert.True(linked != 0 || linkedByWindows, "AttachThreadInput refusé (erreur " + error + ").");
            SetFocus(other.Window);
            Assert.Equal(other.Window, GetFocus());

            Win32Embedding.UnlinkInput(linked);
            Win32Embedding.MakeTopLevel(other.Window);
        }
        finally
        {
            other.Dispose();
            DestroyWindow(top);
        }
    }

    /// <summary>Fenêtre à part créée par un autre fil, qui traite ses messages (comme le lecteur Flash).</summary>
    sealed class OtherThreadWindow : IDisposable
    {
        readonly Thread _thread;
        readonly ManualResetEventSlim _ready = new();
        uint _threadId;
        bool _disposed;

        public OtherThreadWindow()
        {
            _thread = new Thread(Run) { IsBackground = true };
            _thread.Start();
            Assert.True(_ready.Wait(TimeSpan.FromSeconds(10)), "Fenêtre de l'autre fil non créée.");
            Assert.NotEqual(0, Window);
        }

        public nint Window { get; private set; }

        void Run()
        {
            _threadId = GetCurrentThreadId();
            Window = CreateWindowExW(0, "STATIC", "Lecteur (test)", WsOverlappedWindow, 0, 0, 200, 150, 0, 0, 0, 0);
            _ready.Set();
            while (GetMessageW(out Msg message, 0, 0, 0) > 0)
            {
                TranslateMessage(message);
                DispatchMessageW(message);
            }
            DestroyWindow(Window);
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            PostThreadMessageW(_threadId, WmQuit, 0, 0);
            _thread.Join(TimeSpan.FromSeconds(10));
            _ready.Dispose();
        }
    }

    const int WsOverlappedWindow = 0x00CF0000;
    const int WsChild = 0x40000000;
    const int WsVisible = 0x10000000;
    const uint WmQuit = 0x0012;

    [StructLayout(LayoutKind.Sequential)]
    struct Msg
    {
        public nint Window;
        public uint Message;
        public nint WParam;
        public nint LParam;
        public uint Time;
        public int X;
        public int Y;
        public uint Private;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern nint CreateWindowExW(int exStyle, string className, string? windowName, int style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll")] static extern nint SetFocus(nint window);
    [DllImport("user32.dll")] static extern nint GetFocus();
    [DllImport("user32.dll")] static extern int GetMessageW(out Msg message, nint window, uint min, uint max);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool TranslateMessage(in Msg message);
    [DllImport("user32.dll")] static extern nint DispatchMessageW(in Msg message);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool PostThreadMessageW(uint thread, uint message, nint wParam, nint lParam);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
}
