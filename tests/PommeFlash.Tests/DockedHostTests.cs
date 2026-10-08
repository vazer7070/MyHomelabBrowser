using System.Runtime.InteropServices;
using System.Text.Json;

namespace PommeFlash.Tests;

/// <summary>
/// Linux (X11) : la fenêtre cachée de l'hôte est logée dans la fenêtre d'un autre programme, comme
/// PommeBrowser le fait (X11Dock), puis redimensionnée par lui ; un clic de l'utilisateur dans le
/// contenu lui donne le clavier et la touche tapée arrive à la fenêtre du module. Sous Xvfb en CI.
/// </summary>
public sealed class DockedHostTests
{
    const string LibX11 = "libX11.so.6";
    const string LibXtst = "libXtst.so.6";

    [DllImport(LibX11)] static extern nint XOpenDisplay(nint name);
    [DllImport(LibX11)] static extern int XCloseDisplay(nint display);
    [DllImport(LibX11)] static extern int XDefaultScreen(nint display);
    [DllImport(LibX11)] static extern ulong XRootWindow(nint display, int screen);
    [DllImport(LibX11)] static extern ulong XCreateSimpleWindow(nint display, ulong parent, int x, int y, uint width, uint height, uint border, ulong borderColor, ulong background);
    [DllImport(LibX11)] static extern int XMapWindow(nint display, ulong window);
    [DllImport(LibX11)] static extern int XWithdrawWindow(nint display, ulong window, int screen);
    [DllImport(LibX11)] static extern int XQueryTree(nint display, ulong window, out ulong root, out ulong parent, out nint children, out uint count);
    [DllImport(LibX11)] static extern int XFree(nint data);
    [DllImport(LibX11)] static extern int XReparentWindow(nint display, ulong window, ulong parent, int x, int y);
    [DllImport(LibX11)] static extern int XMoveResizeWindow(nint display, ulong window, int x, int y, uint width, uint height);
    [DllImport(LibX11)] static extern int XDestroyWindow(nint display, ulong window);
    [DllImport(LibX11)] static extern int XSync(nint display, int discard);
    [DllImport(LibX11)] static extern byte XKeysymToKeycode(nint display, ulong keysym);
    [DllImport(LibXtst)] static extern int XTestFakeMotionEvent(nint display, int screen, int x, int y, ulong delay);
    [DllImport(LibXtst)] static extern int XTestFakeButtonEvent(nint display, uint button, int press, ulong delay);
    [DllImport(LibXtst)] static extern int XTestFakeKeyEvent(nint display, uint keycode, int press, ulong delay);

    [Fact(Timeout = 180_000)]
    public async Task The_hidden_window_docked_and_resized_by_another_program_gets_the_click_and_the_keys()
    {
        HostRun.SkipIfUnavailable();
        if (HostRun.IsWindowsHost || !OperatingSystem.IsLinux() || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")))
            Assert.Skip("Logement X11 : hôte Linux sous X11 seulement.");

        byte[] movie = new byte[20_000];
        new Random(13).NextBytes(movie);
        using var server = new TestServer();
        server.Add("movie.swf", movie, "application/x-shockwave-flash");
        server.Add("jeu/data.txt", "x"u8.ToArray(), "text/plain");

        await using HostRun host = HostRun.Start(new[]
        {
            "--plugin", HostRun.HostVisiblePath(HostRun.PluginPath!),
            "--swf", server.Url("movie.swf"),
            "--page", server.Url("jeu/page.html"),
            "--width", "400",
            "--height", "300",
            "--hidden"
        }, code => code.Contains("pommeAdd(2,3)", StringComparison.Ordinal) ? (true, "<number>5</number>") : (false, null));

        // Caché, hors de l'écran : pas de clic possible avant d'être logé.
        await host.WaitForAsync(h => h.Reports.Contains("done"), TimeSpan.FromSeconds(90));
        Assert.Contains("click-focus=skipped", host.Reports);
        JsonElement ready = host.Events.First(e => e.GetProperty("event").GetString() == "ready");
        ulong client = (ulong)ready.GetProperty("window").GetInt64();

        nint display = XOpenDisplay(0);
        Assert.NotEqual(0, display);
        ulong dock = 0;
        try
        {
            // Comme X11Dock : fenêtre d'accueil, client retiré puis logé et mis à sa taille.
            int screen = XDefaultScreen(display);
            ulong rootWindow = XRootWindow(display, screen);
            dock = XCreateSimpleWindow(display, rootWindow, 50, 50, 500, 400, 0, 0, 0x404040);
            XMapWindow(display, dock);
            XSync(display, 0);
            XWithdrawWindow(display, client, screen);
            XSync(display, 0);
            for (int i = 0; i < 100; i++)
            {
                XQueryTree(display, client, out ulong root, out ulong parent, out nint children, out _);
                if (children != 0)
                    XFree(children);
                if (parent == root)
                    break;
                await Task.Delay(10, TestContext.Current.CancellationToken);
            }
            XReparentWindow(display, client, dock, 0, 0);
            XMoveResizeWindow(display, client, 0, 0, 500, 400);
            XMapWindow(display, client);
            XSync(display, 0);

            // Taille donnée par le programme d'accueil : transmise au module.
            await host.WaitForAsync(h => h.Reports.Contains("window valid=1 width=500 height=400 type=1"), TimeSpan.FromSeconds(20));

            // Clic dans le contenu, pointeur ailleurs, puis touche « a ».
            XTestFakeMotionEvent(display, -1, 300, 250, 0);
            XTestFakeButtonEvent(display, 1, 1, 0);
            XTestFakeButtonEvent(display, 1, 0, 0);
            XSync(display, 0);
            await Task.Delay(400, TestContext.Current.CancellationToken);
            XTestFakeMotionEvent(display, -1, 900, 700, 0);
            byte key = XKeysymToKeycode(display, 0x61);
            XTestFakeKeyEvent(display, key, 1, 0);
            XTestFakeKeyEvent(display, key, 0, 0);
            XSync(display, 0);
            await host.WaitForAsync(h => h.Reports.Contains("plug-key=97"), TimeSpan.FromSeconds(20));

            // Fin normale, logé : pas d'erreur X11 notée.
            await host.SendAsync("close");
            Assert.Equal(0, await host.WaitForExitAsync(TimeSpan.FromSeconds(30)));
            Assert.DoesNotContain(host.Events, e => e.GetProperty("event").GetString() == "log" &&
                                                    e.GetProperty("message").GetString()?.Contains("Erreur X11", StringComparison.Ordinal) == true);
        }
        finally
        {
            if (dock != 0)
                XDestroyWindow(display, dock);
            XSync(display, 0);
            XCloseDisplay(display);
        }
    }
}
