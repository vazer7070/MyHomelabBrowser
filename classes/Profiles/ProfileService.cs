using MyHomelabBrowser.classes.Profiles;
using MyHomelabBrowser.classes.Profiles.Credentials;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;

public class ProfileService
{
    static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true
    };

    readonly string _rootDir;
    readonly string _profilesIndexPath;
    readonly string _lastProfilePath;

    Dictionary<string, UserProfile> _profiles = new();

    public UserProfile? Current { get; private set; }
    public bool IsLocked { get; private set; }

    public event Action<UserProfile?>? ProfileChanged;

    public ProfileService(string globalAppDataRoot)
    {
        var profilesRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MyHomelabBrowser"
        );

        _rootDir = Path.Combine(profilesRoot, "profiles");
        _profilesIndexPath = Path.Combine(profilesRoot, "profiles.json");
        _lastProfilePath = Path.Combine(profilesRoot, "last_profile.txt");

        Directory.CreateDirectory(_rootDir);
        LoadProfiles();
        LoadLastProfile();
    }
    const int MaxAttempts = 5;
    static readonly TimeSpan BaseLockDuration = TimeSpan.FromSeconds(10);

    void RegisterLoginFailure(UserProfile profile)
    {
        profile.FailedLoginAttempts++;

        if (profile.FailedLoginAttempts >= MaxAttempts)
        {
            var backoff = TimeSpan.FromSeconds(
                BaseLockDuration.TotalSeconds * Math.Pow(2, profile.FailedLoginAttempts - MaxAttempts)
            );

            profile.LoginLockUntilUtc = DateTime.UtcNow.Add(backoff);
        }

        SaveProfiles();
    }

    void ResetLoginProtection(UserProfile profile)
    {
        profile.FailedLoginAttempts = 0;
        profile.LoginLockUntilUtc = null;
    }


    // =====================
    // PROFILS
    // =====================

    public IEnumerable<UserProfile> GetAllProfiles()
        => _profiles.Values.OrderBy(p => p.Username);

    public bool ProfileExists(string username)
    {
        username = (username ?? "").Trim();
        if (username.Length == 0) return false;
        return _profiles.ContainsKey(username.ToLowerInvariant());
    }


    public void CreateProfile(string username, string password)
    {
        username = username.Trim();
        if (username.Length == 0)
            throw new Exception("Nom de profil invalide.");

        var key = username.ToLowerInvariant();
        if (_profiles.ContainsKey(key))
            throw new Exception("Ce profil existe déjà.");

        var (hash, salt) = PasswordHasher.Hash(password);

        var profile = new UserProfile
        {
            Username = username,
            PasswordHash = hash,
            Salt = salt
        };

        _profiles[key] = profile;
        Directory.CreateDirectory(GetProfileDir(username));

        SaveProfiles();
    }

    public bool VerifyPassword(UserProfile profile, string password)
    {
        
        return PasswordHasher.Verify(
            password,
            profile.PasswordHash,
            profile.Salt
        );
    }
    public bool Login(string username, string password)
    {
        username = (username ?? "").Trim();
        if (username.Length == 0)
            return false;

        var key = username.ToLowerInvariant();

        if (!_profiles.TryGetValue(key, out var profile))
            return false;

        // sécurité hash invalide
        if (profile.PasswordHash == null || profile.PasswordHash.Length == 0 ||
            profile.Salt == null || profile.Salt.Length == 0)
            return false;

        // 🔒 Vérifier lock temporaire
        if (profile.LoginLockUntilUtc.HasValue &&
            profile.LoginLockUntilUtc > DateTime.UtcNow)
        {
            return false;
        }

        // 🔐 Vérification mot de passe
        if (!PasswordHasher.Verify(password, profile.PasswordHash, profile.Salt))
        {
            // 🔴 Échec → incrément tentative
            profile.FailedLoginAttempts++;

            const int MaxAttempts = 5;
            const int BaseDelaySeconds = 10;

            if (profile.FailedLoginAttempts >= MaxAttempts)
            {
                var exponent = profile.FailedLoginAttempts - MaxAttempts;
                var delay = TimeSpan.FromSeconds(
                    BaseDelaySeconds * Math.Pow(2, exponent)
                );

                profile.LoginLockUntilUtc = DateTime.UtcNow.Add(delay);
            }

            SaveProfiles();
            return false;
        }

        // 🟢 Succès → reset protection
        profile.FailedLoginAttempts = 0;
        profile.LoginLockUntilUtc = null;

        Current = profile;
        IsLocked = false;

        AppDataContext.UseProfile(profile.Username);
        SaveLastProfile();
        ProfileChanged?.Invoke(Current);

        return true;
    }

    public void LoginSilent(UserProfile profile)
    {
        Current = profile;
        AppDataContext.UseProfile(profile.Username);
        SaveLastProfile();             
        ProfileChanged?.Invoke(profile);
    }


    public void Logout()
    {
        Current = null;
        IsLocked = false;

        AppDataContext.UseGlobal();
        SaveLastProfile();
        ProfileChanged?.Invoke(null);
    }


    public void Lock()
    {
        if (Current == null)
            return;

        IsLocked = true;
        ProfileChanged?.Invoke(Current);
    }

    public bool Unlock(string password)
    {
        if (Current == null)
            return false;

        if (Current.PasswordHash == null || Current.PasswordHash.Length == 0 ||
            Current.Salt == null || Current.Salt.Length == 0)
            return false;

        if (!PasswordHasher.Verify(password, Current.PasswordHash, Current.Salt))
            return false;

        IsLocked = false;
        ProfileChanged?.Invoke(Current);
        return true;
    }


    public void DeleteCurrentProfile()
    {
        if (Current == null)
            return;

        var key = Current.Username.ToLowerInvariant();
        var dir = GetProfileDir(Current.Username);

        _profiles.Remove(key);

        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
        catch { }

        Current = null;
        IsLocked = false;

        SaveProfiles();
        AppDataContext.UseGlobal();
        ProfileChanged?.Invoke(null);
    }

    public void UpdateProfile(string newUsername, string? newPassword)
    {
        if (Current == null)
            throw new Exception("Aucun profil connecté.");

        newUsername = newUsername.Trim();
        if (newUsername.Length == 0)
            throw new Exception("Nom de profil invalide.");

        var oldKey = Current.Username.ToLowerInvariant();
        var newKey = newUsername.ToLowerInvariant();

        if (newKey != oldKey)
        {
            if (_profiles.ContainsKey(newKey))
                throw new Exception("Un profil avec ce nom existe déjà.");

            _profiles.Remove(oldKey);
            Current.Username = newUsername;
            _profiles[newKey] = Current;
        }

        if (!string.IsNullOrWhiteSpace(newPassword))
        {
            var (hash, salt) = PasswordHasher.Hash(newPassword);
            Current.PasswordHash = hash;
            Current.Salt = salt;
        }

        SaveProfiles();
        AppDataContext.UseProfile(Current.Username);
        ProfileChanged?.Invoke(Current);
    }





    // =====================
    // PERSISTENCE
    // =====================

    void LoadProfiles()
    {
        if (!File.Exists(_profilesIndexPath))
            return;

        var json = File.ReadAllText(_profilesIndexPath);
        var list = JsonSerializer.Deserialize<List<UserProfile>>(json);

        if (list == null)
            return;

        _profiles = list.ToDictionary(
            p => p.Username.ToLowerInvariant(),
            p => p
        );
    }

    void SaveProfiles()
    {
        var list = _profiles.Values.ToList();
        var json = JsonSerializer.Serialize(list, JsonOpts);
        File.WriteAllText(_profilesIndexPath, json);
    }

    void LoadLastProfile()
    {
        if (!File.Exists(_lastProfilePath))
            return;

        var username = File.ReadAllText(_lastProfilePath).Trim();
        if (string.IsNullOrEmpty(username))
            return;

        if (_profiles.TryGetValue(username.ToLowerInvariant(), out var profile))
        {
            Current = profile;
            IsLocked = false;
            AppDataContext.UseProfile(profile.Username);
            ProfileChanged?.Invoke(Current);
        }
    }


    void SaveLastProfile()
    {
        File.WriteAllText(_lastProfilePath, Current?.Username ?? "");
    }

    public string GetProfileDir(string username)
    {
        return Path.Combine(_rootDir, username.ToLowerInvariant());
    }
}
