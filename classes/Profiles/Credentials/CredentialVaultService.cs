using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace MyHomelabBrowser.classes.Profiles.Credentials
{
    public sealed class CredentialVaultService
    {
        // Coffres créés avant Argon2id : déverrouillés avec PBKDF2, puis convertis (voir UpgradeKeyDerivation).
        private const int Pbkdf2Iterations = 200_000;
        private const int MaxVaultBytes = 16 * 1024 * 1024;

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        private readonly Func<string> _getVaultPath;
        private readonly List<CredentialEntry> _cache = new();
        private readonly List<SiteCredentialPolicy> _policies = new();

        private byte[]? _key;
        private byte[]? _salt;

        // Dérivation qui a produit _key : Argon2id (paramètres), ou null pour PBKDF2 (ancien format).
        private Argon2Parameters? _kdf;

        public bool IsUnlocked => _key is { Length: > 0 };

        /// <summary>
        /// Cause technique du dernier échec « coffre illisible » (type et message de l'erreur,
        /// jamais le mot de passe ni le contenu), pour le journal des erreurs.
        /// </summary>
        public string? LastUnlockError { get; private set; }
        public bool IsSessionUnlocked => IsUnlocked;
        public bool VaultExists => File.Exists(_getVaultPath()) || File.Exists(GetBackupPath());
        public DateTime? UnlockAvailableAtUtc => LoadSecurityState().LockedUntilUtc;

        public CredentialVaultService(Func<string> getVaultPath)
        {
            _getVaultPath = getVaultPath ?? throw new ArgumentNullException(nameof(getVaultPath));
        }

        public void ReloadForCurrentProfile()
        {
            Lock();
            DeleteLegacyPlaintextIndex();
        }

        public void Lock()
        {
            if (_key != null)
                CryptographicOperations.ZeroMemory(_key);
            if (_salt != null)
                CryptographicOperations.ZeroMemory(_salt);

            _key = null;
            _salt = null;
            _kdf = null;
            _cache.Clear();
            _policies.Clear();
        }

        public bool TryInitializeNewVault(string vaultPassword)
        {
            if (string.IsNullOrWhiteSpace(vaultPassword))
                return false;

            try
            {
                if (VaultExists)
                    return TryUnlock(vaultPassword);

                var path = _getVaultPath();
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);

                _salt = RandomNumberGenerator.GetBytes(16);
                _kdf = Argon2Parameters.Recommended;
                _key = DeriveKey(vaultPassword, _salt, _kdf);
                _cache.Clear();
                _policies.Clear();

                Save();
                ResetUnlockProtection();
                DeleteLegacyPlaintextIndex();
                return true;
            }
            catch
            {
                Lock();
                return false;
            }
        }

        public bool TryUnlock(string vaultPassword) => TryUnlock(vaultPassword, out _);

        /// <summary>
        /// Déverrouille le coffre ; en cas d'échec, <paramref name="failure"/> dit pourquoi. Seul un
        /// mot de passe refusé par le chiffrement compte comme un essai raté : un fichier abîmé ou
        /// d'un format inconnu est signalé comme tel (<see cref="LastUnlockError"/>), sans bloquer.
        /// </summary>
        public bool TryUnlock(string vaultPassword, out VaultUnlockFailure failure)
        {
            failure = VaultUnlockFailure.None;
            LastUnlockError = null;
            if (string.IsNullOrEmpty(vaultPassword))
            {
                failure = VaultUnlockFailure.WrongPassword;
                return false;
            }
            if (IsUnlockBlocked())
            {
                failure = VaultUnlockFailure.Locked;
                return false;
            }

            var primaryPath = _getVaultPath();
            var backupPath = GetBackupPath();

            // Si le principal est structurellement valide mais que son tag GCM refuse
            // le mot de passe, on ne tente jamais l'ancienne sauvegarde. Sinon, un ancien
            // mot de passe pourrait redevenir valable après une rotation du secret.
            if (File.Exists(primaryPath))
            {
                var primaryLoaded = TryLoadVault(
                    primaryPath,
                    vaultPassword,
                    out var primaryPayload,
                    out var primaryEnvelope,
                    out var primaryBytes,
                    out var primaryKey,
                    out var primaryNeedsMigration,
                    out var authenticationFailed,
                    out var primaryError);

                if (primaryLoaded)
                {
                    CompleteUnlock(
                        primaryPayload!,
                        primaryEnvelope!,
                        primaryBytes!,
                        primaryKey!,
                        fromBackup: false,
                        needsMigration: primaryNeedsMigration);
                    UpgradeKeyDerivation(vaultPassword);
                    return true;
                }

                if (authenticationFailed)
                {
                    RegisterUnlockFailure();
                    Lock();
                    failure = VaultUnlockFailure.WrongPassword;
                    return false;
                }
                LastUnlockError = Path.GetFileName(primaryPath) + " : " + primaryError;
            }

            // La sauvegarde n'est utilisée que si le principal manque ou est corrompu.
            bool backupAuthenticationFailed = false;
            string? backupError = null;
            if (File.Exists(backupPath)
                && TryLoadVault(
                    backupPath,
                    vaultPassword,
                    out var backupPayload,
                    out var backupEnvelope,
                    out var backupBytes,
                    out var backupKey,
                    out var backupNeedsMigration,
                    out backupAuthenticationFailed,
                    out backupError))
            {
                CompleteUnlock(
                    backupPayload!,
                    backupEnvelope!,
                    backupBytes!,
                    backupKey!,
                    fromBackup: true,
                    needsMigration: backupNeedsMigration);
                UpgradeKeyDerivation(vaultPassword);
                return true;
            }

            Lock();
            if (backupAuthenticationFailed)
            {
                RegisterUnlockFailure();
                failure = VaultUnlockFailure.WrongPassword;
                return false;
            }

            // Aucun fichier lisible : ce n'est pas le mot de passe qui est en cause.
            if (backupError != null)
                LastUnlockError = (LastUnlockError != null ? LastUnlockError + " ; " : string.Empty) + Path.GetFileName(backupPath) + " : " + backupError;
            failure = VaultUnlockFailure.Unreadable;
            return false;
        }

        /// <summary>
        /// Mot de passe perdu : le coffre (et sa sauvegarde) est mis de côté sous un autre nom,
        /// jamais effacé, puisqu'il reste déchiffrable si le mot de passe revient. Un nouveau
        /// coffre peut ensuite être créé. Renvoie le fichier mis de côté, ou null s'il n'y en avait pas.
        /// </summary>
        public string? ResetVault()
        {
            Lock();
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
            string? archived = null;
            foreach (string path in new[] { _getVaultPath(), GetBackupPath() })
            {
                if (!File.Exists(path))
                    continue;
                string target = path + ".reset-" + stamp;
                File.Move(path, target);
                archived ??= target;
            }
            ResetUnlockProtection();
            LastUnlockError = null;
            return archived;
        }

        private bool TryLoadVault(
            string path,
            string password,
            out VaultPayload? payload,
            out VaultEnvelope? envelope,
            out byte[]? bytes,
            out byte[]? derivedKey,
            out bool needsMigration,
            out bool authenticationFailed,
            out string? error)
        {
            error = null;
            payload = null;
            envelope = null;
            bytes = null;
            derivedKey = null;
            needsMigration = false;
            authenticationFailed = false;

            try
            {
                bytes = ReadVaultFile(path);
                envelope = VaultEnvelope.Deserialize(bytes);
                derivedKey = DeriveKey(password, envelope.Salt, envelope.Kdf);

                string decryptedJson;
                try
                {
                    decryptedJson = DecryptToString(
                        derivedKey,
                        envelope.Nonce,
                        envelope.Ciphertext,
                        envelope.AssociatedData());
                }
                catch (CryptographicException ex)
                {
                    authenticationFailed = true;
                    error = ex.GetType().Name;
                    CryptographicOperations.ZeroMemory(derivedKey);
                    derivedKey = null;
                    return false;
                }

                needsMigration = decryptedJson.TrimStart().StartsWith("[", StringComparison.Ordinal);
                payload = DeserializePayload(decryptedJson);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                if (derivedKey != null)
                {
                    CryptographicOperations.ZeroMemory(derivedKey);
                    derivedKey = null;
                }
                return false;
            }
        }

        private void CompleteUnlock(
            VaultPayload payload,
            VaultEnvelope envelope,
            byte[] sourceBytes,
            byte[] derivedKey,
            bool fromBackup,
            bool needsMigration)
        {
            var normalized = NormalizePayload(payload);

            Lock();
            _key = derivedKey;
            _salt = envelope.Salt.ToArray();
            _kdf = envelope.Kdf;
            _cache.AddRange(payload.Credentials);
            _policies.AddRange(payload.Policies);

            ResetUnlockProtection();
            DeleteLegacyPlaintextIndex();

            if (fromBackup)
                WriteAllBytesAtomic(_getVaultPath(), sourceBytes);

            if (normalized || needsMigration)
                Save();
        }

        /// <summary>
        /// Coffre d'avant Argon2id, tout juste déverrouillé : clé dérivée à nouveau avec Argon2id
        /// (nouveau sel, même mot de passe), coffre et sauvegarde réécrits. Sans rien demander à
        /// l'utilisateur. Si l'écriture échoue, le coffre reste ouvert avec l'ancienne clé, et la
        /// conversion sera retentée au prochain déverrouillage.
        /// </summary>
        private void UpgradeKeyDerivation(string password)
        {
            if (_kdf != null || !IsUnlocked)
                return;

            byte[]? oldKey = _key;
            byte[]? oldSalt = _salt;
            byte[] salt = RandomNumberGenerator.GetBytes(16);
            _kdf = Argon2Parameters.Recommended;
            _salt = salt;
            _key = DeriveKey(password, salt, _kdf);
            try
            {
                Save();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                CryptographicOperations.ZeroMemory(_key);
                _key = oldKey;
                _salt = oldSalt;
                _kdf = null;
                return;
            }

            if (oldKey != null)
                CryptographicOperations.ZeroMemory(oldKey);
            if (oldSalt != null)
                CryptographicOperations.ZeroMemory(oldSalt);

            // La sauvegarde garde sinon l'ancien format, plus facile à attaquer : remplacée, ou retirée.
            try
            {
                RefreshBackupFromPrimary();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                try
                {
                    File.Delete(GetBackupPath());
                }
                catch (Exception deleteError) when (deleteError is IOException or UnauthorizedAccessException)
                {
                }
            }
        }

        public bool TryChangeVaultPassword(string currentVaultPassword, string newVaultPassword)
        {
            if (string.IsNullOrWhiteSpace(newVaultPassword) || !TryUnlock(currentVaultPassword))
                return false;

            byte[]? newKey = null;
            byte[]? newSalt = null;

            try
            {
                newSalt = RandomNumberGenerator.GetBytes(16);
                newKey = DeriveKey(newVaultPassword, newSalt, Argon2Parameters.Recommended);

                if (_key != null)
                    CryptographicOperations.ZeroMemory(_key);
                if (_salt != null)
                    CryptographicOperations.ZeroMemory(_salt);

                _key = newKey;
                _salt = newSalt;
                _kdf = Argon2Parameters.Recommended;
                newKey = null;
                newSalt = null;

                Save();
                RefreshBackupFromPrimary();
                ResetUnlockProtection();
                return true;
            }
            catch
            {
                if (newKey != null)
                    CryptographicOperations.ZeroMemory(newKey);
                if (newSalt != null)
                    CryptographicOperations.ZeroMemory(newSalt);
                return false;
            }
        }

        public IReadOnlyList<CredentialEntry> GetAll()
        {
            EnsureUnlocked();
            return _cache.OrderByDescending(x => x.UpdatedAt).ToList();
        }

        public CredentialEntry? FindForOrigin(Uri? uri, string? username = null)
        {
            if (!IsUnlocked || !CredentialOrigin.TryCreateTrusted(uri, out var origin))
                return null;

            return FindForOrigin(origin, username);
        }

        public CredentialEntry? FindForOrigin(string origin, string? username = null)
        {
            if (!IsUnlocked)
                return null;

            var normalizedOrigin = CredentialOrigin.NormalizeStoredValue(origin);
            if (normalizedOrigin.Length == 0)
                return null;

            return _cache
                .Where(x => string.Equals(x.Host, normalizedOrigin, StringComparison.OrdinalIgnoreCase))
                .Where(x => string.IsNullOrWhiteSpace(username)
                            || string.Equals(x.Username, username, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(x => x.UpdatedAt)
                .FirstOrDefault();
        }

        // Compatibilité interne temporaire : l'appelant doit fournir une origine complète.
        public CredentialEntry? FindForHost(string origin) => FindForOrigin(origin);

        public bool HasCredentialForOrigin(Uri? uri)
        {
            return IsUnlocked && FindForOrigin(uri) != null;
        }

        public bool HasCredentialForHost(string origin)
        {
            return IsUnlocked && FindForOrigin(origin) != null;
        }

        public CredentialSavePolicy GetPolicy(string origin)
        {
            EnsureUnlocked();
            var normalizedOrigin = CredentialOrigin.NormalizeStoredValue(origin);
            if (normalizedOrigin.Length == 0)
                return CredentialSavePolicy.Ask;

            return _policies
                .FirstOrDefault(x => string.Equals(x.Origin, normalizedOrigin, StringComparison.OrdinalIgnoreCase))
                ?.SavePolicy ?? CredentialSavePolicy.Ask;
        }

        public void SetPolicy(string origin, CredentialSavePolicy savePolicy)
        {
            EnsureUnlocked();
            var normalizedOrigin = CredentialOrigin.NormalizeStoredValue(origin);
            if (normalizedOrigin.Length == 0)
                throw new ArgumentException(Tr("Origine invalide."), nameof(origin));

            var existing = _policies.FirstOrDefault(x =>
                string.Equals(x.Origin, normalizedOrigin, StringComparison.OrdinalIgnoreCase));

            if (savePolicy == CredentialSavePolicy.Ask)
            {
                if (existing != null)
                    _policies.Remove(existing);
            }
            else if (existing == null)
            {
                _policies.Add(new SiteCredentialPolicy
                {
                    Origin = normalizedOrigin,
                    SavePolicy = savePolicy,
                    UpdatedAtUtc = DateTime.UtcNow
                });
            }
            else
            {
                existing.SavePolicy = savePolicy;
                existing.UpdatedAtUtc = DateTime.UtcNow;
            }

            Save();
        }

        public void Upsert(
            string origin,
            string username,
            string password,
            string? formAction,
            bool alwaysSave = false,
            bool neverSave = false)
        {
            EnsureUnlocked();

            var normalizedOrigin = CredentialOrigin.NormalizeStoredValue(origin);
            if (normalizedOrigin.Length == 0)
                throw new ArgumentException(Tr("Origine invalide."), nameof(origin));
            if (string.IsNullOrEmpty(password))
                throw new ArgumentException(Tr("Mot de passe vide."), nameof(password));

            // Compatibilité avec les anciens appelants, sans jamais créer une fausse entrée secrète.
            if (neverSave)
            {
                SetPolicy(normalizedOrigin, CredentialSavePolicy.NeverSave);
                return;
            }

            var normalizedUser = username?.Trim() ?? string.Empty;
            var existing = _cache.FirstOrDefault(x =>
                string.Equals(x.Host, normalizedOrigin, StringComparison.OrdinalIgnoreCase)
                && string.Equals(x.Username, normalizedUser, StringComparison.OrdinalIgnoreCase));

            if (existing == null)
            {
                _cache.Add(new CredentialEntry
                {
                    Host = normalizedOrigin,
                    Username = normalizedUser,
                    Password = password,
                    FormAction = NormalizeFormAction(formAction, normalizedOrigin),
                    UpdatedAt = DateTime.UtcNow
                });
            }
            else
            {
                existing.Password = password;
                existing.FormAction = NormalizeFormAction(formAction, normalizedOrigin);
                existing.UpdatedAt = DateTime.UtcNow;
            }

            if (alwaysSave)
                SetPolicyInternal(normalizedOrigin, CredentialSavePolicy.AlwaysSave);

            Save();
        }

        /// <summary>
        /// Associe (ou retire, avec null) une clé TOTP à un identifiant enregistré.
        /// </summary>
        public void SetTotpSecret(string origin, string username, string? secret)
        {
            EnsureUnlocked();
            var normalizedOrigin = CredentialOrigin.NormalizeStoredValue(origin);
            var existing = _cache.FirstOrDefault(x =>
                string.Equals(x.Host, normalizedOrigin, StringComparison.OrdinalIgnoreCase)
                && string.Equals(x.Username, username, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException(Tr("Identifiant introuvable dans le coffre."));

            if (!string.IsNullOrWhiteSpace(secret) &&
                !MyHomelabBrowser.classes.Security.Totp.TryParse(secret, out _, out string error))
            {
                throw new ArgumentException(error, nameof(secret));
            }

            existing.TotpSecret = string.IsNullOrWhiteSpace(secret) ? null : secret.Trim();
            existing.UpdatedAt = DateTime.UtcNow;
            Save();
        }

        public void Delete(string origin, string username)
        {
            EnsureUnlocked();
            var normalizedOrigin = CredentialOrigin.NormalizeStoredValue(origin);
            if (normalizedOrigin.Length == 0)
                return;

            _cache.RemoveAll(x =>
                string.Equals(x.Host, normalizedOrigin, StringComparison.OrdinalIgnoreCase)
                && string.Equals(x.Username, username, StringComparison.OrdinalIgnoreCase));

            Save();
        }

        private void Save()
        {
            EnsureUnlocked();
            if (_salt is not { Length: > 0 })
                throw new InvalidOperationException(Tr("Sel du coffre manquant."));

            var payload = new VaultPayload
            {
                Credentials = _cache.ToList(),
                Policies = _policies.ToList()
            };

            var json = JsonSerializer.Serialize(payload, JsonOpts);
            var envelope = VaultEnvelope.CreateFromPlaintext(json, _key!, _salt, _kdf);
            WriteAllBytesAtomic(_getVaultPath(), envelope.Serialize());
        }

        private static VaultPayload DeserializePayload(string json)
        {
            if (json.TrimStart().StartsWith("[", StringComparison.Ordinal))
            {
                var legacyItems = JsonSerializer.Deserialize<List<LegacyCredentialEntry>>(json, JsonOpts)
                                  ?? new List<LegacyCredentialEntry>();
                var payload = new VaultPayload();

                foreach (var item in legacyItems)
                {
                    var origin = CredentialOrigin.NormalizeStoredValue(item.Host);
                    if (origin.Length == 0)
                        continue;

                    if (item.NeverSave)
                    {
                        payload.Policies.Add(new SiteCredentialPolicy
                        {
                            Origin = origin,
                            SavePolicy = CredentialSavePolicy.NeverSave,
                            UpdatedAtUtc = item.UpdatedAt
                        });
                        continue;
                    }

                    if (!string.IsNullOrEmpty(item.Password))
                    {
                        payload.Credentials.Add(new CredentialEntry
                        {
                            Host = origin,
                            Username = item.Username ?? string.Empty,
                            Password = item.Password,
                            FormAction = item.FormAction,
                            UpdatedAt = item.UpdatedAt
                        });
                    }

                    if (item.AlwaysSave)
                    {
                        payload.Policies.Add(new SiteCredentialPolicy
                        {
                            Origin = origin,
                            SavePolicy = CredentialSavePolicy.AlwaysSave,
                            UpdatedAtUtc = item.UpdatedAt
                        });
                    }
                }

                return payload;
            }

            return JsonSerializer.Deserialize<VaultPayload>(json, JsonOpts) ?? new VaultPayload();
        }

        private static bool NormalizePayload(VaultPayload payload)
        {
            var changed = false;

            for (var i = payload.Credentials.Count - 1; i >= 0; i--)
            {
                var item = payload.Credentials[i];
                var origin = CredentialOrigin.NormalizeStoredValue(item.Host);
                if (origin.Length == 0 || string.IsNullOrEmpty(item.Password))
                {
                    payload.Credentials.RemoveAt(i);
                    changed = true;
                    continue;
                }

                if (!string.Equals(item.Host, origin, StringComparison.Ordinal))
                {
                    item.Host = origin;
                    changed = true;
                }

                if (item.Username == null)
                {
                    item.Username = string.Empty;
                    changed = true;
                }
            }

            for (var i = payload.Policies.Count - 1; i >= 0; i--)
            {
                var policy = payload.Policies[i];
                var origin = CredentialOrigin.NormalizeStoredValue(policy.Origin);
                if (origin.Length == 0 || policy.SavePolicy == CredentialSavePolicy.Ask)
                {
                    payload.Policies.RemoveAt(i);
                    changed = true;
                    continue;
                }

                if (!string.Equals(policy.Origin, origin, StringComparison.Ordinal))
                {
                    policy.Origin = origin;
                    changed = true;
                }
            }

            var credentialCount = payload.Credentials.Count;
            payload.Credentials = payload.Credentials
                .GroupBy(x => (x.Host.ToLowerInvariant(), x.Username.ToLowerInvariant()))
                .Select(g => g.OrderByDescending(x => x.UpdatedAt).First())
                .ToList();
            changed |= credentialCount != payload.Credentials.Count;

            var policyCount = payload.Policies.Count;
            payload.Policies = payload.Policies
                .GroupBy(x => x.Origin, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderByDescending(x => x.UpdatedAtUtc).First())
                .ToList();
            changed |= policyCount != payload.Policies.Count;

            return changed;
        }

        private void SetPolicyInternal(string normalizedOrigin, CredentialSavePolicy savePolicy)
        {
            var existing = _policies.FirstOrDefault(x =>
                string.Equals(x.Origin, normalizedOrigin, StringComparison.OrdinalIgnoreCase));

            if (existing == null)
            {
                _policies.Add(new SiteCredentialPolicy
                {
                    Origin = normalizedOrigin,
                    SavePolicy = savePolicy,
                    UpdatedAtUtc = DateTime.UtcNow
                });
            }
            else
            {
                existing.SavePolicy = savePolicy;
                existing.UpdatedAtUtc = DateTime.UtcNow;
            }
        }

        private bool IsUnlockBlocked()
        {
            var state = LoadSecurityState();
            return state.LockedUntilUtc.HasValue && state.LockedUntilUtc.Value > DateTime.UtcNow;
        }

        private void RegisterUnlockFailure()
        {
            var state = LoadSecurityState();
            state.FailedAttempts++;

            if (state.FailedAttempts >= 3)
            {
                var exponent = Math.Min(state.FailedAttempts - 3, 9);
                var delaySeconds = Math.Min(300, 1 << exponent);
                state.LockedUntilUtc = DateTime.UtcNow.AddSeconds(delaySeconds);
            }

            SaveSecurityState(state);
        }

        private void ResetUnlockProtection()
        {
            var statePath = GetSecurityStatePath();
            try
            {
                if (File.Exists(statePath))
                    File.Delete(statePath);
            }
            catch
            {
                SaveSecurityState(new VaultSecurityState());
            }
        }

        private VaultSecurityState LoadSecurityState()
        {
            try
            {
                var path = GetSecurityStatePath();
                if (!File.Exists(path))
                    return new VaultSecurityState();

                return JsonSerializer.Deserialize<VaultSecurityState>(File.ReadAllText(path), JsonOpts)
                       ?? new VaultSecurityState();
            }
            catch
            {
                return new VaultSecurityState();
            }
        }

        private void SaveSecurityState(VaultSecurityState state)
        {
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(state, JsonOpts));
            WriteAllBytesAtomic(GetSecurityStatePath(), bytes, keepBackup: false);
        }

        private string GetSecurityStatePath()
        {
            var directory = Path.GetDirectoryName(_getVaultPath())!;
            return Path.Combine(directory, "vault-security.json");
        }

        private string GetBackupPath() => _getVaultPath() + ".bak";

        private void RefreshBackupFromPrimary()
        {
            var primaryPath = _getVaultPath();
            if (!File.Exists(primaryPath))
                return;

            WriteAllBytesAtomic(GetBackupPath(), ReadVaultFile(primaryPath), keepBackup: false);
        }

        private static byte[] ReadVaultFile(string path)
        {
            var info = new FileInfo(path);
            if (info.Length <= 0 || info.Length > MaxVaultBytes)
                throw new InvalidDataException(Tr("Taille de coffre invalide."));

            return File.ReadAllBytes(path);
        }

        private static void WriteAllBytesAtomic(string path, byte[] bytes, bool keepBackup = true)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tempPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
            var backupPath = keepBackup ? path + ".bak" : null;

            try
            {
                using (var stream = new FileStream(
                           tempPath,
                           FileMode.CreateNew,
                           FileAccess.Write,
                           FileShare.None,
                           64 * 1024,
                           FileOptions.WriteThrough))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(flushToDisk: true);
                }

                if (File.Exists(path))
                {
                    if (keepBackup)
                    {
                        File.Replace(tempPath, path, backupPath, ignoreMetadataErrors: true);
                    }
                    else
                    {
                        File.Move(tempPath, path, overwrite: true);
                    }
                }
                else
                {
                    File.Move(tempPath, path);
                }
            }
            finally
            {
                try
                {
                    if (File.Exists(tempPath))
                        File.Delete(tempPath);
                }
                catch { }
            }
        }

        private void DeleteLegacyPlaintextIndex()
        {
            var vaultPath = _getVaultPath();
            var candidates = new[]
            {
                Path.ChangeExtension(vaultPath, ".index.json"),
                vaultPath + ".index.json"
            };

            foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    if (File.Exists(candidate))
                        File.Delete(candidate);
                }
                catch { }
            }
        }

        private static string? NormalizeFormAction(string? formAction, string origin)
        {
            if (string.IsNullOrWhiteSpace(formAction))
                return null;

            if (!Uri.TryCreate(origin, UriKind.Absolute, out var originUri))
                return null;

            if (!Uri.TryCreate(originUri, formAction, out var actionUri))
                return null;

            return actionUri.GetLeftPart(UriPartial.Path);
        }

        /// <summary>Clé AES-256 du coffre : Argon2id (<paramref name="kdf"/>), ou PBKDF2 pour l'ancien format.</summary>
        private static byte[] DeriveKey(string password, byte[] salt, Argon2Parameters? kdf)
        {
            if (kdf == null)
            {
                return Rfc2898DeriveBytes.Pbkdf2(
                    password,
                    salt,
                    Pbkdf2Iterations,
                    HashAlgorithmName.SHA256,
                    32);
            }

            byte[] secret = Encoding.UTF8.GetBytes(password);
            try
            {
                return VaultKeyDerivation.Argon2id(secret, salt, kdf.MemoryKiB, kdf.Iterations, kdf.Parallelism, 32);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(secret);
            }
        }

        private static string DecryptToString(byte[] key, byte[] nonce, byte[] ciphertext, byte[]? associatedData)
        {
            if (ciphertext.Length < 17)
                throw new InvalidDataException(Tr("Contenu chiffré invalide."));

            var tag = ciphertext.AsSpan(0, 16);
            var data = ciphertext.AsSpan(16);
            var plain = new byte[data.Length];

            try
            {
                using var aes = new AesGcm(key, 16);
                aes.Decrypt(nonce, data, tag, plain, associatedData);
                return Encoding.UTF8.GetString(plain);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plain);
            }
        }

        private static byte[] EncryptFromString(byte[] key, byte[] plaintextUtf8, byte[] nonce, byte[]? associatedData)
        {
            var tag = new byte[16];
            var cipher = new byte[plaintextUtf8.Length];

            using var aes = new AesGcm(key, 16);
            aes.Encrypt(nonce, plaintextUtf8, cipher, tag, associatedData);

            var result = new byte[tag.Length + cipher.Length];
            Buffer.BlockCopy(tag, 0, result, 0, tag.Length);
            Buffer.BlockCopy(cipher, 0, result, tag.Length, cipher.Length);
            CryptographicOperations.ZeroMemory(tag);
            return result;
        }

        private void EnsureUnlocked()
        {
            if (!IsUnlocked)
                throw new InvalidOperationException(Tr("Coffre verrouillé."));
        }

        private sealed class LegacyCredentialEntry
        {
            public string Host { get; set; } = string.Empty;
            public string? Username { get; set; }
            public string Password { get; set; } = string.Empty;
            public string? FormAction { get; set; }
            public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
            public bool AlwaysSave { get; set; }
            public bool NeverSave { get; set; }
        }

        /// <summary>Paramètres d'Argon2id enregistrés avec le coffre (bornés à la lecture).</summary>
        private sealed record Argon2Parameters(int MemoryKiB, int Iterations, int Parallelism)
        {
            /// <summary>Deuxième choix recommandé par la RFC 9106 : 64 Mio, 3 passes, 4 voies.</summary>
            public static readonly Argon2Parameters Recommended = new(64 * 1024, 3, 4);

            // Un fichier modifié ne doit pas pouvoir réclamer des Go de mémoire ou des minutes de calcul.
            public bool IsAcceptable =>
                MemoryKiB is >= 8 * 1024 and <= 1024 * 1024 &&
                Iterations is >= 1 and <= 20 &&
                Parallelism is >= 1 and <= 16;
        }

        /// <summary>
        /// Fichier du coffre. Format Argon2id : « PVK2 », paramètres d'Argon2id (mémoire en Kio,
        /// passes, voies), puis sel, nonce et contenu chiffré (AES-256-GCM, étiquette en tête), le
        /// début du fichier étant authentifié avec le contenu. Ancien format (PBKDF2) : sel, nonce
        /// et contenu seulement. Chaque bloc est précédé de sa longueur (Int32).
        /// </summary>
        private sealed class VaultEnvelope
        {
            private static readonly byte[] Magic = "PVK2"u8.ToArray();

            public Argon2Parameters? Kdf { get; init; }
            public byte[] Salt { get; init; } = Array.Empty<byte>();
            public byte[] Nonce { get; init; } = Array.Empty<byte>();
            public byte[] Ciphertext { get; init; } = Array.Empty<byte>();

            /// <summary>Début du fichier (format, paramètres, sel, nonce), authentifié avec le contenu ; rien pour l'ancien format.</summary>
            public byte[]? AssociatedData() => Kdf == null ? null : Header(Kdf, Salt, Nonce);

            public byte[] Serialize()
            {
                using var stream = new MemoryStream();
                using var writer = new BinaryWriter(stream);

                if (Kdf != null)
                    writer.Write(Header(Kdf, Salt, Nonce));
                else
                    WriteSaltAndNonce(writer, Salt, Nonce);
                writer.Write(Ciphertext.Length);
                writer.Write(Ciphertext);
                writer.Flush();
                return stream.ToArray();
            }

            public static VaultEnvelope Deserialize(byte[] bytes)
            {
                using var stream = new MemoryStream(bytes, writable: false);
                using var reader = new BinaryReader(stream);

                Argon2Parameters? kdf = null;
                if (bytes.AsSpan().StartsWith(Magic))
                {
                    reader.ReadBytes(Magic.Length);
                    kdf = new Argon2Parameters(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32());
                    if (!kdf.IsAcceptable)
                        throw new InvalidDataException(Tr("Paramètres de chiffrement du coffre invalides."));
                }

                var salt = ReadBounded(reader, min: 16, max: 64);
                var nonce = ReadBounded(reader, min: 12, max: 32);
                var ciphertext = ReadBounded(reader, min: 17, max: MaxVaultBytes);

                if (stream.Position != stream.Length)
                    throw new InvalidDataException(Tr("Données supplémentaires inattendues."));

                return new VaultEnvelope
                {
                    Kdf = kdf,
                    Salt = salt,
                    Nonce = nonce,
                    Ciphertext = ciphertext
                };
            }

            public static VaultEnvelope CreateFromPlaintext(string json, byte[] key, byte[] salt, Argon2Parameters? kdf)
            {
                var plain = Encoding.UTF8.GetBytes(json);
                try
                {
                    byte[] nonce = RandomNumberGenerator.GetBytes(12);
                    var ciphertext = EncryptFromString(key, plain, nonce, kdf == null ? null : Header(kdf, salt, nonce));
                    return new VaultEnvelope
                    {
                        Kdf = kdf,
                        Salt = salt.ToArray(),
                        Nonce = nonce,
                        Ciphertext = ciphertext
                    };
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(plain);
                }
            }

            private static byte[] Header(Argon2Parameters kdf, byte[] salt, byte[] nonce)
            {
                using var stream = new MemoryStream();
                using var writer = new BinaryWriter(stream);
                writer.Write(Magic);
                writer.Write(kdf.MemoryKiB);
                writer.Write(kdf.Iterations);
                writer.Write(kdf.Parallelism);
                WriteSaltAndNonce(writer, salt, nonce);
                writer.Flush();
                return stream.ToArray();
            }

            private static void WriteSaltAndNonce(BinaryWriter writer, byte[] salt, byte[] nonce)
            {
                writer.Write(salt.Length);
                writer.Write(salt);
                writer.Write(nonce.Length);
                writer.Write(nonce);
            }

            private static byte[] ReadBounded(BinaryReader reader, int min, int max)
            {
                var length = reader.ReadInt32();
                if (length < min || length > max)
                    throw new InvalidDataException(Tr("Longueur invalide dans le coffre."));

                var bytes = reader.ReadBytes(length);
                if (bytes.Length != length)
                    throw new EndOfStreamException();

                return bytes;
            }
        }
    }
}
