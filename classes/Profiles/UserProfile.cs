using System;

namespace MyHomelabBrowser.classes.Profiles
{
    public class UserProfile
    {
        public string Username { get; set; } = "";
        public byte[] PasswordHash { get; set; } = [];
        public byte[] Salt { get; set; } = [];

        // 0 = profil créé avant l'enregistrement du nombre d'itérations (100 000).
        public int HashIterations { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public int FailedLoginAttempts { get; set; }
        public DateTime? LoginLockUntilUtc { get; set; }
    }
}
