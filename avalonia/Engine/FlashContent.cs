using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PommeBrowser.Engine
{
    /// <summary>
    /// Contenu Flash principal d'une page (le plus grand), décrit par le script de détection :
    /// fichier SWF, page qui l'affiche, flashvars, taille et paramètres de l'élément. Ces données
    /// viennent de la page : elles sont bornées et vérifiées avant de servir au moteur intégré.
    /// </summary>
    public sealed partial record FlashContent(
        Uri Swf,
        Uri Page,
        string? FlashVars,
        int Width,
        int Height,
        string? Id,
        IReadOnlyList<KeyValuePair<string, string>> Params)
    {
        /// <summary>Préfixe du message envoyé par le script de détection.</summary>
        public const string MessagePrefix = "content:";

        const int MaxFlashVars = 16 * 1024;
        const int MaxParams = 32;
        const int MaxParamValue = 2048;

        public long Area => (long)Width * Height;

        [GeneratedRegex("^[a-z][a-z0-9_-]{0,63}$", RegexOptions.CultureInvariant)]
        private static partial Regex ParamName();

        /// <summary>Description reçue de la page, ou null si elle est invalide.</summary>
        public static FlashContent? Parse(string json)
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(json);
                JsonElement root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                    return null;

                if (!TryWebUri(Text(root, "swf"), out Uri? swf) || !TryWebUri(Text(root, "page"), out Uri? page))
                    return null;

                string? flashVars = Text(root, "flashvars");
                if (flashVars is { Length: > MaxFlashVars })
                    flashVars = null;

                var parameters = new List<KeyValuePair<string, string>>();
                if (root.TryGetProperty("params", out JsonElement values) && values.ValueKind == JsonValueKind.Object)
                {
                    foreach (JsonProperty param in values.EnumerateObject())
                    {
                        string name = param.Name.ToLowerInvariant();
                        if (parameters.Count >= MaxParams || !ParamName().IsMatch(name) || param.Value.ValueKind != JsonValueKind.String)
                            continue;
                        string value = param.Value.GetString() ?? string.Empty;
                        if (value.Length <= MaxParamValue)
                            parameters.Add(new(name, value));
                    }
                }

                string? id = Text(root, "id");
                if (id is { Length: > 128 })
                    id = null;

                return new FlashContent(swf, page, flashVars, Size(root, "width", 800), Size(root, "height", 600), id, parameters);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        static string? Text(JsonElement root, string name)
            => root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

        /// <summary>Taille en pixels ; valeur par défaut si elle manque ou n'est pas plausible.</summary>
        static int Size(JsonElement root, string name, int fallback)
            => root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number &&
               value.TryGetInt32(out int size) && size is >= 16 and <= 8192
                ? size
                : fallback;

        /// <summary>Seuls les contenus du Web (http, https) sont confiés au moteur intégré.</summary>
        static bool TryWebUri(string? text, [NotNullWhen(true)] out Uri? uri)
        {
            uri = null;
            if (text is null or { Length: > 4096 } || !Uri.TryCreate(text, UriKind.Absolute, out Uri? parsed))
                return false;
            if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
                return false;
            uri = parsed;
            return true;
        }
    }
}
