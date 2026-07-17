namespace MyHomelabBrowser.classes.Profiles.Credentials
{
    public sealed class CredentialCandidate
    {
        public required string Origin { get; init; }
        public string Username { get; init; } = string.Empty;
        public required string Password { get; init; }
        public string? FormAction { get; init; }
    }
}
