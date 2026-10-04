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

        /// <summary>
        /// Préfixe de la liste de tous les contenus d'un document (tableau JSON de descriptions),
        /// envoyée par le script de détection à chaque changement.
        /// </summary>
        public const string ListPrefix = "contents:";

        /// <summary>Contenus d'un document retenus au plus.</summary>
        public const int MaxListed = 16;

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

        /// <summary>
        /// Même contenu : même fichier, même document, même identifiant d'élément, mêmes flashvars
        /// (taille et paramètres mis à part).
        /// </summary>
        public bool IsSameAs(FlashContent other)
            => Swf == other.Swf && Page == other.Page && string.Equals(Id, other.Id, StringComparison.Ordinal) &&
               string.Equals(FlashVars, other.FlashVars, StringComparison.Ordinal);

        /// <summary>Clé de l'identité du contenu (voir <see cref="IsSameAs"/>).</summary>
        public string Identity => string.Join('\n', Page.GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped),
            Swf.GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped), Id ?? string.Empty, FlashVars ?? string.Empty);

        /// <summary>Pour le journal : fichier, taille et identifiant.</summary>
        /// <summary>Nom court du fichier du contenu (« EvonyClient1921.swf »), pour le journal.</summary>
        public static string ShortName(Uri swf)
        {
            string name = System.IO.Path.GetFileName(swf.AbsolutePath);
            return name.Length > 0 ? name : swf.Host;
        }

        public override string ToString()
            => $"{Swf.GetLeftPart(UriPartial.Path)} ({Width}×{Height}{(Id is { Length: > 0 } ? ", id " + Id : string.Empty)})";

        /// <summary>
        /// Liste des contenus d'un document (« contents: ») : descriptions valides, toutes du même
        /// document, <see cref="MaxListed"/> au plus ; vide si la liste est invalide.
        /// </summary>
        public static IReadOnlyList<FlashContent> ParseList(string json)
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(json);
                if (document.RootElement.ValueKind != JsonValueKind.Array)
                    return Array.Empty<FlashContent>();
                var list = new List<FlashContent>();
                foreach (JsonElement item in document.RootElement.EnumerateArray())
                {
                    if (list.Count >= MaxListed)
                        break;
                    if (Parse(item.GetRawText()) is not { } content)
                        continue;
                    if (list.Count > 0 && content.Page != list[0].Page)
                        return Array.Empty<FlashContent>();
                    if (!list.Exists(c => c.IsSameAs(content)))
                        list.Add(content);
                }
                return list;
            }
            catch (JsonException)
            {
                return Array.Empty<FlashContent>();
            }
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
    /// rapport pixels CSS / pixels de l'écran, et visibilité. Contenu d'un cadre : zone du cadre
    /// où il est visible (<paramref name="Clip"/>, pixels CSS). Données de la page : bornées.
    /// </summary>
    public sealed record FlashRect(double X, double Y, double Width, double Height, double PixelRatio, bool Visible,
                                   FlashClip? Clip = null)
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
                FlashClip? clip = null;
                if (root.TryGetProperty("clip", out JsonElement area) && area.ValueKind == JsonValueKind.Object)
                {
                    if (!Number(area, "x", out double clipX) || !Number(area, "y", out double clipY) ||
                        !Number(area, "w", out double clipWidth) || !Number(area, "h", out double clipHeight) ||
                        Math.Abs(clipX) > MaxCoordinate || Math.Abs(clipY) > MaxCoordinate ||
                        clipWidth is < 0 or > MaxSize || clipHeight is < 0 or > MaxSize)
                        return null;
                    clip = new FlashClip(clipX, clipY, clipWidth, clipHeight);
                }
                return new FlashRect(x, y, width, height, ratio, visible && width >= 1 && height >= 1, clip);
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
            if (Clip is { } clip)
            {
                // Contenu d'un cadre : seule la partie dans la zone du cadre est montrée.
                left = Math.Max(left, clip.X * factor);
                top = Math.Max(top, clip.Y * factor);
                right = Math.Min(right, (clip.X + clip.Width) * factor);
                bottom = Math.Min(bottom, (clip.Y + clip.Height) * factor);
            }
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

    /// <summary>Zone d'un cadre où son contenu est visible, en pixels CSS dans la zone affichée.</summary>
    public readonly record struct FlashClip(double X, double Y, double Width, double Height);

    /// <summary>
    /// Contenu placé dans la page : partie visible (DIP, dans la zone de la page web), et fenêtre
    /// du lecteur dans cette partie (pixels de l'écran).
    /// </summary>
    public readonly record struct FlashPlacement(
        double Left, double Top, double Width, double Height,
        int ClientX, int ClientY, int ClientWidth, int ClientHeight);
}
