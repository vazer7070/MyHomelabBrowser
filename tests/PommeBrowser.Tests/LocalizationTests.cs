using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using MyHomelabBrowser.classes.Localization;

namespace PommeBrowser.Tests;

/// <summary>
/// Loc est statique : ces tests ne tournent pas en parallèle des autres,
/// qui comparent des textes français.
/// </summary>
[CollectionDefinition(nameof(LocalizationCollection), DisableParallelization = true)]
public sealed class LocalizationCollection
{
}

[Collection(nameof(LocalizationCollection))]
public sealed class LocalizationTests
{
    static readonly string[] DisplayProperties = { "Text", "Content", "Header", "ToolTip", "Title", "InputGestureText" };
    static readonly string[] InlineElements = { "TextBlock", "Run", "Span", "Bold", "Italic", "Hyperlink" };
    static readonly Regex Letters = new("[A-Za-zÀ-ÿ]{2,}");
    static readonly Regex Placeholder = new(@"\{(\d+)(?:[,:][^}]*)?\}");

    [Fact]
    public void EveryTranslatableTextHasAnEnglishTranslation()
    {
        IReadOnlyDictionary<string, string> table = LoadEnglishTable();

        List<string> missing = CollectKeys()
            .Where(k => !table.ContainsKey(k.Key))
            .Select(k => $"{k.Value}: {k.Key}")
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        Assert.True(missing.Count == 0,
            "Textes absents de lang/en.json :" + Environment.NewLine + string.Join(Environment.NewLine, missing));
    }

    [Fact]
    public void TranslationsOnlyUseArgumentsOfTheFrenchText()
    {
        // Un {n} inconnu du texte français ferait planter string.Format à l'affichage.
        var invalid = LoadEnglishTable()
            .Where(e => !Indexes(e.Value).IsSubsetOf(Indexes(e.Key)))
            .Select(e => $"{e.Key} → {e.Value}")
            .ToList();

        Assert.Empty(invalid);
    }

    [Fact]
    public void TranslationKeysAreLiterals()
    {
        // Tr($"…") produirait une clé différente à chaque appel : jamais traduite.
        var interpolated = RepositoryFiles.Sources("*.cs")
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), @"\bTr\(\s*\$").Select(_ => RepositoryFiles.Relative(f)))
            .ToList();

        Assert.Empty(interpolated);
    }

    [Fact]
    public void TrTranslatesFormatsAndFallsBackToFrench()
    {
        try
        {
            Loc.Initialize("en", new Dictionary<string, string>
            {
                ["Fermer"] = "Close",
                ["{0} onglets"] = "{0} tabs"
            });

            Assert.True(Loc.IsTranslating);
            Assert.Equal("Close", Loc.Tr("Fermer"));
            Assert.Equal("1,234 tabs", Loc.Tr("{0} onglets", 1234.ToString("N0", Loc.Culture)));
            Assert.Equal("Texte sans traduction", Loc.Tr("Texte sans traduction"));
        }
        finally
        {
            Loc.Initialize("fr", null);
        }

        Assert.False(Loc.IsTranslating);
        Assert.Equal("Fermer", Loc.Tr("Fermer"));
    }

    static IReadOnlyDictionary<string, string> LoadEnglishTable()
    {
        using FileStream stream = File.OpenRead(Path.Combine(RepositoryFiles.Root, "lang", "en.json"));
        return Loc.LoadTable(stream);
    }

    static HashSet<string> Indexes(string text)
        => Placeholder.Matches(text).Select(m => m.Groups[1].Value).ToHashSet();

    static Dictionary<string, string> CollectKeys()
    {
        var keys = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (string file in RepositoryFiles.Sources("*.cs"))
        {
            // Les exemples des commentaires ne sont pas des textes de l'interface.
            string source = string.Join('\n', File.ReadLines(file).Where(l => !l.TrimStart().StartsWith("//")));
            foreach (Match m in Regex.Matches(source, @"\bTr\(\s*""((?:[^""\\]|\\.)*)"""))
                Add(keys, Unescape(m.Groups[1].Value), file);

            // Descriptions des commandes : enregistrées en français, traduites à l'affichage.
            if (Path.GetFileName(file) == "SettingsService.cs")
            {
                foreach (Match m in Regex.Matches(source, @"Description = ""([^""]+)"""))
                    Add(keys, m.Groups[1].Value, file);
            }
        }

        foreach (string file in RepositoryFiles.Sources("*.xaml"))
        {
            foreach (XElement element in XDocument.Load(file).Descendants())
            {
                foreach (XAttribute attribute in element.Attributes())
                {
                    string property = attribute.Name.LocalName.Split('.').Last();
                    if (DisplayProperties.Contains(property) && !attribute.Value.StartsWith('{'))
                        Add(keys, attribute.Value, file);
                }

                if (InlineElements.Contains(element.Name.LocalName))
                {
                    foreach (XText text in element.Nodes().OfType<XText>())
                        Add(keys, string.Join(' ', text.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)), file);
                }
            }
        }

        return keys;
    }

    static void Add(Dictionary<string, string> keys, string text, string file)
    {
        if (Letters.IsMatch(text))
            keys.TryAdd(text, RepositoryFiles.Relative(file));
    }

    static string Unescape(string literal)
    {
        var builder = new StringBuilder(literal.Length);
        for (int i = 0; i < literal.Length; i++)
        {
            if (literal[i] != '\\' || i + 1 == literal.Length)
            {
                builder.Append(literal[i]);
                continue;
            }

            char next = literal[++i];
            builder.Append(next switch
            {
                'n' => '\n',
                'r' => '\r',
                't' => '\t',
                '0' => '\0',
                _ => next
            });
        }
        return builder.ToString();
    }
}
