using System;

namespace MyHomelabBrowser.classes.Profiles
{
    public class UserProfile
    {
        public string Username { get; set; } = "";
        public byte[] PasswordHash { get; set; } = [];
        public byte[] Salt { get; set; } = [];
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public byte[]? VaultPasswordHash { get; set; }
        public byte[]? VaultSalt { get; set; }

        public int FailedLoginAttempts { get; set; }
        public DateTime? LoginLockUntilUtc { get; set; }

        public int FailedVaultAttempts { get; set; }
        public DateTime? VaultLockUntilUtc { get; set; }

    }
}
