using System;

namespace MyHomelabBrowser.classes.Profiles
{
    public class UserProfile
    {
        public string Username { get; set; } = "";
        public byte[] PasswordHash { get; set; } = [];
        public byte[] Salt { get; set; } = [];
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public int FailedLoginAttempts { get; set; }
        public DateTime? LoginLockUntilUtc { get; set; }

    }
}
