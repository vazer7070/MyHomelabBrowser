using MyHomelabBrowser.classes;
using MyHomelabBrowser.classes.Profiles;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using static MyHomelabBrowser.classes.Localization.Loc;

public class ProfileService
{
    static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true
    };

    const int MaxAttempts = 5;
    const int BaseLockSeconds = 10;
    const int MaxLockSeconds = 15 * 60;
    public const int MaxUsernameLength = ProfileNameRules.MaxLength;

    readonly string _rootDir;
    readonly string _profilesIndexPath;
    readonly string _lastProfilePath;

    Dictionary<string, UserProfile> _profiles = new();

    public UserProfile? Current { get; private set; }
    public bool IsLocked { get; private set; }

    public event Action<UserProfile?>? ProfileChanged;

    // Levé avant tout changement de dossier de profil, pour terminer les écritures en attente.
    public event Action? ProfileChanging;

    // Ancien nom, nouveau nom : les données WebView2 du profil doivent suivre.
    public event Action<string, string>? ProfileRenamed;

    // Nom du profil supprimé : ses données WebView2 (cookies, cache) doivent être effacées.
    public event Action<string>? ProfileDeleted;

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

    // =====================
    // VALIDATION
    // =====================

    public static bool TryValidateUsername(string? username, out string error)
        => ProfileNameRules.TryValidate(username, out error);

    // =====================
    // PROFILS
    // =====================

    public IEnumerable<UserProfile> GetAllProfiles()
        => _profiles.Values.OrderBy(p => p.Username, StringComparer.CurrentCultureIgnoreCase);

    public bool ProfileExists(string username)
    {
        username = (username ?? "").Trim();
        if (username.Length == 0) return false;
        return _profiles.ContainsKey(username.ToLowerInvariant());
    }

    public UserProfile? FindProfile(string username)
    {
        username = (username ?? "").Trim();
        return username.Length > 0 && _profiles.TryGetValue(username.ToLowerInvariant(), out var profile)
            ? profile
            : null;
    }

    public void CreateProfile(string username, string password)
    {
        username = (username ?? string.Empty).Trim();
        if (!TryValidateUsername(username, out string error))
            throw new InvalidOperationException(error);

        var key = username.ToLowerInvariant();
        if (_profiles.ContainsKey(key))
            throw new InvalidOperationException(Tr("Ce profil existe déjà."));

        var (hash, salt) = PasswordHasher.Hash(password);

        var profile = new UserProfile
        {
            Username = username,
            PasswordHash = hash,
            Salt = salt,
            HashIterations = PasswordHasher.CurrentIterations
        };

        _profiles[key] = profile;
        Directory.CreateDirectory(GetProfileDir(username));

        SaveProfiles();
    }

    /// <summary>
    /// Vérifie le mot de passe d'un profil en appliquant la même protection contre
    /// les essais répétés que Login : sans cela, le menu « Changer de profil »
    /// permettait de tester des mots de passe sans limite.
    /// </summary>
    public bool VerifyPassword(UserProfile profile, string password)
    {
        if (profile == null)
            return false;

        if (IsLoginLocked(profile))
            return false;

        if (!PasswordHasher.Verify(password, profile.PasswordHash, profile.Salt, profile.HashIterations))
        {
            RegisterLoginFailure(profile);
            return false;
        }

        OnSuccessfulVerification(profile, password);
        return true;
    }

    public static bool IsLoginLocked(UserProfile profile)
        => profile.LoginLockUntilUtc.HasValue && profile.LoginLockUntilUtc > DateTime.UtcNow;

    public bool Login(string username, string password)
    {
        var profile = FindProfile(username);
        if (profile == null || !VerifyPassword(profile, password))
            return false;

        ProfileChanging?.Invoke();
        Current = profile;
        IsLocked = false;

        AppDataContext.UseProfile(profile.Username);
        SaveLastProfile();
        ProfileChanged?.Invoke(Current);

        return true;
    }

    public void LoginSilent(UserProfile profile)
    {
        ProfileChanging?.Invoke();
        Current = profile;
        IsLocked = false;
        AppDataContext.UseProfile(profile.Username);
        SaveLastProfile();
        ProfileChanged?.Invoke(profile);
    }

    public void Logout()
    {
        ProfileChanging?.Invoke();
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
        if (Current == null || !VerifyPassword(Current, password))
            return false;

        IsLocked = false;
        ProfileChanged?.Invoke(Current);
        return true;
    }

    public void DeleteCurrentProfile()
    {
        if (Current == null)
            return;

        ProfileChanging?.Invoke();

        var deletedName = Current.Username;
        var key = deletedName.ToLowerInvariant();
        var dir = GetProfileDir(deletedName);

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
        SaveLastProfile();
        ProfileDeleted?.Invoke(deletedName);
        ProfileChanged?.Invoke(null);
    }

    public void UpdateProfile(string newUsername, string? newPassword)
    {
        if (Current == null)
            throw new InvalidOperationException(Tr("Aucun profil connecté."));

        newUsername = (newUsername ?? string.Empty).Trim();

        var oldKey = Current.Username.ToLowerInvariant();
        var newKey = newUsername.ToLowerInvariant();

        // Un ancien profil au nom non conforme peut toujours changer de mot de passe ;
        // la validation ne s'applique qu'à un nouveau nom.
        if (newKey != oldKey && !TryValidateUsername(newUsername, out string error))
            throw new InvalidOperationException(error);

        if (newKey != oldKey)
        {
            if (_profiles.ContainsKey(newKey))
                throw new InvalidOperationException(Tr("Un profil avec ce nom existe déjà."));

            ProfileChanging?.Invoke();

            // Les données du profil (coffre, historique, favoris, paramètres) sont
            // rangées dans un dossier nommé d'après le profil : on le déplace,
            // sinon le profil renommé repartait de zéro.
            string oldUsername = Current.Username;
            MoveProfileDirectory(oldUsername, newUsername);

            _profiles.Remove(oldKey);
            Current.Username = newUsername;
            _profiles[newKey] = Current;

            ProfileRenamed?.Invoke(oldUsername, newUsername);
        }
        else
        {
            // Changement de casse uniquement : le dossier reste le même.
            Current.Username = newUsername;
        }

        if (!string.IsNullOrWhiteSpace(newPassword))
        {
            var (hash, salt) = PasswordHasher.Hash(newPassword);
            Current.PasswordHash = hash;
            Current.Salt = salt;
            Current.HashIterations = PasswordHasher.CurrentIterations;
        }

        SaveProfiles();
        AppDataContext.UseProfile(Current.Username);
        SaveLastProfile();
        ProfileChanged?.Invoke(Current);
    }

    void MoveProfileDirectory(string oldUsername, string newUsername)
    {
        string source = GetProfileDir(oldUsername);
        string destination = GetProfileDir(newUsername);

        if (!Directory.Exists(source))
            return;

        if (Directory.Exists(destination))
        {
            // Dossier résiduel d'un ancien profil supprimé : on ne fusionne jamais.
            if (Directory.EnumerateFileSystemEntries(destination).Any())
                throw new InvalidOperationException(
                    Tr("Un dossier de données existe déjà pour ce nom. Choisissez un autre nom."));

            Directory.Delete(destination);
        }

        Directory.Move(source, destination);
    }

    void RegisterLoginFailure(UserProfile profile)
    {
        profile.FailedLoginAttempts++;

        if (profile.FailedLoginAttempts >= MaxAttempts)
        {
            int exponent = Math.Min(profile.FailedLoginAttempts - MaxAttempts, 10);
            double seconds = Math.Min(MaxLockSeconds, BaseLockSeconds * Math.Pow(2, exponent));
            profile.LoginLockUntilUtc = DateTime.UtcNow.AddSeconds(seconds);
        }

        TrySaveProfiles();
    }

    void OnSuccessfulVerification(UserProfile profile, string password)
    {
        bool changed = profile.FailedLoginAttempts != 0 || profile.LoginLockUntilUtc != null;
        profile.FailedLoginAttempts = 0;
        profile.LoginLockUntilUtc = null;

        // Migration transparente vers le nombre d'itérations actuel.
        if (profile.HashIterations < PasswordHasher.CurrentIterations)
        {
            var (hash, salt) = PasswordHasher.Hash(password);
            profile.PasswordHash = hash;
            profile.Salt = salt;
            profile.HashIterations = PasswordHasher.CurrentIterations;
            changed = true;
        }

        if (changed)
            TrySaveProfiles();
    }

    // =====================
    // PERSISTENCE
    // =====================

    void LoadProfiles()
    {
        if (!File.Exists(_profilesIndexPath))
            return;

        try
        {
            var json = File.ReadAllText(_profilesIndexPath);
            var list = JsonSerializer.Deserialize<List<UserProfile>>(json);

            if (list == null)
                return;

            _profiles = list
                .Where(p => !string.IsNullOrWhiteSpace(p.Username))
                .GroupBy(p => p.Username.ToLowerInvariant())
                .ToDictionary(g => g.Key, g => g.First());
        }
        catch
        {
            // Un index illisible ne doit pas empêcher le navigateur de démarrer.
            // Il est mis de côté pour pouvoir être récupéré manuellement.
            try
            {
                string backup = _profilesIndexPath + ".invalid-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                File.Move(_profilesIndexPath, backup, overwrite: false);
            }
            catch { }

            _profiles = new Dictionary<string, UserProfile>();
        }
    }

    void SaveProfiles()
    {
        var list = _profiles.Values.ToList();
        var json = JsonSerializer.Serialize(list, JsonOpts);
        AtomicFile.WriteAllText(_profilesIndexPath, json);
    }

    void TrySaveProfiles()
    {
        try { SaveProfiles(); }
        catch { }
    }

    void LoadLastProfile()
    {
        try
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
        catch
        {
            // Démarrage sans profil si le fichier est illisible.
        }
    }

    void SaveLastProfile()
    {
        try { AtomicFile.WriteAllText(_lastProfilePath, Current?.Username ?? ""); }
        catch { }
    }

    public string GetProfileDir(string username)
    {
        return Path.Combine(_rootDir, username.Trim().ToLowerInvariant());
    }
}
