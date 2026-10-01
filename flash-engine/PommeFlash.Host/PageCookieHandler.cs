using System.Net;

namespace PommeFlash.Host
{
    /// <summary>
    /// Cookies partagés avec la page, comme dans un navigateur : chaque chargement du module, et
    /// chaque redirection, porte les cookies que PommeBrowser a pour cette adresse ; ceux que les
    /// réponses déposent (Set-Cookie) lui sont transmis. PommeBrowser décide de ce qu'il donne et
    /// accepte (seulement pour le site de la page). Les redirections sont suivies ici, pour que
    /// chaque étape porte ses propres cookies et que ceux d'un site n'en suivent jamais un autre.
    /// </summary>
    sealed class PageCookieHandler : DelegatingHandler
    {
        const int MaxRedirects = 10;

        public PageCookieHandler()
            : base(new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                AllowAutoRedirect = false,
                UseCookies = false
            })
        {
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            byte[]? body = request.Content != null ? await request.Content.ReadAsByteArrayAsync(cancellation).ConfigureAwait(false) : null;
            HttpRequestMessage current = request;
            for (int hop = 0; ; hop++)
            {
                await AttachCookiesAsync(current, cancellation).ConfigureAwait(false);
                HttpResponseMessage response = await base.SendAsync(current, cancellation).ConfigureAwait(false);
                ForwardCookies(current.RequestUri!, response);

                if (hop >= MaxRedirects || !IsRedirect(response.StatusCode) || response.Headers.Location is not { } location)
                    return response;
                Uri next = location.IsAbsoluteUri ? location : new Uri(current.RequestUri!, location);
                if (next.Scheme is not ("http" or "https"))
                    return response;

                HttpRequestMessage following = Follow(request, current, next, response.StatusCode, body);
                response.Dispose();
                if (!ReferenceEquals(current, request))
                    current.Dispose();
                current = following;
            }
        }

        static bool IsRedirect(HttpStatusCode status)
            => status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
                or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

        static async Task AttachCookiesAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            request.Headers.Remove("Cookie");
            if (request.RequestUri is not { } uri || uri.Scheme is not ("http" or "https"))
                return;
            string? cookies = await HostChannel.PageCookiesAsync(uri, cancellation).ConfigureAwait(false);
            if (string.IsNullOrEmpty(cookies))
                return;
            request.Headers.TryAddWithoutValidation("Cookie", cookies);
            // Noms seuls dans le journal : les valeurs sont des secrets de session.
            HostChannel.Trace("cookies:" + uri.Host, $"Cookies de la page joints aux chargements de {uri.Host} : {Names(cookies)}");
        }

        static void ForwardCookies(Uri url, HttpResponseMessage response)
        {
            if (!response.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? cookies))
                return;
            foreach (string cookie in cookies)
            {
                HostChannel.SetPageCookie(url, cookie, fromHttp: true);
                string name = SetCookieName(cookie);
                HostChannel.Trace("set-cookie:" + url.Host + "/" + name, $"Cookie reçu de {url.Host}, transmis à la page : {name}");
            }
        }

        /// <summary>Étape suivante : GET après 301, 302 et 303 (comme les navigateurs), même requête après 307 et 308.</summary>
        static HttpRequestMessage Follow(HttpRequestMessage original, HttpRequestMessage current, Uri next, HttpStatusCode status, byte[]? body)
        {
            bool keepMethod = status is HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;
            HttpMethod method = keepMethod || current.Method == HttpMethod.Head ? current.Method : HttpMethod.Get;
            var following = new HttpRequestMessage(method, next) { Version = current.Version };
            foreach (KeyValuePair<string, IEnumerable<string>> header in original.Headers)
            {
                if (!header.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase) &&
                    !header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase) &&
                    !header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase))
                {
                    following.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }
            if (keepMethod && body != null)
            {
                following.Content = new ByteArrayContent(body);
                if (original.Content != null)
                {
                    foreach (KeyValuePair<string, IEnumerable<string>> header in original.Content.Headers)
                        following.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }
            return following;
        }

        /// <summary>Noms des cookies d'un en-tête Cookie (« a=1; b=2 »), sans leurs valeurs.</summary>
        static string Names(string cookies)
            => HostChannel.Excerpt(string.Join(", ", cookies.Split(';').Select(Name).Where(n => n.Length > 0)), 200);

        /// <summary>Nom du cookie d'un en-tête Set-Cookie : son premier couple, avant les attributs.</summary>
        static string SetCookieName(string setCookie) => Name(setCookie.Split(';', 2)[0]);

        static string Name(string pair) => pair.Split('=', 2)[0].Trim();
    }
}
