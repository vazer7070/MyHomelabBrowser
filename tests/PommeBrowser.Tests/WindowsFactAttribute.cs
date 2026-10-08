namespace PommeBrowser.Tests;

/// <summary>Test propre à Windows (fenêtres, saisie) : exécuté par la compilation Windows seulement.</summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = "Windows uniquement";
    }
}
