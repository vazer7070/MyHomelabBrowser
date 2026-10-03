using System.Globalization;

namespace PommeFlash.Host
{
    /// <summary>
    /// Paramètres de lancement donnés par PommeBrowser :
    /// --plugin &lt;NPSWF64_*.dll&gt; --swf &lt;adresse&gt; --page &lt;adresse de la page&gt;
    /// [--flashvars &lt;…&gt;] [--width N] [--height N] [--id &lt;…&gt;] [--param nom=valeur]…
    /// [--user-agent &lt;…&gt;] [--private] [--hidden].
    /// </summary>
    sealed class HostOptions
    {
        public const string FlashMimeType = "application/x-shockwave-flash";

        public string PluginPath { get; private set; } = string.Empty;
        public Uri Swf { get; private set; } = null!;
        public Uri Page { get; private set; } = null!;
        public string? FlashVars { get; private set; }
        public int Width { get; private set; } = 800;
        public int Height { get; private set; } = 600;
        public string? ElementId { get; private set; }
        public List<KeyValuePair<string, string>> Params { get; } = new();
        public string UserAgent { get; private set; } = BasiliskUserAgent();
        public bool IsPrivate { get; private set; }

        /// <summary>
        /// Cookies partagés avec la page : PommeBrowser donne ceux de chaque adresse chargée et
        /// garde ceux que les réponses déposent. Sans, l'hôte a ses propres cookies, vides au départ.
        /// </summary>
        public bool ShareCookies { get; private set; }

        /// <summary>Fenêtre créée cachée : PommeBrowser la loge dans l'onglet avant de l'afficher.</summary>
        public bool Hidden { get; private set; }

        /// <summary>
        /// Identité de navigateur donnée au module (NPN_UserAgent) et aux chargements : celle de
        /// Basilisk, où les contenus fonctionnent, avec l'architecture de l'hôte (WOW64 pour un
        /// hôte 32 bits sur Windows 64 bits, comme un Basilisk 32 bits).
        /// </summary>
        public static string BasiliskUserAgent()
        {
            Version os = OperatingSystem.IsWindows() ? Environment.OSVersion.Version : new Version(10, 0);
            string platform = Environment.Is64BitProcess ? "; Win64; x64" : Environment.Is64BitOperatingSystem ? "; WOW64" : string.Empty;
            return $"Mozilla/5.0 (Windows NT {os.Major}.{os.Minor}{platform}; rv:140.0) Gecko/20100101 Goanna/6.9 Firefox/140.0 Basilisk/20250701";
        }

        public static HostOptions Parse(IReadOnlyList<string> args)
        {
            var options = new HostOptions();
            string? swf = null;
            string? page = null;
            for (int i = 0; i < args.Count; i++)
            {
                string Value() => i + 1 < args.Count ? args[++i] : throw new ArgumentException($"Valeur manquante après {args[i]}.");

                switch (args[i])
                {
                    case "--plugin":
                        options.PluginPath = Value();
                        break;
                    case "--swf":
                        swf = Value();
                        break;
                    case "--page":
                        page = Value();
                        break;
                    case "--flashvars":
                        options.FlashVars = Value();
                        break;
                    case "--width":
                        options.Width = Math.Clamp(int.Parse(Value(), CultureInfo.InvariantCulture), 1, 16384);
                        break;
                    case "--height":
                        options.Height = Math.Clamp(int.Parse(Value(), CultureInfo.InvariantCulture), 1, 16384);
                        break;
                    case "--id":
                        options.ElementId = Value();
                        break;
                    case "--param":
                        string param = Value();
                        int equals = param.IndexOf('=');
                        if (equals <= 0)
                            throw new ArgumentException($"Paramètre invalide : {param}");
                        options.Params.Add(new(param[..equals].Trim(), param[(equals + 1)..]));
                        break;
                    case "--user-agent":
                        options.UserAgent = Value();
                        break;
                    case "--private":
                        options.IsPrivate = true;
                        break;
                    case "--share-cookies":
                        options.ShareCookies = true;
                        break;
                    case "--hidden":
                        options.Hidden = true;
                        break;
                    default:
                        throw new ArgumentException($"Option inconnue : {args[i]}");
                }
            }

            if (options.PluginPath.Length == 0 || !File.Exists(options.PluginPath))
                throw new ArgumentException("Module Flash introuvable (--plugin).");
            if (swf == null || !Uri.TryCreate(swf, UriKind.Absolute, out Uri? swfUri))
                throw new ArgumentException("Adresse du contenu Flash invalide (--swf).");
            options.Swf = swfUri;
            options.Page = page != null && Uri.TryCreate(page, UriKind.Absolute, out Uri? pageUri) ? pageUri : swfUri;
            return options;
        }

        /// <summary>
        /// Attributs puis paramètres de l'élément, comme un navigateur les passe à NPP_New. Le module
        /// dessine toujours dans sa propre fenêtre : les modes « direct » et « gpu » de la page sont
        /// gardés (fenêtrés sous Windows, ils donnent accès à Stage3D, dont beaucoup de jeux ont
        /// besoin) ; « opaque » et « transparent », qui demandent le dessin sans fenêtre, deviennent
        /// « window ».
        /// </summary>
        public List<KeyValuePair<string, string>> PluginArguments()
        {
            var list = new List<KeyValuePair<string, string>>
            {
                new("type", FlashMimeType),
                new("src", Swf.AbsoluteUri),
                new("width", Width.ToString(CultureInfo.InvariantCulture)),
                new("height", Height.ToString(CultureInfo.InvariantCulture))
            };
            if (ElementId != null)
            {
                list.Add(new("id", ElementId));
                list.Add(new("name", ElementId));
            }

            var parameters = new List<KeyValuePair<string, string>>
            {
                new("movie", Swf.AbsoluteUri),
                new("quality", "high"),
                new("allowscriptaccess", "sameDomain"),
                new("allowfullscreen", "true")
            };
            if (FlashVars != null)
                parameters.Add(new("flashvars", FlashVars));
            foreach (KeyValuePair<string, string> param in Params)
            {
                parameters.RemoveAll(p => string.Equals(p.Key, param.Key, StringComparison.OrdinalIgnoreCase));
                parameters.Add(param);
            }
            parameters.Add(new("wmode", RenderMode(parameters)));

            list.AddRange(parameters);
            return list;
        }

        /// <summary>Mode de rendu passé au module (voir <see cref="PluginArguments"/>) ; retiré de la liste.</summary>
        static string RenderMode(List<KeyValuePair<string, string>> parameters)
        {
            string? requested = parameters.LastOrDefault(p => string.Equals(p.Key, "wmode", StringComparison.OrdinalIgnoreCase)).Value?.Trim();
            parameters.RemoveAll(p => string.Equals(p.Key, "wmode", StringComparison.OrdinalIgnoreCase));
            return requested != null && (requested.Equals("direct", StringComparison.OrdinalIgnoreCase) || requested.Equals("gpu", StringComparison.OrdinalIgnoreCase))
                ? requested.ToLowerInvariant()
                : "window";
        }
    }
}
