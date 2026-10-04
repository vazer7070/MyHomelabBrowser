using System.Net;

namespace PommeFlash.Host
{
    /// <summary>
    /// Chargements du module, comme dans un navigateur. Les redirections sont suivies ici, étape par
    /// étape (20 au plus, comme Firefox) :
    /// <list type="bullet">
    /// <item>Cookies partagés avec la page (<c>--share-cookies</c>) : chaque étape porte les cookies
    /// que PommeBrowser a pour son adresse, et ceux que les réponses déposent (Set-Cookie) lui sont
    /// transmis. PommeBrowser décide de ce qu'il donne et accepte (seulement pour le site de la
    /// page) ; ceux d'un site n'en suivent jamais un autre. Sans partage : cookies propres à l'hôte.</item>
    /// <item>Le module peut demander à être consulté avant chaque redirection d'un chargement
    /// notifié (<see cref="RedirectApproval"/>, NPP_URLRedirectNotify) : Flash y applique ses règles
    /// de sécurité. Refusée, la redirection fait échouer le chargement.</item>
    /// <item>Un envoi (POST) redirigé par 307 ou 308 vers une autre origine n'est pas suivi (Firefox).</item>
    /// </list>
    /// </summary>
    sealed class BrowserHttpHandler : DelegatingHandler
    {
        /// <summary>Comme Firefox (network.http.redirection-limit).</summary>
        const int MaxRedirects = 20;

        /// <summary>Accord du module pour suivre une redirection (adresse suivante, code HTTP).</summary>
        public static readonly HttpRequestOptionsKey<Func<Uri, int, CancellationToken, Task<bool>>> RedirectApproval = new("PommeFlash.RedirectApproval");

        readonly bool _shareCookies;

        /// <param name="cookies">Cookies propres à l'hôte, quand ceux de la page ne sont pas partagés.</param>
        public BrowserHttpHandler(bool shareCookies, CookieContainer cookies)
            : base(Inner(shareCookies, cookies))
        {
            _shareCookies = shareCookies;
        }

        static SocketsHttpHandler Inner(bool shareCookies, CookieContainer cookies)
        {
            var inner = new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                AllowAutoRedirect = false,
                UseCookies = !shareCookies
            };
            if (!shareCookies)
                inner.CookieContainer = cookies;
            return inner;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            byte[]? body = request.Content != null ? await request.Content.ReadAsByteArrayAsync(cancellation).ConfigureAwait(false) : null;
            request.Options.TryGetValue(RedirectApproval, out Func<Uri, int, CancellationToken, Task<bool>>? approve);
            HttpRequestMessage current = request;
            for (int hop = 0; ; hop++)
            {
                if (_shareCookies)
                    await AttachCookiesAsync(current, cancellation).ConfigureAwait(false);
                HttpResponseMessage response = await base.SendAsync(current, cancellation).ConfigureAwait(false);
                if (_shareCookies)
                    ForwardCookies(current.RequestUri!, response);

                if (hop >= MaxRedirects || !IsRedirect(response.StatusCode) || response.Headers.Location is not { } location)
                    return response;
                Uri from = current.RequestUri!;
                Uri next = location.IsAbsoluteUri ? location : new Uri(from, location);
                if (next.Scheme is not ("http" or "https"))
                    return response;
                int status = (int)response.StatusCode;
                if (status is 307 or 308 && current.Method == HttpMethod.Post && !SameOrigin(from, next))
                {
                    HostChannel.Trace("redirect-post:" + from.GetLeftPart(UriPartial.Path),
                        $"Envoi redirigé vers une autre origine, non suivi (comme Firefox) : {from.GetLeftPart(UriPartial.Path)} → {next.GetLeftPart(UriPartial.Path)}");
                    return response;
                }
                HostChannel.Trace("redirect:" + from.GetLeftPart(UriPartial.Path),
                    $"Redirection {status} : {from.GetLeftPart(UriPartial.Path)} → {next.GetLeftPart(UriPartial.Path)}");
                if (approve != null && !await approve(next, status, cancellation).ConfigureAwait(false))
                {
                    response.Dispose();
                    throw new HttpRequestException($"redirection vers {next.GetLeftPart(UriPartial.Path)} refusée par le module");
                }

                HttpRequestMessage following = Follow(request, current, next, response.StatusCode, body);
                response.Dispose();
                if (!ReferenceEquals(current, request))
                    current.Dispose();
                current = following;
            }
        }

        static bool SameOrigin(Uri a, Uri b)
            => a.Scheme == b.Scheme && a.Port == b.Port && string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase);

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
