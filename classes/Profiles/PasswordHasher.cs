using System.Security.Cryptography;

namespace MyHomelabBrowser.classes.Profiles
{
    static class PasswordHasher
    {
        const int SaltSize = 16;
        const int HashSize = 32;

        // Valeur des profils créés avant la version 0.9.8, conservée pour les vérifier.
        public const int LegacyIterations = 100_000;

        // Recommandation OWASP actuelle pour PBKDF2-HMAC-SHA256.
        public const int CurrentIterations = 600_000;

        public static (byte[] hash, byte[] salt) Hash(string password)
        {
            byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);
            byte[] hash = Derive(password, salt, CurrentIterations);
            return (hash, salt);
        }

        public static bool Verify(string password, byte[] hash, byte[] salt, int iterations = LegacyIterations)
        {
            if (hash is not { Length: HashSize } || salt is not { Length: > 0 })
                return false;

            byte[] computed = Derive(password, salt, iterations > 0 ? iterations : LegacyIterations);
            try
            {
                return CryptographicOperations.FixedTimeEquals(computed, hash);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(computed);
            }
        }

        static byte[] Derive(string password, byte[] salt, int iterations)
            => Rfc2898DeriveBytes.Pbkdf2(
                password ?? string.Empty,
                salt,
                iterations,
                HashAlgorithmName.SHA256,
                HashSize);
    }
}
