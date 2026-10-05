using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace PommeBrowser.Engine
{
    /// <summary>
    /// Scripts d'un contenu Flash logé dans un cadre d'une autre origine que la page (Demon Slayer :
    /// jeu de s81fr.sq.koramgame.com dans la page de game.fr.demon.koramgame.com). Dans un
    /// navigateur, ses ExternalInterface.call (isSafeFlash, setAllowLeave…) tournent dans le
    /// document de ce cadre ; le document principal n'y a pas accès. Le moteur les y exécute
    /// lui-même (WebView2 : protocole DevTools), dans ce cadre seulement, et seulement s'il a
    /// toujours l'origine du contenu au moment de l'exécution.
    /// </summary>
    public static class FlashFrames
    {
        /// <summary>
        /// Cadre du contenu dans l'arbre des cadres (Page.getFrameTree : { frame: { id, url },
        /// childFrames: […] }) : celui dont l'adresse est <paramref name="page"/>, ou à défaut le
        /// seul cadre de son origine (adresse changée depuis par la page). Jamais le document
        /// principal. Null : introuvable (cadre d'un autre site, tenu par un autre processus).
        /// </summary>
        public static string? FindFrameId(JsonElement frameTree, Uri page)
        {
            var frames = new List<(string Id, Uri Url)>();
            Collect(frameTree, root: true, frames);
            foreach ((string id, Uri url) in frames)
            {
                if (Uri.Compare(url, page, UriComponents.HttpRequestUrl, UriFormat.UriEscaped, StringComparison.Ordinal) == 0)
                    return id;
            }
            List<string> sameOrigin = frames.Where(frame => SameOrigin(frame.Url, page)).Select(frame => frame.Id).ToList();
            return sameOrigin.Count == 1 ? sameOrigin[0] : null;
        }

        static void Collect(JsonElement node, bool root, List<(string, Uri)> frames)
        {
            if (node.ValueKind != JsonValueKind.Object)
                return;
            if (!root && node.TryGetProperty("frame", out JsonElement frame) &&
                frame.TryGetProperty("id", out JsonElement id) && id.ValueKind == JsonValueKind.String &&
                frame.TryGetProperty("url", out JsonElement url) && url.ValueKind == JsonValueKind.String &&
                Uri.TryCreate(url.GetString(), UriKind.Absolute, out Uri? address) && address.Scheme is "http" or "https")
            {
                frames.Add((id.GetString()!, address));
            }
            if (node.TryGetProperty("childFrames", out JsonElement children) && children.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement child in children.EnumerateArray())
                    Collect(child, root: false, frames);
            }
        }

        static bool SameOrigin(Uri a, Uri b)
            => Uri.Compare(a, b, UriComponents.SchemeAndServer, UriFormat.UriEscaped, StringComparison.OrdinalIgnoreCase) == 0;

        /// <summary>
        /// Script exécuté dans le monde isolé de PommeBrowser du cadre (que les scripts de la page
        /// ne modifient pas) : il vérifie l'origine du document, puis fait exécuter
        /// <paramref name="script"/> dans le monde de la page par un élément &lt;script&gt; (comme
        /// NPN_Evaluate : portée globale, valeur de la dernière instruction), qui laisse son
        /// résultat sur l'élément. Rend { ok, value } (value : texte, JSON, ou null).
        /// </summary>
        public static string InFrameScript(Uri page, string script) => """
            (() => {
              if (location.origin !== __ORIGIN__) return { ok: false };
              const element = document.createElement('script');
              element.textContent = '(' + __RUN__ + ')(' + JSON.stringify(__CODE__) + ');';
              (document.head || document.documentElement).appendChild(element);
              element.remove();
              if (!element.hasAttribute('data-pomme-done')) return { ok: false };
              return { ok: true, value: element.hasAttribute('data-pomme-result') ? element.getAttribute('data-pomme-result') : null };
            })()
            """.Replace("__ORIGIN__", JsonSerializer.Serialize(Origin(page)), StringComparison.Ordinal)
               .Replace("__RUN__", JsonSerializer.Serialize(RunInPage), StringComparison.Ordinal)
               .Replace("__CODE__", JsonSerializer.Serialize(script), StringComparison.Ordinal);

        /// <summary>Origine comme location.origin : hôte en Punycode, port seulement s'il n'est pas celui par défaut.</summary>
        public static string Origin(Uri page)
            => page.Scheme + "://" + (page.HostNameType == UriHostNameType.IPv6 ? page.Host : page.IdnHost) +
               (page.IsDefaultPort ? string.Empty : ":" + page.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));

        /// <summary>Monde de la page : le code évalué en portée globale, son résultat (comme ExecuteScriptAsync) posé sur l'élément.</summary>
        const string RunInPage = """
            function (code) {
              const element = document.currentScript;
              let value;
              try { value = (0, eval)(code); } catch (e) { value = undefined; }
              if (value !== undefined && value !== null) {
                let text;
                try { text = typeof value === 'string' ? value : JSON.stringify(value); } catch (e) { text = String(value); }
                if (typeof text === 'string') element.setAttribute('data-pomme-result', text);
              }
              element.setAttribute('data-pomme-done', '');
            }
            """;

        /// <summary>
        /// Réponse de Runtime.evaluate à <see cref="InFrameScript"/> : (ok, valeur). Null : erreur
        /// du protocole (document du cadre remplacé depuis : son monde isolé avec lui).
        /// </summary>
        public static (bool Ok, string? Value)? ReadResult(string reply)
        {
            using JsonDocument document = JsonDocument.Parse(reply);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("result", out JsonElement result))
                return null;
            if (root.TryGetProperty("exceptionDetails", out _) ||
                !result.TryGetProperty("value", out JsonElement value) || value.ValueKind != JsonValueKind.Object ||
                !value.TryGetProperty("ok", out JsonElement ok) || ok.ValueKind != JsonValueKind.True)
                return (false, null);
            return (true, value.TryGetProperty("value", out JsonElement text) && text.ValueKind == JsonValueKind.String ? text.GetString() : null);
        }
    }
}
