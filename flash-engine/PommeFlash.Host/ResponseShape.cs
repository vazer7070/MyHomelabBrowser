using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PommeFlash.Host
{
    /// <summary>
    /// Forme d'une petite réponse texte (connexion d'un jeu, configuration…), pour le journal :
    /// ce qu'elle est (XML et son élément racine, JSON et ses clés, formulaire et ses champs,
    /// nombre seul), jamais ses valeurs, qui peuvent être des secrets de session. Un nombre seul
    /// (code de retour) est noté : il n'en est pas un, et dit souvent pourquoi un jeu abandonne.
    /// </summary>
    static partial class ResponseShape
    {
        /// <summary>Début du corps lu, et taille au-delà de laquelle la forme n'est pas notée.</summary>
        public const int Limit = 4096;

        /// <summary>Type de contenu dont la forme est notée (texte, ou type inconnu).</summary>
        public static bool IsText(string? mediaType)
            => mediaType == null || mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ||
               mediaType.EndsWith("xml", StringComparison.OrdinalIgnoreCase) || mediaType.EndsWith("json", StringComparison.OrdinalIgnoreCase) ||
               mediaType.Equals("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase);

        public static string Describe(ReadOnlySpan<byte> body)
        {
            if (body.IsEmpty)
                return "vide";
            string text;
            try
            {
                text = new UTF8Encoding(false, true).GetString(body).Trim().TrimStart('﻿');
            }
            catch (DecoderFallbackException)
            {
                return "binaire";
            }
            if (text.Length == 0)
                return "blancs seulement";
            if (NumberPattern().IsMatch(text))
                return "nombre seul : " + text;
            if (text[0] == '<')
            {
                Match root = RootPattern().Match(text);
                return root.Success ? $"XML ou HTML, élément <{Excerpt(root.Groups[1].Value)}>" : "XML ou HTML";
            }
            if (text[0] is '{' or '[')
                return Json(text);
            if (FormPattern().IsMatch(text))
                return "formulaire, champs : " + Names(text.Split('&').Select(pair => Uri.UnescapeDataString(pair.Split('=', 2)[0])));
            return $"texte ({text.Length} caractères)";
        }

        static string Json(string text)
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(text);
                JsonElement root = document.RootElement;
                return root.ValueKind == JsonValueKind.Object
                    ? "JSON, clés : " + Names(root.EnumerateObject().Select(property => property.Name))
                    : $"JSON, tableau de {root.GetArrayLength()} éléments";
            }
            catch (JsonException)
            {
                return "JSON incomplet ou invalide";
            }
        }

        static string Names(IEnumerable<string> names)
        {
            List<string> list = names.Where(name => name.Length > 0).Select(Excerpt).Take(20).ToList();
            return list.Count == 0 ? "aucun" : string.Join(", ", list);
        }

        static string Excerpt(string name) => name.Length <= 40 ? name : name[..40] + "…";

        // Code de retour : un nombre court (pas un jeton numérique).
        [GeneratedRegex(@"^-?\d{1,6}$")]
        private static partial Regex NumberPattern();

        // Premier élément, après la déclaration XML, les commentaires et le doctype.
        [GeneratedRegex(@"^(?:\s*(?:<\?.*?\?>|<!--.*?-->|<!DOCTYPE[^>]*>))*\s*<([A-Za-z_][\w:.-]*)", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
        private static partial Regex RootPattern();

        [GeneratedRegex(@"^[\w.%\[\]-]+=[^&=\s]*(?:&[\w.%\[\]-]+=[^&=\s]*)*$")]
        private static partial Regex FormPattern();
    }
}
