using System;
using System.Collections.Generic;

namespace MyHomelabBrowser.classes.Profiles
{
    /// <summary>
    /// Règles de nommage des profils. Le nom sert de nom de dossier : il doit rester
    /// un segment de chemin simple, sinon un profil nommé ".." pointerait vers le
    /// dossier de l'application et sa suppression l'effacerait entièrement.
    /// </summary>
    public static class ProfileNameRules
    {
        public const int MaxLength = 32;

        private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
            "default"
        };

        public static bool TryValidate(string? username, out string error)
        {
            error = string.Empty;
            string value = (username ?? string.Empty).Trim();

            if (value.Length == 0)
            {
                error = "Le nom du profil est obligatoire.";
                return false;
            }

            if (value.Length > MaxLength)
            {
                error = $"Le nom du profil ne doit pas dépasser {MaxLength} caractères.";
                return false;
            }

            foreach (char c in value)
            {
                if (!(char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' or '.'))
                {
                    error = "Le nom du profil ne peut contenir que des lettres, des chiffres, des espaces et - _ .";
                    return false;
                }
            }

            if (value.StartsWith('.') || value.EndsWith('.'))
            {
                error = "Le nom du profil ne peut pas commencer ni finir par un point.";
                return false;
            }

            if (ReservedNames.Contains(value))
            {
                error = "Ce nom est réservé. Choisissez-en un autre.";
                return false;
            }

            return true;
        }
    }
}
