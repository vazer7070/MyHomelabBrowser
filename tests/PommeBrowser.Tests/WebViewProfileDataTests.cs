using MyHomelabBrowser.classes.Profiles;

namespace PommeBrowser.Tests;

// WebViewProfileData utilise un état statique : ces tests ne tournent pas en parallèle.
[Collection("WebViewProfileData")]
public sealed class WebViewProfileDataTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pomme-tests-" + Guid.NewGuid().ToString("N"));

    public WebViewProfileDataTests()
    {
        Directory.CreateDirectory(_root);
        WebViewProfileData.RootOverride = _root;
    }

    public void Dispose()
    {
        WebViewProfileData.RootOverride = null;
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string CreateProfileData(string id)
    {
        string folder = Path.Combine(_root, id, "WebView2");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "Cookies"), id);
        return folder;
    }

    [Fact]
    public void Free_folder_is_moved_immediately()
    {
        CreateProfileData("alice");

        WebViewProfileData.MoveOrSchedule("Alice", "Bob", folderInUse: false);

        Assert.False(Directory.Exists(Path.Combine(_root, "alice")));
        Assert.Equal("alice", File.ReadAllText(Path.Combine(WebViewProfileData.GetUserDataFolder("bob"), "Cookies")));
        Assert.Empty(WebViewProfileData.GetPendingOperations());
    }

    [Fact]
    public void Locked_folder_is_moved_at_next_start()
    {
        CreateProfileData("alice");

        WebViewProfileData.MoveOrSchedule("alice", "bob", folderInUse: true);
        Assert.True(Directory.Exists(Path.Combine(_root, "alice")));

        WebViewProfileData.ApplyPendingOperations();

        Assert.True(File.Exists(Path.Combine(_root, "bob", "WebView2", "Cookies")));
        Assert.Empty(WebViewProfileData.GetPendingOperations());
    }

    [Fact]
    public void Successive_renames_are_chained()
    {
        CreateProfileData("alice");

        WebViewProfileData.MoveOrSchedule("alice", "bob", folderInUse: true);
        WebViewProfileData.MoveOrSchedule("bob", "carol", folderInUse: true);
        WebViewProfileData.ApplyPendingOperations();

        Assert.True(File.Exists(Path.Combine(_root, "carol", "WebView2", "Cookies")));
        Assert.False(Directory.Exists(Path.Combine(_root, "bob")));
    }

    [Fact]
    public void Deleting_a_renamed_profile_deletes_the_original_data()
    {
        CreateProfileData("alice");

        WebViewProfileData.MoveOrSchedule("alice", "bob", folderInUse: true);
        WebViewProfileData.DeleteOrSchedule("bob", folderInUse: true);
        WebViewProfileData.ApplyPendingOperations();

        Assert.False(Directory.Exists(Path.Combine(_root, "alice")));
        Assert.False(Directory.Exists(Path.Combine(_root, "bob")));
        Assert.Empty(WebViewProfileData.GetPendingOperations());
    }

    [Fact]
    public void Existing_data_at_the_destination_is_never_overwritten()
    {
        CreateProfileData("alice");
        CreateProfileData("bob");

        WebViewProfileData.MoveOrSchedule("alice", "bob", folderInUse: false);

        Assert.Equal("bob", File.ReadAllText(Path.Combine(_root, "bob", "WebView2", "Cookies")));
        Assert.True(Directory.Exists(Path.Combine(_root, "alice")));
    }
}
