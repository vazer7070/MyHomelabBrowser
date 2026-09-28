using System;
using System.IO;
using System.Threading.Tasks;

namespace MyHomelabBrowser.classes.Flash
{
    /// <summary>
    /// Profil Basilisk réservé à un onglet. À la libération, l'emplacement redevient
    /// disponible ; un profil jetable (onglet privé) est supprimé.
    /// </summary>
    public sealed class LegacyProfileLease : IDisposable
    {
        private readonly Action? _release;
        private bool _disposed;

        internal LegacyProfileLease(string profilePath, bool isTemporary, Action? release)
        {
            ProfilePath = profilePath;
            IsTemporary = isTemporary;
            _release = release;
        }

        public string ProfilePath { get; }
        public bool IsTemporary { get; }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _release?.Invoke();

            if (IsTemporary)
                _ = Task.Run(() => DeleteDirectoryWithRetries(ProfilePath));
        }

        private static void DeleteDirectoryWithRetries(string path)
        {
            for (int attempt = 0; attempt < 8; attempt++)
            {
                try
                {
                    if (!Directory.Exists(path))
                        return;

                    Directory.Delete(path, recursive: true);
                    return;
                }
                catch
                {
                    System.Threading.Thread.Sleep(250 * (attempt + 1));
                }
            }
        }
    }
}
