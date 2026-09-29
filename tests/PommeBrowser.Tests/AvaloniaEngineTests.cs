using Avalonia.Input;
using PommeBrowser.Engine;

namespace PommeBrowser.Tests;

/// <summary>Édition Avalonia : raccourcis pris avant la page et scripts injectés par les moteurs.</summary>
public class AvaloniaEngineTests
{
    [Theory]
    [InlineData(Key.T, KeyModifiers.Control, true)]
    [InlineData(Key.L, KeyModifiers.Control, true)]
    [InlineData(Key.F, KeyModifiers.Control, true)]
    [InlineData(Key.D1, KeyModifiers.Control, true)]
    [InlineData(Key.D1, KeyModifiers.Control | KeyModifiers.Shift, false)]
    [InlineData(Key.Left, KeyModifiers.Alt, true)]
    [InlineData(Key.F5, KeyModifiers.None, true)]
    [InlineData(Key.F12, KeyModifiers.None, true)]
    // Copier, coller, tout sélectionner et la saisie restent à la page.
    [InlineData(Key.C, KeyModifiers.Control, false)]
    [InlineData(Key.V, KeyModifiers.Control, false)]
    [InlineData(Key.A, KeyModifiers.Control, false)]
    [InlineData(Key.A, KeyModifiers.None, false)]
    [InlineData(Key.Escape, KeyModifiers.None, false)]
    public void BrowserShortcutsAreTakenBeforeThePage(Key key, KeyModifiers modifiers, bool expected)
        => Assert.Equal(expected, BrowserShortcuts.IsShortcut(key, modifiers));

    [Theory]
    [InlineData(Key.L, KeyModifiers.Control, true)]
    [InlineData(Key.F, KeyModifiers.Control, true)]
    [InlineData(Key.T, KeyModifiers.Control, true)]
    [InlineData(Key.N, KeyModifiers.Control | KeyModifiers.Shift, true)]
    [InlineData(Key.D, KeyModifiers.Alt, true)]
    [InlineData(Key.F6, KeyModifiers.None, true)]
    [InlineData(Key.F3, KeyModifiers.None, true)]
    // Ces raccourcis laissent le clavier à la page.
    [InlineData(Key.R, KeyModifiers.Control, false)]
    [InlineData(Key.W, KeyModifiers.Control, false)]
    [InlineData(Key.T, KeyModifiers.Control | KeyModifiers.Shift, false)]
    [InlineData(Key.OemPlus, KeyModifiers.Control, false)]
    public void ShortcutsThatOpenAFieldReleaseThePageKeyboard(Key key, KeyModifiers modifiers, bool expected)
    {
        Assert.True(BrowserShortcuts.IsShortcut(key, modifiers));
        Assert.Equal(expected, BrowserShortcuts.MovesKeyboardToWindow(key, modifiers));
    }

    [Fact]
    public void CommandIsControlOnlyOnMacOs()
    {
        KeyModifiers normalized = BrowserShortcuts.Normalize(KeyModifiers.Meta | KeyModifiers.Shift);
        if (OperatingSystem.IsMacOS())
            Assert.Equal(KeyModifiers.Control | KeyModifiers.Shift, normalized);
        else
            Assert.Equal(KeyModifiers.Meta | KeyModifiers.Shift, normalized);
        Assert.Equal(KeyModifiers.Control, BrowserShortcuts.Normalize(KeyModifiers.Control));
    }

    [Fact]
    public void TopFrameScriptsSkipFrames()
    {
        string wrapped = UserScripts.Wrap("doSomething();", allFrames: false, atDocumentStart: true);
        Assert.Contains("if (window !== window.top) return;", wrapped);
        Assert.Contains("doSomething();", wrapped);
        Assert.DoesNotContain("DOMContentLoaded", wrapped);
        Assert.StartsWith("(function () {", wrapped);
        Assert.EndsWith("})();", wrapped);
    }

    [Fact]
    public void DocumentEndScriptsWaitForTheDocument()
    {
        string wrapped = UserScripts.Wrap("doSomething();", allFrames: true, atDocumentStart: false);
        Assert.DoesNotContain("window.top", wrapped);
        Assert.Contains("DOMContentLoaded", wrapped);
        Assert.Contains("document.readyState === 'loading'", wrapped);
        Assert.Equal(wrapped.Count(c => c == '{'), wrapped.Count(c => c == '}'));
        Assert.Equal(wrapped.Count(c => c == '('), wrapped.Count(c => c == ')'));
    }
}
