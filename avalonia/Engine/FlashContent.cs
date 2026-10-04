using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
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

        /// <summary>
        /// Formats publicitaires courants (IAB) : sur une page « jeu + publicités Flash », un
        /// contenu de cette taille cède la place à un contenu d'une autre taille, même plus petit.
        /// </summary>
        public static readonly IReadOnlyList<(int Width, int Height)> AdSizes = new[]
        {
            (728, 90), (970, 90), (970, 250), (468, 60), (234, 60), (320, 50), (320, 100),
            (300, 250), (336, 280), (250, 250), (200, 200), (180, 150), (125, 125),
            (160, 600), (120, 600), (300, 600), (120, 240), (88, 31)
        };

        /// <summary>Taille d'un format publicitaire courant.</summary>
        public bool HasAdSize => AdSizes.Contains((Width, Height));

        /// <summary>
        /// Contenu à confier au moteur intégré plutôt que <paramref name="current"/> (contenu
        /// principal retenu jusque-là, d'un autre document de la page par exemple) : celui qui n'a
        /// pas une taille de publicité, sinon le plus grand.
        /// </summary>
        public bool IsPreferredOver(FlashContent? current)
        {
            if (current == null)
                return true;
            if (HasAdSize != current.HasAdSize)
                return !HasAdSize;
            return Area > current.Area;
        }

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

    /// <summary>
    /// Position du contenu lu par le moteur intégré dans la page (script de suivi, voir
    /// RuffleContent.FlashTrackerScript) : rectangle en pixels CSS par rapport à la zone affichée,
    /// rapport pixels CSS / pixels de l'écran, et visibilité. Données de la page : bornées.
    /// </summary>
    public sealed record FlashRect(double X, double Y, double Width, double Height, double PixelRatio, bool Visible)
    {
        const double MaxCoordinate = 1_000_000;
        const double MaxSize = 100_000;

        /// <summary>Position reçue de la page, ou null si elle est invalide.</summary>
        public static FlashRect? Parse(string json)
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(json);
                JsonElement root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    !Number(root, "x", out double x) || !Number(root, "y", out double y) ||
                    !Number(root, "w", out double width) || !Number(root, "h", out double height) ||
                    !Number(root, "dpr", out double ratio))
                    return null;
                if (Math.Abs(x) > MaxCoordinate || Math.Abs(y) > MaxCoordinate ||
                    width is < 0 or > MaxSize || height is < 0 or > MaxSize || ratio is <= 0 or > 16)
                    return null;
                bool visible = root.TryGetProperty("visible", out JsonElement shown) && shown.ValueKind == JsonValueKind.True;
                return new FlashRect(x, y, width, height, ratio, visible && width >= 1 && height >= 1);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        static bool Number(JsonElement root, string name, out double value)
        {
            value = 0;
            return root.TryGetProperty(name, out JsonElement element) && element.ValueKind == JsonValueKind.Number &&
                   element.TryGetDouble(out value) && double.IsFinite(value);
        }

        /// <summary>
        /// Placement dans la zone de la page web (<paramref name="areaWidth"/> × <paramref name="areaHeight"/>
        /// DIP, <paramref name="scaling"/> pixels de l'écran par DIP) : partie visible du contenu, en DIP,
        /// et position et taille du contenu entier dans cette partie, en pixels de l'écran (négative
        /// quand le haut ou la gauche est hors de la zone). Null si rien n'est visible.
        /// </summary>
        public FlashPlacement? Place(double areaWidth, double areaHeight, double scaling)
        {
            if (!Visible || !(scaling > 0) || !(areaWidth > 0) || !(areaHeight > 0))
                return null;
            double factor = PixelRatio / scaling;
            double left = Math.Max(0, X * factor);
            double top = Math.Max(0, Y * factor);
            double right = Math.Min(areaWidth, (X + Width) * factor);
            double bottom = Math.Min(areaHeight, (Y + Height) * factor);
            if (right - left < 1 || bottom - top < 1)
                return null;

            // Origine de la fenêtre logée : celle qu'Avalonia lui donne (pixels tronqués).
            int hostX = (int)(left * scaling);
            int hostY = (int)(top * scaling);
            return new FlashPlacement(left, top, right - left, bottom - top,
                (int)Math.Round(X * PixelRatio) - hostX, (int)Math.Round(Y * PixelRatio) - hostY,
                Math.Max(1, (int)Math.Round(Width * PixelRatio)), Math.Max(1, (int)Math.Round(Height * PixelRatio)));
        }
    }

    /// <summary>
    /// Contenu placé dans la page : partie visible (DIP, dans la zone de la page web), et fenêtre
    /// du lecteur dans cette partie (pixels de l'écran).
    /// </summary>
    public readonly record struct FlashPlacement(
        double Left, double Top, double Width, double Height,
        int ClientX, int ClientY, int ClientWidth, int ClientHeight);
}
