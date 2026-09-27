using MyHomelabBrowser.classes;

namespace PommeBrowser.Tests;

public class AppearanceSettingsTests
{
    [Theory]
    [InlineData(AppTheme.Dark, true, true)]
    [InlineData(AppTheme.Light, false, false)]
    [InlineData(AppTheme.System, true, false)]
    [InlineData(AppTheme.System, false, true)]
    public void Theme_resolution_follows_windows_when_asked(AppTheme theme, bool windowsLight, bool expectedDark)
    {
        var settings = new AppearanceSettings { Theme = theme };
        Assert.Equal(expectedDark, settings.ResolveIsDark(() => windowsLight));
    }

    [Fact]
    public void Unknown_windows_preference_falls_back_to_dark()
        => Assert.True(new AppearanceSettings { Theme = AppTheme.System }.ResolveIsDark(() => null));

    [Fact]
    public void Settings_round_trip_and_normalize_language()
    {
        string path = Path.Combine(Path.GetTempPath(), "pomme-appearance-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            new AppearanceSettings { Theme = AppTheme.Light, Language = "EN" }.Save(path);
            AppearanceSettings loaded = AppearanceSettings.Load(path);

            Assert.Equal(AppTheme.Light, loaded.Theme);
            Assert.Equal("en", loaded.Language);
            Assert.Contains("\"Light\"", File.ReadAllText(path));

            Assert.Equal("fr", AppearanceSettings.NormalizeLanguage("de"));
            Assert.Equal(AppTheme.Dark, AppearanceSettings.Load(path + ".absent").Theme);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
