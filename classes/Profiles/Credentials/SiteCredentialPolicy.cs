namespace MyHomelabBrowser.classes.Profiles.Credentials
{
    public sealed class SiteCredentialPolicy
    {
        public string Origin { get; set; } = string.Empty;
        public CredentialSavePolicy SavePolicy { get; set; } = CredentialSavePolicy.Ask;
        public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    }
}
