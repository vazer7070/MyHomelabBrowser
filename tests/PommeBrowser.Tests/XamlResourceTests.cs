using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace PommeBrowser.Tests;

/// <summary>
/// Erreurs de XAML que la compilation laisse passer et qui ne se voient qu'à l'exécution
/// (XamlParseException à l'ouverture de la fenêtre concernée).
/// </summary>
public sealed class XamlResourceTests
{
    static readonly XNamespace XamlNs = "http://schemas.microsoft.com/winfx/2006/xaml";
    static readonly XNamespace PresentationNs = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    [Fact]
    public void EveryReferencedResourceKeyExists()
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (string file in RepositoryFiles.Sources("*.xaml"))
        {
            foreach (Match m in Regex.Matches(File.ReadAllText(file), @"x:Key=""([^""{]+)"""))
                keys.Add(m.Groups[1].Value);
        }

        var missing = new List<string>();
        foreach (string file in RepositoryFiles.Sources("*.xaml"))
        {
            foreach (Match m in Regex.Matches(File.ReadAllText(file), @"\{(?:StaticResource|DynamicResource)\s+([A-Za-z_]\w*)\}"))
            {
                if (!keys.Contains(m.Groups[1].Value))
                    missing.Add($"{RepositoryFiles.Relative(file)}: {m.Groups[1].Value}");
            }
        }

        foreach (string file in RepositoryFiles.Sources("*.cs"))
        {
            foreach (Match m in Regex.Matches(File.ReadAllText(file), @"(?:FindResource|SetResourceReference\([^,]+,)\s*\(?\s*""([^""]+)"""))
            {
                if (!keys.Contains(m.Groups[1].Value))
                    missing.Add($"{RepositoryFiles.Relative(file)}: {m.Groups[1].Value}");
            }
        }

        Assert.True(missing.Count == 0, "Ressources introuvables :" + Environment.NewLine + string.Join(Environment.NewLine, missing));
    }

    [Fact]
    public void EverySetterAndTriggerTargetsADependencyProperty()
    {
        // Un Setter sur une propriété CLR simple (ex. Window.WindowStartupLocation) compile,
        // puis fait planter la fenêtre qui utilise le style.
        string? wpfDirectory = FindWpfAssemblies();
        if (wpfDirectory == null)
            return; // Assemblys WPF absents (SDK sans le pack Windows Desktop) : rien à vérifier.

        using var context = CreateLoadContext(wpfDirectory);
        var wpf = new WpfTypes(context, wpfDirectory, LocalBaseTypes());

        var problems = new List<string>();
        foreach (string file in RepositoryFiles.Sources("*.xaml"))
        {
            XDocument doc = XDocument.Load(file, LoadOptions.SetLineInfo);
            foreach (XElement element in doc.Descendants().Where(e => e.Name.LocalName is "Setter" or "Trigger" or "Condition"))
            {
                if (element.Attribute("Property")?.Value is not { } property)
                    continue;

                string where = $"{RepositoryFiles.Relative(file)}:{((IXmlLineInfo)element).LineNumber}";
                (Type? owner, string name) = ResolveProperty(wpf, element, property);

                if (owner == null)
                    problems.Add($"{where}: type introuvable pour « {property} »");
                else if (!HasDependencyProperty(owner, name))
                    problems.Add($"{where}: {owner.Name}.{name} n'est pas une propriété de dépendance");
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    static (Type? Owner, string Name) ResolveProperty(WpfTypes wpf, XElement element, string property)
    {
        int dot = property.LastIndexOf('.');
        if (dot > 0)
            return (wpf.Resolve(element, property[..dot]), property[(dot + 1)..]);

        string? targetName = element.Attribute("TargetName")?.Value ?? element.Attribute("SourceName")?.Value;
        return (targetName != null ? NamedElementType(wpf, element, targetName) : ContextType(wpf, element), property);
    }

    // Type visé par un Setter/Trigger sans TargetName : celui du style ou du modèle englobant.
    static Type? ContextType(WpfTypes wpf, XElement element)
    {
        for (XElement? parent = element.Parent; parent != null; parent = parent.Parent)
        {
            if (parent.Name.LocalName is "Style" or "ControlTemplate" or "DataTemplate")
            {
                if (parent.Attribute("TargetType")?.Value is { } targetType)
                    return wpf.Resolve(parent, targetType);

                // Style en ligne (<Button.Style>) : type de l'élément qui le porte.
                if (parent.Name.LocalName == "Style" && parent.Parent?.Parent is { } owner)
                    return wpf.ElementType(owner);
            }
        }
        return null;
    }

    static Type? NamedElementType(WpfTypes wpf, XElement element, string name)
    {
        for (XElement? parent = element.Parent; parent != null; parent = parent.Parent)
        {
            if (parent.Name.LocalName is not ("ControlTemplate" or "DataTemplate" or "Style"))
                continue;

            XElement? named = parent.Descendants().FirstOrDefault(d =>
                d.Attribute(XamlNs + "Name")?.Value == name || d.Attribute("Name")?.Value == name);
            if (named != null)
                return wpf.ElementType(named);
        }
        return null;
    }

    static bool HasDependencyProperty(Type type, string name)
    {
        for (Type? t = type; t != null; t = t.BaseType)
        {
            if (t.GetField(name + "Property", BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly) != null)
                return true;
        }
        return false;
    }

    // Classes de l'application (fenêtres, contrôles) : leur classe de base, pour remonter jusqu'à WPF.
    static Dictionary<string, string> LocalBaseTypes()
    {
        var bases = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string file in RepositoryFiles.Sources("*.cs"))
        {
            foreach (Match m in Regex.Matches(File.ReadAllText(file), @"\bclass\s+(\w+)\s*:\s*([\w.]+)"))
                bases.TryAdd(m.Groups[1].Value, m.Groups[2].Value.Split('.').Last());
        }
        return bases;
    }

    static MetadataLoadContext CreateLoadContext(string wpfDirectory)
    {
        string runtimeDirectory = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        IEnumerable<string> assemblies = Directory.GetFiles(wpfDirectory, "*.dll")
            .Concat(Directory.GetFiles(runtimeDirectory, "*.dll"))
            .GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First());
        return new MetadataLoadContext(new PathAssemblyResolver(assemblies), "System.Runtime");
    }

    // Assemblys de référence WPF : pack Windows Desktop du SDK, sinon cache NuGet.
    static string? FindWpfAssemblies()
    {
        string framework = $"net{Environment.Version.Major}.0";
        string runtimeDirectory = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        string dotnetRoot = Path.GetFullPath(Path.Combine(runtimeDirectory, "..", "..", ".."));
        string nuget = Environment.GetEnvironmentVariable("NUGET_PACKAGES")
                       ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");

        string[] packs =
        {
            Path.Combine(dotnetRoot, "packs", "Microsoft.WindowsDesktop.App.Ref"),
            Path.Combine(nuget, "microsoft.windowsdesktop.app.ref")
        };

        return packs
            .Where(Directory.Exists)
            .SelectMany(Directory.GetDirectories)
            .Select(version => Path.Combine(version, "ref", framework))
            .Where(dir => File.Exists(Path.Combine(dir, "PresentationFramework.dll")))
            .OrderByDescending(dir => Version.TryParse(Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(dir))), out Version? v) ? v : new Version())
            .FirstOrDefault();
    }

    sealed class WpfTypes
    {
        readonly Dictionary<string, Type> _types = new(StringComparer.Ordinal);
        readonly Dictionary<string, string> _localBases;

        public WpfTypes(MetadataLoadContext context, string directory, Dictionary<string, string> localBases)
        {
            _localBases = localBases;
            foreach (string name in new[] { "WindowsBase", "PresentationCore", "PresentationFramework" })
            {
                foreach (Type type in context.LoadFromAssemblyPath(Path.Combine(directory, name + ".dll")).GetExportedTypes())
                {
                    if (type.Namespace?.StartsWith("System.Windows", StringComparison.Ordinal) == true)
                        _types.TryAdd(type.Name, type);
                }
            }
        }

        public Type? Resolve(XElement context, string typeName)
        {
            typeName = typeName.Trim();
            if (typeName.StartsWith("{x:Type", StringComparison.Ordinal))
                typeName = typeName[7..].TrimEnd('}').Trim();

            int colon = typeName.IndexOf(':');
            return colon < 0
                ? Find(typeName)
                : context.GetNamespaceOfPrefix(typeName[..colon])?.NamespaceName.StartsWith("clr-namespace:", StringComparison.Ordinal) == true
                    ? FindLocal(typeName[(colon + 1)..])
                    : Find(typeName[(colon + 1)..]);
        }

        public Type? ElementType(XElement element)
            => element.Name.Namespace == PresentationNs
                ? Find(element.Name.LocalName)
                : FindLocal(element.Name.LocalName);

        Type? Find(string name) => _types.GetValueOrDefault(name);

        Type? FindLocal(string name)
        {
            for (int depth = 0; depth < 10 && name != null; depth++)
            {
                if (_types.TryGetValue(name, out Type? type))
                    return type;
                name = _localBases.GetValueOrDefault(name)!;
            }
            return null;
        }
    }
}
