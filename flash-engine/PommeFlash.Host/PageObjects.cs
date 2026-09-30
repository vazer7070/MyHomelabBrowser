using PommeFlash.Host.Native;

namespace PommeFlash.Host
{
    /// <summary>
    /// La page, telle que le module la voit par npruntime : fenêtre (window, top), emplacement
    /// (location), document, navigateur et l'élément qui porte le contenu. Tout est en lecture
    /// seule : les échanges avec les scripts de la vraie page viendront plus tard.
    /// </summary>
    sealed class PageObjects
    {
        public PageObjects(HostOptions options)
        {
            var location = NpObjects.Wrap(new LocationObject(options.Page));
            var document = NpObjects.Wrap(new DocumentObject(options.Page, location));
            var navigator = NpObjects.Wrap(new NavigatorObject(options.UserAgent));
            var window = new WindowObject(location, document, navigator);
            Window = NpObjects.Wrap(window);
            window.SetSelf(Window);
            Element = NpObjects.Wrap(new ElementObject(options));
        }

        /// <summary>Objet « window » (une référence, gardée par l'instance).</summary>
        public NpObjectRef Window { get; }

        /// <summary>Élément &lt;embed&gt; ou &lt;object&gt; (une référence, gardée par l'instance).</summary>
        public NpObjectRef Element { get; }

        sealed class WindowObject : HostObject
        {
            readonly NpObjectRef _location;
            readonly NpObjectRef _document;
            readonly NpObjectRef _navigator;
            NpObjectRef _self;

            public WindowObject(NpObjectRef location, NpObjectRef document, NpObjectRef navigator)
            {
                _location = location;
                _document = document;
                _navigator = navigator;
            }

            public override bool HasProperty(string name) => name is "location" or "document" or "navigator" or "top" or "self" or "window" or "parent";

            public override bool GetProperty(string name, out object? value)
            {
                value = name switch
                {
                    "location" => _location,
                    "document" => _document,
                    "navigator" => _navigator,
                    "top" or "self" or "window" or "parent" => Self(),
                    _ => null
                };
                return value != null;
            }

            // La fenêtre se désigne elle-même : son objet NPAPI, retrouvé à la première demande.
            object? Self() => _self.Pointer != 0 ? _self : null;

            public void SetSelf(NpObjectRef self) => _self = self;
        }

        sealed class LocationObject : HostObject
        {
            readonly Uri _page;

            public LocationObject(Uri page) => _page = page;

            public override bool HasProperty(string name) => Value(name) != null;

            public override bool GetProperty(string name, out object? value)
            {
                value = Value(name);
                return value != null;
            }

            public override bool HasMethod(string name) => name == "toString";

            public override bool Invoke(string name, object?[] args, out object? result)
            {
                result = name == "toString" ? _page.AbsoluteUri : null;
                return result != null;
            }

            public override bool InvokeDefault(object?[] args, out object? result)
            {
                result = _page.AbsoluteUri;
                return true;
            }

            string? Value(string name) => name switch
            {
                "href" => _page.AbsoluteUri,
                "protocol" => _page.Scheme + ":",
                "host" => _page.IsDefaultPort ? _page.Host : _page.Host + ":" + _page.Port,
                "hostname" => _page.Host,
                "port" => _page.IsDefaultPort ? string.Empty : _page.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "pathname" => _page.AbsolutePath,
                "search" => _page.Query,
                "hash" => _page.Fragment,
                "origin" => _page.GetLeftPart(UriPartial.Authority),
                _ => null
            };
        }

        sealed class DocumentObject : HostObject
        {
            readonly Uri _page;
            readonly NpObjectRef _location;

            public DocumentObject(Uri page, NpObjectRef location)
            {
                _page = page;
                _location = location;
            }

            public override bool HasProperty(string name) => name is "location" or "URL" or "documentURI" or "domain";

            public override bool GetProperty(string name, out object? value)
            {
                value = name switch
                {
                    "location" => _location,
                    "URL" or "documentURI" => _page.AbsoluteUri,
                    "domain" => _page.Host,
                    _ => null
                };
                return value != null;
            }
        }

        sealed class NavigatorObject : HostObject
        {
            readonly string _userAgent;

            public NavigatorObject(string userAgent) => _userAgent = userAgent;

            public override bool HasProperty(string name) => name is "userAgent" or "appName" or "platform";

            public override bool GetProperty(string name, out object? value)
            {
                value = name switch
                {
                    "userAgent" => _userAgent,
                    "appName" => "Netscape",
                    "platform" => "Win32",
                    _ => null
                };
                return value != null;
            }
        }

        sealed class ElementObject : HostObject
        {
            readonly HostOptions _options;

            public ElementObject(HostOptions options) => _options = options;

            public override bool HasProperty(string name) => Value(name) != null;

            public override bool GetProperty(string name, out object? value)
            {
                value = Value(name);
                return value != null;
            }

            string? Value(string name) => name switch
            {
                "id" or "name" => _options.ElementId ?? string.Empty,
                "type" => HostOptions.FlashMimeType,
                "src" or "data" => _options.Swf.AbsoluteUri,
                "nodeName" or "tagName" => "EMBED",
                _ => null
            };
        }
    }
}
