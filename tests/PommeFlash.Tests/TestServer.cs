using System.Net;
using System.Net.Sockets;

namespace PommeFlash.Tests;

/// <summary>Petit serveur HTTP local : contenu Flash, fichier texte, écho des envois, 404 sinon.</summary>
sealed class TestServer : IDisposable
{
    readonly HttpListener _listener = new();
    readonly Dictionary<string, (byte[] Body, string Type)> _files = new(StringComparer.Ordinal);

    public TestServer()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        Port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _listener.Start();
        _ = Task.Run(ServeAsync);
    }

    public int Port { get; }

    public string Url(string path) => $"http://127.0.0.1:{Port}/{path}";

    public void Add(string path, byte[] body, string type) => _files[path] = (body, type);

    async Task ServeAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
            {
                return;
            }

            string path = context.Request.Url!.AbsolutePath.TrimStart('/');
            HttpListenerResponse response = context.Response;
            if (context.Request.HttpMethod == "POST" && path.EndsWith("echo", StringComparison.Ordinal))
            {
                using var body = new MemoryStream();
                await context.Request.InputStream.CopyToAsync(body);
                response.ContentType = context.Request.ContentType ?? "application/octet-stream";
                await Write(response, body.ToArray());
            }
            else if (_files.TryGetValue(path, out (byte[] Body, string Type) file))
            {
                response.ContentType = file.Type;
                await Write(response, file.Body);
            }
            else
            {
                response.StatusCode = 404;
                response.Close();
            }
        }
    }

    static async Task Write(HttpListenerResponse response, byte[] body)
    {
        response.ContentLength64 = body.Length;
        await response.OutputStream.WriteAsync(body);
        response.Close();
    }

    public void Dispose() => _listener.Close();
}
