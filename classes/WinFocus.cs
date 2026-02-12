using System.Runtime.InteropServices;

static class WinFocus
{
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("kernel32.dll")]
    static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll")] static extern bool SetFocus(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern IntPtr SetActiveWindow(IntPtr hWnd);

    // 🔑 Focus Win32 cross-thread (la vraie solution)
    public static void ForceFocus(IntPtr hostWindowHwnd, IntPtr targetHwnd)
    {
        if (targetHwnd == IntPtr.Zero) return;

        // thread du foreground (souvent WPF)
        uint fgTid = GetWindowThreadProcessId(GetForegroundWindow(), out _);

        // thread de la cible (Basilisk)
        uint targetTid = GetWindowThreadProcessId(targetHwnd, out _);

        // thread courant
        uint curTid = GetCurrentThreadId();

        // Attacher INPUT entre threads pour autoriser SetFocus
        try
        {
            if (fgTid != curTid) AttachThreadInput(fgTid, curTid, true);
            if (targetTid != curTid) AttachThreadInput(targetTid, curTid, true);

            // Ramener la fenêtre WPF au premier plan (sinon focus refusé)
            if (hostWindowHwnd != IntPtr.Zero)
            {
                SetForegroundWindow(hostWindowHwnd);
                SetActiveWindow(hostWindowHwnd);
            }

            // Focus sur Basilisk (enfant docké)
            SetFocus(targetHwnd);
        }
        finally
        {
            if (targetTid != curTid) AttachThreadInput(targetTid, curTid, false);
            if (fgTid != curTid) AttachThreadInput(fgTid, curTid, false);
        }
    }
}
