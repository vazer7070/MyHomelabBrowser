using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection.PortableExecutable;
using System.Text.Json;

namespace PommeFlash.Tests;

/// <summary>Hôte lancé pour un test : événements reçus (une ligne JSON chacun) et commandes.</summary>
sealed class HostRun : IAsyncDisposable
{
    readonly Process _process;
    readonly ConcurrentQueue<JsonElement> _events = new();
    readonly Task _reader;
    readonly SemaphoreSlim _arrived = new(0);
    readonly SemaphoreSlim _input = new(1, 1);
    readonly Func<string, (bool Ok, string? Value)>? _scripts;
    readonly Func<string, bool, string?>? _cookies;

    HostRun(Process process, Func<string, (bool Ok, string? Value)>? scripts, Func<string, bool, string?>? cookies)
    {
        _process = process;
        _scripts = scripts;
        _cookies = cookies;
        _reader = Task.Run(ReadAsync);
    }

    public static string? HostPath => Environment.GetEnvironmentVariable("POMMEFLASH_HOST");
    public static string? PluginPath => Environment.GetEnvironmentVariable("POMMEFLASH_TEST_PLUGIN");
    static string? Launcher => Environment.GetEnvironmentVariable("POMMEFLASH_LAUNCHER") is { Length: > 0 } launcher ? launcher : null;

    /// <summary>Hôte Windows (PommeFlashHost.exe, lancé en place ou par Wine) ; sinon l'hôte Linux, lancé en place.</summary>
    public static bool IsWindowsHost
    {
        get
        {
            using FileStream file = File.OpenRead(HostPath!);
            return file.ReadByte() == 'M' && file.ReadByte() == 'Z';
        }
    }

    /// <summary>Hôte 32 bits (win-x86), pour un module NPSWF32 : la table NPNetscapeFuncs y fait 236 octets.</summary>
    public static bool Is32BitHost
    {
        get
        {
            if (!IsWindowsHost)
                return false;
            using var reader = new PEReader(File.OpenRead(HostPath!));
            return reader.PEHeaders.CoffHeader.Machine == Machine.I386;
        }
    }

    /// <summary>Chemin vu par l'hôte (sous Wine, le disque Z: est la racine du système).</summary>
    public static string HostVisiblePath(string path) => Launcher != null && path.StartsWith('/') ? "Z:" + path : path;

    public static void SkipIfUnavailable()
    {
        if (string.IsNullOrEmpty(HostPath) || string.IsNullOrEmpty(PluginPath))
            Assert.Skip("POMMEFLASH_HOST et POMMEFLASH_TEST_PLUGIN ne sont pas définis.");
        if (!OperatingSystem.IsWindows() && Launcher == null && IsWindowsHost)
            Assert.Skip("Hors de Windows, l'hôte Windows se lance avec Wine (POMMEFLASH_LAUNCHER=wine).");
    }

    /// <summary>
    /// Hôte lancé ; à la place de PommeBrowser, <paramref name="scripts"/> répond aux scripts de la
    /// page qu'il demande (événements « eval ») et <paramref name="cookies"/> aux cookies d'une
    /// adresse (« cookies » : adresse, HttpOnly compris ; null : refusés).
    /// </summary>
    public static HostRun Start(IEnumerable<string> arguments, Func<string, (bool Ok, string? Value)>? scripts = null,
        Func<string, bool, string?>? cookies = null)
    {
        var start = new ProcessStartInfo
        {
            FileName = Launcher ?? HostPath!,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (Launcher != null)
            start.ArgumentList.Add(HostPath!);
        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);
        start.Environment["WINEDEBUG"] = "-all";
        return new HostRun(Process.Start(start) ?? throw new InvalidOperationException("Hôte non lancé."), scripts, cookies);
    }

    async Task ReadAsync()
    {
        while (await _process.StandardOutput.ReadLineAsync() is { } line)
        {
            if (!line.StartsWith('{'))
                continue;
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement received = document.RootElement.Clone();
            _events.Enqueue(received);
            string? kind = received.GetProperty("event").GetString();
            if (_scripts != null && kind == "eval")
            {
                (bool ok, string? value) = _scripts(received.GetProperty("code").GetString()!);
                string reply = JsonSerializer.Serialize(new { ok, value });
                await SendAsync($"result {received.GetProperty("id").GetInt32()} {reply}");
            }
            else if (_cookies != null && kind == "cookies")
            {
                string? value = _cookies(received.GetProperty("url").GetString()!, received.GetProperty("http").GetBoolean());
                string reply = JsonSerializer.Serialize(new { ok = value != null, value });
                await SendAsync($"result {received.GetProperty("id").GetInt32()} {reply}");
            }
            _arrived.Release();
        }
        _arrived.Release();
    }

    public IReadOnlyList<JsonElement> Events => _events.ToList();

    /// <summary>Textes des événements « status » commençant par « TEST ».</summary>
    public IReadOnlyList<string> Reports => Events
        .Where(e => e.GetProperty("event").GetString() == "status")
        .Select(e => e.GetProperty("text").GetString()!)
        .Where(t => t.StartsWith("TEST ", StringComparison.Ordinal))
        .Select(t => t[5..])
        .ToList();

    public async Task WaitForAsync(Func<HostRun, bool> condition, TimeSpan timeout)
    {
        using var cancel = new CancellationTokenSource(timeout);
        while (!condition(this))
        {
            if (_process.HasExited && _reader.IsCompleted)
                throw new InvalidOperationException("L'hôte s'est arrêté :\n" + string.Join("\n", Events));
            try
            {
                await _arrived.WaitAsync(cancel.Token);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException("Délai dépassé. Reçu :\n" + string.Join("\n", Events));
            }
        }
    }

    public async Task SendAsync(string command)
    {
        await _input.WaitAsync();
        try
        {
            await _process.StandardInput.WriteLineAsync(command);
            await _process.StandardInput.FlushAsync();
        }
        finally
        {
            _input.Release();
        }
    }

    public async Task<int> WaitForExitAsync(TimeSpan timeout)
    {
        using var cancel = new CancellationTokenSource(timeout);
        await _process.WaitForExitAsync(cancel.Token);
        await _reader;
        return _process.ExitCode;
    }

    public async ValueTask DisposeAsync()
    {
        if (!_process.HasExited)
        {
            try
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync();
            }
            catch (InvalidOperationException)
            {
            }
        }
        _process.Dispose();
    }
}
