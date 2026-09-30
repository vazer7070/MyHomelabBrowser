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
    [InlineData("NPSWF32_34_0_0_323.dll", true, true)]
    [InlineData("NPSWF32.dll", true, true)]
    [InlineData("pepflashplayer64_32_0_0_371.dll", true, false)]
    [InlineData("FlashUtil32_34_0_0_323_Plugin.dll", true, false)]
    [InlineData("Flash.ocx", true, false)]
    [InlineData("libflashplayer.so", false, true)]
    [InlineData("libpepflashplayer.so", false, false)]
    [InlineData("NPSWF64_32_0_0_371.dll", false, false)]
    public void ModuleNamesAreThoseOfTheNpapiPlugin(string name, bool windows, bool expected)
        => Assert.Equal(expected, FlashModuleSearch.IsModuleName(name, windows));

    [Fact]
    public void ArchitectureIsReadFromTheFileNotItsName()
    {
        const FlashModuleSearch.ModuleArchitecture x64 = FlashModuleSearch.ModuleArchitecture.X64;
        const FlashModuleSearch.ModuleArchitecture x86 = FlashModuleSearch.ModuleArchitecture.X86;
        const FlashModuleSearch.ModuleArchitecture unknown = FlashModuleSearch.ModuleArchitecture.Unknown;

        Assert.Equal(x64, FlashModuleSearch.ArchitectureOf(Write("a.dll", PortableExecutable(0x8664)), windows: true));
        Assert.Equal(x86, FlashModuleSearch.ArchitectureOf(Write("b.dll", PortableExecutable(0x14C)), windows: true));
        // Le nom ne compte pas : un NPSWF32 64 bits, un NPSWF64 32 bits, l'ancien NPSWF32.dll.
        Assert.Equal(x64, FlashModuleSearch.ArchitectureOf(Write("NPSWF32_34_0_0_323.dll", PortableExecutable(0x8664)), windows: true));
        Assert.Equal(x86, FlashModuleSearch.ArchitectureOf(Write("NPSWF64_34_0_0_323.dll", PortableExecutable(0x14C)), windows: true));
        Assert.Equal(x86, FlashModuleSearch.ArchitectureOf(Write("NPSWF32.dll", PortableExecutable(0x14C)), windows: true));
        Assert.True(FlashModuleSearch.Is32Bit(Path.Combine(_folder, "NPSWF64_34_0_0_323.dll")));
        Assert.False(FlashModuleSearch.Is32Bit(Path.Combine(_folder, "NPSWF32_34_0_0_323.dll")));
        // ARM64 : aucun hôte ne peut le charger.
        Assert.Equal(unknown, FlashModuleSearch.ArchitectureOf(Write("c.dll", PortableExecutable(0xAA64)), windows: true));
        Assert.Equal(unknown, FlashModuleSearch.ArchitectureOf(Write("d.dll", "pas une bibliothèque"u8.ToArray()), windows: true));
        Assert.Equal(unknown, FlashModuleSearch.ArchitectureOf(Path.Combine(_folder, "absent.dll"), windows: true));

        // Linux : 64 bits seulement.
        Assert.Equal(x64, FlashModuleSearch.ArchitectureOf(Write("e.so", Elf(2, 0x3E)), windows: false));
        Assert.Equal(unknown, FlashModuleSearch.ArchitectureOf(Write("f.so", Elf(1, 0x03)), windows: false));
        Assert.Equal(unknown, FlashModuleSearch.ArchitectureOf(Write("g.so", Elf(2, 0xB7)), windows: false));
        Assert.Equal(unknown, FlashModuleSearch.ArchitectureOf(Path.Combine(_folder, "a.dll"), windows: false));

        Assert.True(FlashModuleSearch.IsModuleBinary(Path.Combine(_folder, "a.dll"), windows: true));
        Assert.True(FlashModuleSearch.IsModuleBinary(Path.Combine(_folder, "b.dll"), windows: true));
        Assert.False(FlashModuleSearch.IsModuleBinary(Path.Combine(_folder, "c.dll"), windows: true));
    }

    [Fact]
    public void VersionIsReadFromTheFileNameAndOldVersionsDoNotBlockContent()
    {
        Assert.Equal(new Version(32, 0, 0, 371), FlashModuleSearch.VersionOf("NPSWF64_32_0_0_371.dll"));
        Assert.Equal(new Version(34, 0, 0, 323), FlashModuleSearch.VersionOf("NPSWF32_34_0_0_323.dll"));
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
        Write(Path.Combine("Arm", "NPSWF64_2_0_0_0.dll"), PortableExecutable(0xAA64));

        // Les deux architectures sont proposées ; le module 32 bits est marqué comme tel.
        IReadOnlyList<FlashModuleSearch.Module> found = Find(depth: 4, windows: true);
        Assert.Equal(2, found.Count);
        FlashModuleSearch.Module sixtyFour = Assert.Single(found, m => !m.Is32Bit);
        Assert.Equal(module, sixtyFour.Path);
        Assert.Equal(new Version(32, 0, 0, 371), sixtyFour.Version);
        Assert.Single(found, m => m.Is32Bit && m.Path.EndsWith("NPSWF32_32_0_0_371.dll", StringComparison.Ordinal));

        // Trop profond pour la limite demandée.
        Assert.Empty(Find(depth: 3, windows: true));
    }

    [Fact]
    public void ArchitectureOfFoundModulesComesFromTheFile()
    {
        // Ancien nom sans version (NPSWF32.dll) et nom trompeur : l'en-tête décide.
        Write(Path.Combine("a", "NPSWF32.dll"), PortableExecutable(0x14C));
        Write(Path.Combine("b", "NPSWF64_32_0_0_371.dll"), PortableExecutable(0x14C));

        IReadOnlyList<FlashModuleSearch.Module> found = Find(depth: 2, windows: true);

        Assert.Equal(2, found.Count);
        Assert.All(found, m => Assert.True(m.Is32Bit));
    }

    [Fact]
    public void InstalledModulesAndTheirSubfoldersAreLeftOut()
    {
        // Données de PommeBrowser : plugins\ (64 bits) et plugins\x86\ (32 bits).
        Write(Path.Combine("plugins", "NPSWF64_32_0_0_371.dll"), PortableExecutable(0x8664));
        Write(Path.Combine("plugins", "x86", "NPSWF32_32_0_0_371.dll"), PortableExecutable(0x14C));
        string other = Write(Path.Combine("plugins-copie", "NPSWF32_32_0_0_344.dll"), PortableExecutable(0x14C));

        FlashModuleSearch.Module found = Assert.Single(Find(depth: 3, windows: true, exclude: Path.Combine(_folder, "plugins")));
        Assert.Equal(other, found.Path);
    }

    [Fact]
    public void BestModuleOfEachArchitectureAvoidsTheTimeBombThenTakesTheNewest()
    {
        var modules = new FlashModuleSearch.Module[]
        {
            new("32-465", new Version(32, 0, 0, 465), Is32Bit: true),
            new("32-344", new Version(32, 0, 0, 344), Is32Bit: true),
            new("32-371", new Version(32, 0, 0, 371), Is32Bit: true),
            new("64-installé", new Version(34, 0, 0, 323)),
            new("64-sans-version", null),
            new("64-même-version", new Version(34, 0, 0, 323)),
        };

        IReadOnlyList<FlashModuleSearch.Module> best = FlashModuleSearch.Best(modules);

        // 64 bits d'abord ; à égalité, le premier de la liste (le module déjà installé) reste.
        Assert.Equal(new[] { "64-installé", "32-371" }, best.Select(m => m.Path));
        Assert.Empty(FlashModuleSearch.Best(Array.Empty<FlashModuleSearch.Module>()));

        // Versions inconnues (Linux) : le module installé reste.
        FlashModuleSearch.Module kept = Assert.Single(FlashModuleSearch.Best(new FlashModuleSearch.Module[] { new("installé", null), new("trouvé", null) }));
        Assert.Equal("installé", kept.Path);
    }

    [Fact]
    public void IntegratedEngineTriesThirtyTwoBitsFirstUnlessOnlyItMayBlockContent()
    {
        FlashModuleSearch.Module sixtyFour371 = new("64", new Version(32, 0, 0, 371));
        FlashModuleSearch.Module sixtyFourChina = new("64", new Version(34, 0, 0, 323));
        FlashModuleSearch.Module thirtyTwoChina = new("32", new Version(34, 0, 0, 323), Is32Bit: true);
        FlashModuleSearch.Module thirtyTwo371 = new("32", new Version(32, 0, 0, 371), Is32Bit: true);

        Assert.Equal(new[] { thirtyTwoChina, sixtyFourChina }, FlashModuleSearch.IntegratedEngineOrder(new[] { sixtyFourChina, thirtyTwoChina }));
        Assert.Equal(new[] { thirtyTwo371, sixtyFour371 }, FlashModuleSearch.IntegratedEngineOrder(new[] { sixtyFour371, thirtyTwo371 }));
        // Le module 32 bits peut refuser les contenus (blocage de 2021), pas le 64 bits : ce dernier d'abord.
        Assert.Equal(new[] { sixtyFour371, thirtyTwoChina }, FlashModuleSearch.IntegratedEngineOrder(new[] { thirtyTwoChina, sixtyFour371 }));
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
