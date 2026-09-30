using Konscious.Security.Cryptography;

namespace MyHomelabBrowser.classes.Profiles.Credentials
{
    /// <summary>
    /// Argon2id (RFC 9106), avec Konscious.Security.Cryptography : .NET n'en fournit pas. Vérifié
    /// par les tests avec le vecteur de la RFC.
    /// </summary>
    internal static class VaultKeyDerivation
    {
        public static byte[] Argon2id(
            byte[] password,
            byte[] salt,
            int memoryKiB,
            int iterations,
            int parallelism,
            int length,
            byte[]? secret = null,
            byte[]? associatedData = null)
        {
            using var argon = new Argon2id(password)
            {
                Salt = salt,
                MemorySize = memoryKiB,
                Iterations = iterations,
                DegreeOfParallelism = parallelism,
                KnownSecret = secret,
                AssociatedData = associatedData
            };
            return argon.GetBytes(length);
        }
    }
}
