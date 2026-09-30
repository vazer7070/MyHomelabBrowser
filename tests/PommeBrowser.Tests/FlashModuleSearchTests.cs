using PommeBrowser.Legacy;

namespace PommeBrowser.Tests;

/// <summary>Recherche du module Flash Player de l'utilisateur pour Pomme Legacy (Windows et Linux).</summary>
public sealed class FlashModuleSearchTests : IDisposable
{
    readonly string _folder = Path.Combine(Path.GetTempPath(), "pomme-flash-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { }
    }

    /// <summary>Début d'une DLL PE pour la machine donnée (0x8664 : x64, 0x14C : x86).</summary>
    static byte[] PortableExecutable(ushort machine)
    {
        var bytes = new byte[256];
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        BitConverter.GetBytes(128).CopyTo(bytes, 0x3C);
        "PE\0\0"u8.ToArray().CopyTo(bytes, 128);
        BitConverter.GetBytes(machine).CopyTo(bytes, 132);
        return bytes;
    }

    /// <summary>Début d'une bibliothèque ELF (classe 2 : 64 bits, machine 0x3E : x86-64).</summary>
    static byte[] Elf(byte elfClass, ushort machine)
    {
        var bytes = new byte[128];
        new byte[] { 0x7F, (byte)'E', (byte)'L', (byte)'F', elfClass, 1 }.CopyTo(bytes, 0);
        BitConverter.GetBytes(machine).CopyTo(bytes, 18);
        return bytes;
    }

    string Write(string relative, byte[] content)
    {
        string path = Path.Combine(_folder, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
        return path;
    }

    IReadOnlyList<FlashModuleSearch.Module> Find(int depth, bool windows, string? exclude = null)
        => FlashModuleSearch.Find(new[] { new FlashModuleSearch.Location(_folder, depth) }, windows, exclude, CancellationToken.None);

    [Theory]
    [InlineData("NPSWF64_32_0_0_371.dll", true, true)]
    [InlineData("npswf64_32_0_0_371.DLL", true, true)]
    [InlineData("NPSWF32_32_0_0_371.dll", true, false)]
    [InlineData("pepflashplayer64_32_0_0_371.dll", true, false)]
    [InlineData("libflashplayer.so", false, true)]
    [InlineData("libpepflashplayer.so", false, false)]
    [InlineData("NPSWF64_32_0_0_371.dll", false, false)]
    public void ModuleNamesAreThoseOfTheNpapiPlugin(string name, bool windows, bool expected)
        => Assert.Equal(expected, FlashModuleSearch.IsModuleName(name, windows));

    [Fact]
    public void OnlySixtyFourBitLibrariesAreModules()
    {
        Assert.True(FlashModuleSearch.IsModuleBinary(Write("a.dll", PortableExecutable(0x8664)), windows: true));
        Assert.False(FlashModuleSearch.IsModuleBinary(Write("b.dll", PortableExecutable(0x14C)), windows: true));
        Assert.True(FlashModuleSearch.IsModuleBinary(Write("c.so", Elf(2, 0x3E)), windows: false));
        Assert.False(FlashModuleSearch.IsModuleBinary(Write("d.so", Elf(1, 0x03)), windows: false));
        Assert.False(FlashModuleSearch.IsModuleBinary(Write("e.so", Elf(2, 0xB7)), windows: false));
        Assert.False(FlashModuleSearch.IsModuleBinary(Write("f.dll", "pas une bibliothèque"u8.ToArray()), windows: true));
        Assert.False(FlashModuleSearch.IsModuleBinary(Path.Combine(_folder, "absent.dll"), windows: true));
    }

    [Fact]
    public void VersionIsReadFromTheFileNameAndOldVersionsDoNotBlockContent()
    {
        Assert.Equal(new Version(32, 0, 0, 371), FlashModuleSearch.VersionOf("NPSWF64_32_0_0_371.dll"));
        Assert.Null(FlashModuleSearch.VersionOf("libflashplayer.so"));

        Assert.False(FlashModuleSearch.MayBlockContent(new("x", new Version(32, 0, 0, 371))));
        Assert.False(FlashModuleSearch.MayBlockContent(new("x", new Version(29, 0, 0, 171))));
        Assert.False(FlashModuleSearch.MayBlockContent(new("x", null)));
        Assert.True(FlashModuleSearch.MayBlockContent(new("x", new Version(32, 0, 0, 465))));
    }

    [Fact]
    public void PortableBrowserModuleIsFoundInSubfolders()
    {
        // Basilisk portable : BasiliskPortable\App\Basilisk\plugins\NPSWF64_….dll
        string module = Write(Path.Combine("BasiliskPortable", "App", "Basilisk", "plugins", "NPSWF64_32_0_0_371.dll"), PortableExecutable(0x8664));
        Write(Path.Combine("BasiliskPortable", "App", "Basilisk", "plugins", "NPSWF32_32_0_0_371.dll"), PortableExecutable(0x14C));
        Write(Path.Combine("Faux", "NPSWF64_1_0_0_0.dll"), "texte"u8.ToArray());

        FlashModuleSearch.Module found = Assert.Single(Find(depth: 4, windows: true));
        Assert.Equal(module, found.Path);
        Assert.Equal(new Version(32, 0, 0, 371), found.Version);

        // Trop profond pour la limite demandée.
        Assert.Empty(Find(depth: 3, windows: true));
    }

    [Fact]
    public void HiddenInstalledAndDuplicateModulesAreLeftOut()
    {
        byte[] library = Elf(2, 0x3E);
        Write(Path.Combine(".cache", "libflashplayer.so"), library);
        Write(Path.Combine("node_modules", "x", "libflashplayer.so"), library);
        Write(Path.Combine("installé", "libflashplayer.so"), Elf(2, 0x3E).Concat(new byte[] { 1 }).ToArray());
        string first = Write(Path.Combine("a", "libflashplayer.so"), library);
        Write(Path.Combine("b", "c", "libflashplayer.so"), library);

        FlashModuleSearch.Module found = Assert.Single(Find(depth: 5, windows: false, exclude: Path.Combine(_folder, "installé")));
        Assert.Equal(first, found.Path);
    }

    [Fact]
    public void VersionsWithoutTheTimeBombComeFirst()
    {
        Write(Path.Combine("a", "NPSWF64_32_0_0_465.dll"), PortableExecutable(0x8664));
        Write(Path.Combine("b", "c", "NPSWF64_32_0_0_371.dll"), PortableExecutable(0x8664));

        IReadOnlyList<FlashModuleSearch.Module> found = Find(depth: 3, windows: true);

        Assert.Equal(new[] { new Version(32, 0, 0, 371), new Version(32, 0, 0, 465) }, found.Select(m => m.Version));
    }

    [Fact]
    public void MissingFoldersAndCancellationEndTheSearchQuietly()
    {
        Write(Path.Combine("a", "libflashplayer.so"), Elf(2, 0x3E));
        var locations = new[] { new FlashModuleSearch.Location(Path.Combine(_folder, "absent"), 3), new FlashModuleSearch.Location(string.Empty, 3) };
        Assert.Empty(FlashModuleSearch.Find(locations, windows: false, exclude: null, CancellationToken.None));

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Empty(FlashModuleSearch.Find(new[] { new FlashModuleSearch.Location(_folder, 3) }, windows: false, exclude: null, cancelled.Token));
    }
}
