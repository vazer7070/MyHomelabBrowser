namespace PommeBrowser.Tests;

/// <summary>
/// Test propre à Linux (/proc, droits d'exécution, /bin/sh) : ignoré par la compilation Windows,
/// exécuté par celle de l'édition Linux.
/// </summary>
public sealed class LinuxFactAttribute : FactAttribute
{
    public LinuxFactAttribute()
    {
        if (!OperatingSystem.IsLinux())
            Skip = "Linux uniquement";
    }
}
