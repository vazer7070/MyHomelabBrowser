namespace MyHomelabBrowser.classes.Profiles.Credentials
{
    internal sealed class VaultSecurityState
    {
        public int FailedAttempts { get; set; }
        public DateTime? LockedUntilUtc { get; set; }
    }
}
