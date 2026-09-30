namespace MyHomelabBrowser.classes.Profiles.Credentials
{
    /// <summary>Raison d'un déverrouillage refusé.</summary>
    public enum VaultUnlockFailure
    {
        None,

        /// <summary>Le chiffrement refuse ce mot de passe.</summary>
        WrongPassword,

        /// <summary>Trop d'essais : attente avant le suivant.</summary>
        Locked,

        /// <summary>Fichier du coffre abîmé ou d'un format inconnu, quel que soit le mot de passe.</summary>
        Unreadable
    }
}
