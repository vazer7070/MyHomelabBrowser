using System;

namespace MyHomelabBrowser.classes.Profiles.Credentials
{
    public sealed class CredentialEntry
    {
        // Le nom Host est conservé pour rester compatible
        // avec les anciens coffres.
        //
        // La valeur contient maintenant l’origine exacte :
        // https://exemple.com
        // https://exemple.com:8443
        public string Host { get; set; } = string.Empty;

        public string Username { get; set; } = string.Empty;

        public string Password { get; set; } = string.Empty;

        public string? FormAction { get; set; }

        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        // Nom propre affiché dans la fenêtre du coffre.
        // Exemple :
        // https://accounts.exemple.com -> accounts.exemple.com
        public string DisplayHost =>
            CredentialOrigin.DisplayName(Host);
    }
}