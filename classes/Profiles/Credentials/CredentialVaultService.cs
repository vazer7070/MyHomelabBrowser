using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MyHomelabBrowser.classes.Profiles.Credentials
{
    public class CredentialVaultService
    {
        static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        readonly Func<string> _getVaultPath; // dépend du profil (AppDataContext.Root)
        readonly List<CredentialEntry> _cache = new();
        public bool IsSessionUnlocked { get; private set; }



        byte[]? _key; // clé AES en mémoire uniquement (après unlock)
        public bool IsUnlocked => _key != null;

        public CredentialVaultService(Func<string> getVaultPath)
        {
            _getVaultPath = getVaultPath;
        }

        public void Lock()
        {
            _key = null;
            _cache.Clear();
            IsSessionUnlocked = false;
        }


        public bool TryUnlock(string profilePassword)
        {
            try
            {
                var path = _getVaultPath();

                // ===============================
                // 1️⃣ Vault inexistant → créer un vault vide MAIS COHÉRENT
                // ===============================
                if (!File.Exists(path))
                {
                    var salt = RandomNumberGenerator.GetBytes(16);

                    // 🔑 clé dérivée AVEC le salt
                    _key = DeriveKey(profilePassword, salt, out _);

                    var emptyList = new List<CredentialEntry>();
                    var json = JsonSerializer.Serialize(emptyList, JsonOpts);

                    var env = VaultEnvelope.CreateFromPlaintext(
                        json,
                        _key,
                        salt
                    );

                    File.WriteAllBytes(path, env.Serialize());

                    _cache.Clear();
                    IsSessionUnlocked = true;
                    return true;
                }

                // ===============================
                // 2️⃣ Vault existant → unlock normal
                // ===============================
                var bytes = File.ReadAllBytes(path);
                var envExisting = VaultEnvelope.Deserialize(bytes);

                // 🔑 clé dérivée AVEC le salt stocké
                _key = DeriveKey(profilePassword, envExisting.Salt, out _);

                var decryptedJson = DecryptToString(
                    _key,
                    envExisting.Nonce,
                    envExisting.Ciphertext
                );

                var list = JsonSerializer.Deserialize<List<CredentialEntry>>(decryptedJson, JsonOpts)
                           ?? new List<CredentialEntry>();

                _cache.Clear();
                _cache.AddRange(list);

                IsSessionUnlocked = true;
                return true;
            }
            catch
            {
                _key = null;
                _cache.Clear();
                IsSessionUnlocked = false;
                return false;
            }
        }





        public IReadOnlyList<CredentialEntry> GetAll()
            => _cache.OrderByDescending(x => x.UpdatedAt).ToList();

        public CredentialEntry? FindForHost(string host)
        {
            host = NormalizeHost(host);
            return _cache.FirstOrDefault(x => NormalizeHost(x.Host) == host);
        }

        public void Upsert(string host, string username, string password, string? formAction, bool alwaysSave = false, bool neverSave = false)
        {
            if (_key == null)
                throw new InvalidOperationException("Vault verrouillé.");

            host = NormalizeHost(host);

            var existing = _cache.FirstOrDefault(x => NormalizeHost(x.Host) == host && string.Equals(x.Username, username, StringComparison.OrdinalIgnoreCase));
            if (existing == null)
            {
                existing = new CredentialEntry
                {
                    Host = host,
                    Username = username,
                    Password = password,
                    FormAction = formAction,
                    UpdatedAt = DateTime.UtcNow,
                    AlwaysSave = alwaysSave,
                    NeverSave = neverSave

                };
                _cache.Add(existing);
            }
            else
            {
                existing.Password = password;
                existing.FormAction = formAction;
                existing.UpdatedAt = DateTime.UtcNow;
                existing.AlwaysSave = alwaysSave;
                existing.NeverSave = neverSave;
            }

            Save();
        }

        public void Delete(string host, string username)
        {
            if (_key == null)
                throw new InvalidOperationException("Vault verrouillé.");

            host = NormalizeHost(host);

            _cache.RemoveAll(x =>
                NormalizeHost(x.Host) == host &&
                string.Equals(x.Username, username, StringComparison.OrdinalIgnoreCase));

            Save();
        }

        void Save()
        {
            if (_key == null)
                throw new InvalidOperationException("Vault verrouillé.");

            var json = JsonSerializer.Serialize(_cache, JsonOpts);

            // Si c'est un nouveau vault : créer un salt persistant dans l'enveloppe
            var path = _getVaultPath();
            VaultEnvelope env;

            if (File.Exists(path))
            {
                // garder le salt existant
                var old = VaultEnvelope.Deserialize(File.ReadAllBytes(path));
                env = VaultEnvelope.CreateFromPlaintext(json, _key, old.Salt);
            }
            else
            {
                env = VaultEnvelope.CreateFromPlaintext(json, _key, salt: null);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, env.Serialize());
        }

        static string NormalizeHost(string host)
        {
            host = (host ?? "").Trim().ToLowerInvariant();
            if (host.StartsWith("www.")) host = host[4..];
            return host;
        }

        static byte[] DeriveKey(string password, byte[]? salt, out byte[] usedSalt)
        {
            usedSalt = salt ?? RandomNumberGenerator.GetBytes(16);

            using var pbkdf2 = new Rfc2898DeriveBytes(
                password,
                usedSalt,
                200_000,
                HashAlgorithmName.SHA256);

            return pbkdf2.GetBytes(32); // AES-256
        }

        static string DecryptToString(byte[] key, byte[] nonce, byte[] ciphertext)
        {
            // ciphertext = TAG(16) + DATA
            var tag = ciphertext[..16];
            var data = ciphertext[16..];

            var plain = new byte[data.Length];
            using var aes = new AesGcm(key);
            aes.Decrypt(nonce, data, tag, plain);

            return Encoding.UTF8.GetString(plain);
        }

        static byte[] EncryptFromString(byte[] key, byte[] plaintextUtf8, out byte[] nonce)
        {
            nonce = RandomNumberGenerator.GetBytes(12);

            var tag = new byte[16];
            var cipher = new byte[plaintextUtf8.Length];

            using var aes = new AesGcm(key);
            aes.Encrypt(nonce, plaintextUtf8, cipher, tag);

            // store TAG + DATA
            return tag.Concat(cipher).ToArray();
        }

        sealed class VaultEnvelope
        {
            public byte[] Salt { get; set; } = Array.Empty<byte>();
            public byte[] Nonce { get; set; } = Array.Empty<byte>();
            public byte[] Ciphertext { get; set; } = Array.Empty<byte>();

            public byte[] Serialize()
            {
                // format binaire minimal : [saltLen][salt][nonceLen][nonce][ctLen][ct]
                using var ms = new MemoryStream();
                using var bw = new BinaryWriter(ms);

                bw.Write(Salt.Length); bw.Write(Salt);
                bw.Write(Nonce.Length); bw.Write(Nonce);
                bw.Write(Ciphertext.Length); bw.Write(Ciphertext);

                return ms.ToArray();
            }

            public static VaultEnvelope Deserialize(byte[] bytes)
            {
                using var ms = new MemoryStream(bytes);
                using var br = new BinaryReader(ms);

                var saltLen = br.ReadInt32();
                var salt = br.ReadBytes(saltLen);

                var nonceLen = br.ReadInt32();
                var nonce = br.ReadBytes(nonceLen);

                var ctLen = br.ReadInt32();
                var ct = br.ReadBytes(ctLen);

                return new VaultEnvelope { Salt = salt, Nonce = nonce, Ciphertext = ct };
            }

            public static VaultEnvelope CreateFromPlaintext(string json, byte[] key, byte[]? salt)
            {
                var usedKey = key;

                byte[] usedSalt;
                if (salt == null || salt.Length == 0)
                {
                    usedSalt = RandomNumberGenerator.GetBytes(16);
                }
                else usedSalt = salt;

                // IMPORTANT : le key fourni doit déjà être dérivé avec usedSalt côté caller
                // Ici on ne redérive pas, on chiffre juste.
                var plain = Encoding.UTF8.GetBytes(json);
                var ct = EncryptFromString(usedKey, plain, out var nonce);

                return new VaultEnvelope
                {
                    Salt = usedSalt,
                    Nonce = nonce,
                    Ciphertext = ct
                };
            }
        }
    }
}
