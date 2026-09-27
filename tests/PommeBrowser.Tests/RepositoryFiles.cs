namespace PommeBrowser.Tests;

/// <summary>
/// Fichiers sources de l'application dans le dépôt (hors tests et sorties de compilation).
/// </summary>
static class RepositoryFiles
{
    public static readonly string Root = FindRoot();

    public static IEnumerable<string> Sources(string pattern)
    {
        string[] excluded = { "bin", "obj", "tests", ".git", ".vs" };
        return Directory.EnumerateFiles(Root, pattern, SearchOption.AllDirectories)
            .Where(f => !Relative(f).Split('/').Any(part => excluded.Contains(part, StringComparer.OrdinalIgnoreCase)));
    }

    public static string Relative(string path) => Path.GetRelativePath(Root, path).Replace('\\', '/');

    static string FindRoot()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "MyHomelabBrowser.sln")))
                return dir.FullName;
        }
        throw new DirectoryNotFoundException("Racine du dépôt introuvable.");
    }
}
