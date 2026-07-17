using System;
using System.IO;
using System.Threading.Tasks;

namespace MyHomelabBrowser.classes.Flash
{
    public sealed class LegacyProfileLease : IDisposable
    {
        private bool _disposed;

        internal LegacyProfileLease(string profilePath, bool isTemporary)
        {
            ProfilePath = profilePath;
            IsTemporary = isTemporary;
        }

        public string ProfilePath { get; }
        public bool IsTemporary { get; }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            if (!IsTemporary)
                return;

            _ = Task.Run(() => DeleteDirectoryWithRetries(ProfilePath));
            GC.SuppressFinalize(this);
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
