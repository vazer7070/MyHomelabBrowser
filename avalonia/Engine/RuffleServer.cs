using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using MyHomelabBrowser.classes;

namespace PommeBrowser.Engine
{
    /// <summary>
    /// Fichiers de Ruffle servis sur l'adresse locale (127.0.0.1, port choisi au hasard) pour les
    /// moteurs qui ne permettent pas d'ajouter un schéma d'adresse à une vue déjà créée (WKWebView).
    /// Seuls les fichiers de Ruffle vérifiés à la compilation sont servis ; rien d'autre.
    /// </summary>
    static class RuffleServer
    {
        static readonly object Gate = new();
        static HttpListener? _listener;
        static string? _baseUrl;

        public static string BaseUrl
        {
            get
            {
                lock (Gate)
                {
                    if (_baseUrl != null)
                        return _baseUrl;
                    try
                    {
                        int port = FreePort();
                        string prefix = $"http://127.0.0.1:{port}/";
                        var listener = new HttpListener();
                        listener.Prefixes.Add(prefix);
                        listener.Start();
                        _listener = listener;
                        _baseUrl = prefix + "ruffle/";
                        _ = Task.Run(() => ServeAsync(listener, _baseUrl));
                    }
                    catch (Exception ex) when (ex is HttpListenerException or SocketException or InvalidOperationException or PlatformNotSupportedException)
                    {
                        RuntimeLogBuffer.Append("[Ruffle] Serveur local indisponible : " + ex.Message);
                        _baseUrl = "http://127.0.0.1:9/ruffle/";
                    }
                    return _baseUrl;
                }
            }
        }

        static int FreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        static async Task ServeAsync(HttpListener listener, string baseUrl)
        {
            while (listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await listener.GetContextAsync();
                }
                catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
                {
                    return;
                }

                try
                {
                    HttpListenerResponse response = context.Response;
                    string path = context.Request.Url?.AbsolutePath ?? string.Empty;
                    (byte[] Data, string ContentType)? file = context.Request.HttpMethod is "GET" or "HEAD" && path.StartsWith("/ruffle/", StringComparison.Ordinal)
                        ? RuffleContent.Read(path["/ruffle/".Length..], baseUrl)
                        : null;

                    response.AddHeader("Access-Control-Allow-Origin", "*");
                    response.AddHeader("X-Content-Type-Options", "nosniff");
                    if (file is { } content)
                    {
                        response.StatusCode = 200;
                        response.ContentType = content.ContentType;
                        response.AddHeader("Cache-Control", "max-age=604800, immutable");
                        response.ContentLength64 = content.Data.Length;
                        if (context.Request.HttpMethod == "GET")
                            await response.OutputStream.WriteAsync(content.Data);
                    }
                    else
                    {
                        response.StatusCode = 404;
                    }
                    response.Close();
                }
                catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException or System.IO.IOException)
                {
                    // Page fermée pendant l'envoi.
                }
            }
        }
    }
}
