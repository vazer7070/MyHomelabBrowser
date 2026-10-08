using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace PommeFlash.Tests;

/// <summary>
/// Petit serveur HTTP local : contenu Flash, fichier texte, écho des envois, redirections avec
/// cookie, téléchargement sans fin, 404 sinon. Il note l'en-tête Cookie reçu pour chaque chemin.
/// </summary>
sealed class TestServer : IDisposable
{
    readonly HttpListener _listener = new();
    readonly Dictionary<string, (byte[] Body, string Type)> _files = new(StringComparer.Ordinal);
    readonly Dictionary<string, (string Target, string SetCookie)> _redirects = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<string, string> _cookies = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<string, System.Collections.Specialized.NameValueCollection> _headers = new(StringComparer.Ordinal);
    readonly HashSet<string> _slow = new(StringComparer.Ordinal);

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

    /// <summary>Redirection (302) vers <paramref name="target"/>, qui dépose un cookie au passage.</summary>
    public void Redirect(string path, string target, string setCookie) => _redirects[path] = (target, setCookie);

    /// <summary>Téléchargement qui ne finit pas : 1000 octets toutes les 50 ms, jusqu'à la fin du serveur.</summary>
    public void AddSlow(string path) => _slow.Add(path);

    /// <summary>En-tête Cookie reçu pour ce chemin (vide sans cookie), null s'il n'a pas été demandé.</summary>
    public string? CookieHeader(string path) => _cookies.TryGetValue(path, out string? cookie) ? cookie : null;

    /// <summary>Adresse demandée au moins une fois.</summary>
    public bool WasRequested(string path) => _cookies.ContainsKey(path);

    /// <summary>En-tête de la dernière requête reçue pour ce chemin (null : absent, ou chemin jamais demandé).</summary>
    public string? Header(string path, string name) => _headers.TryGetValue(path, out var headers) ? headers[name] : null;

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
            _cookies[path] = context.Request.Headers["Cookie"] ?? string.Empty;
            _headers[path] = new System.Collections.Specialized.NameValueCollection(context.Request.Headers);
            HttpListenerResponse response = context.Response;
            if (_redirects.TryGetValue(path, out (string Target, string SetCookie) redirect))
            {
                response.StatusCode = 302;
                response.RedirectLocation = redirect.Target;
                response.AddHeader("Set-Cookie", redirect.SetCookie);
                response.Close();
            }
            else if (_slow.Contains(path))
            {
                response.ContentType = "application/octet-stream";
                response.SendChunked = true;
                _ = Task.Run(() => TrickleAsync(response));
            }
            else if (context.Request.HttpMethod == "POST" && path.EndsWith("echo", StringComparison.Ordinal))
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

    async Task TrickleAsync(HttpListenerResponse response)
    {
        byte[] chunk = new byte[1000];
        try
        {
            while (_listener.IsListening)
            {
                await response.OutputStream.WriteAsync(chunk);
                await response.OutputStream.FlushAsync();
                await Task.Delay(50);
            }
        }
        catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or IOException or InvalidOperationException)
        {
            // Client parti (chargement abandonné) ou serveur arrêté.
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
