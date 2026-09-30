using System;
using System.Collections.Generic;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace PommeBrowser.Linux.Core
{
    /// <summary>
    /// Rapport « Signaler un problème » : mêmes catégories, mêmes clés et même format
    /// d'identifiant que l'édition Windows (le serveur de support les traite de la même façon).
    /// </summary>
    public static class SupportReport
    {
        public const int MaxDescriptionLength = 3500;

        /// <summary>Clé envoyée au serveur, libellé affiché.</summary>
        public static IReadOnlyList<(string Key, string Label)> Categories => new[]
        {
            ("bug", Tr("Bug ou dysfonctionnement")),
            ("missing_feature", Tr("Fonctionnalité absente")),
            ("feature_request", Tr("Demande d’ajout")),
            ("ui_ux", Tr("Interface ou ergonomie")),
            ("performance", "Performance"),
            ("other", Tr("Autre"))
        };

        public static IReadOnlyList<(string Key, string Label)> Modules => new[]
        {
            ("browser", "PommeBrowser"),
            ("adblock", Tr("Bloqueur de publicités"))
        };

        /// <summary>PB-aaaammjj-XXXX.</summary>
        public static string NewId(DateTime utcNow)
            => $"PB-{utcNow:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..4].ToUpperInvariant()}";

        /// <summary>Adresse sans identifiants, paramètres ni ancre : rien de personnel ne part avec le rapport.</summary>
        public static string SanitizeUrl(string value)
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri))
                return Limit(value, 300);

            var builder = new UriBuilder(uri)
            {
                UserName = string.Empty,
                Password = string.Empty,
                Query = string.Empty,
                Fragment = string.Empty
            };
            return Limit(builder.Uri.ToString(), 300);
        }

        public static string Limit(string? value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value))
                return Tr("inconnu");

            string trimmed = value.Trim();
            return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength] + "…";
        }

        /// <summary>Message d'erreur du formulaire, ou null s'il peut être envoyé.</summary>
        public static string? Validate(string title, string description)
        {
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(description))
                return Tr("Merci de remplir le titre et la description avant l’envoi.");
            if (description.Trim().Length > MaxDescriptionLength)
                return Tr("La description est trop longue pour être envoyée.");
            return null;
        }
    }
}
