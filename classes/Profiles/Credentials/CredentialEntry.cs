namespace MyHomelabBrowser.classes.Profiles.Credentials
{
    public sealed class CredentialEntry
    {
        // Conservé sous le nom Host pour migrer sans casser les anciens coffres.
        // La valeur contient désormais l'origine exacte : schéma + hôte + port.
        public string Host { get; set; } = string.Empty;
        public string Username { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        public string? FormAction { get; set; }
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        public string DisplayHost => CredentialOrigin.DisplayName(Host);
    }
}
