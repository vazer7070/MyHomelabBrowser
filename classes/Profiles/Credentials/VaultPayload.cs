namespace MyHomelabBrowser.classes.Profiles.Credentials
{
    internal sealed class VaultPayload
    {
        public List<CredentialEntry> Credentials { get; set; } = new();
        public List<SiteCredentialPolicy> Policies { get; set; } = new();
    }
}
