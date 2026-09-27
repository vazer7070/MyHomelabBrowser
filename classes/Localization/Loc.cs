using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace MyHomelabBrowser.classes.Localization
{
    /// <summary>
    /// Traduction de l'interface. Le français est la langue source : les textes du code
    /// passent par <see cref="Tr(string)"/> avec leur libellé français, qui sert de clé
    /// dans la table de traduction (lang/en.json). Un texte absent de la table reste en français.
    /// </summary>
    public static class Loc
    {
        private static IReadOnlyDictionary<string, string> _table = new Dictionary<string, string>();

        public static string Language { get; private set; } = "fr";

        public static bool IsTranslating => _table.Count > 0;

        public static CultureInfo Culture => Language == "en" ? CultureInfo.GetCultureInfo("en-US") : CultureInfo.GetCultureInfo("fr-FR");

        public static void Initialize(string language, IReadOnlyDictionary<string, string>? table)
        {
            Language = language == "en" ? "en" : "fr";
            _table = Language == "fr" || table == null
                ? new Dictionary<string, string>()
                : new Dictionary<string, string>(table, StringComparer.Ordinal);
        }

        public static IReadOnlyDictionary<string, string> LoadTable(Stream json)
            => JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();

        /// <summary>
        /// Texte traduit (ou le texte français si aucune traduction n'existe).
        /// </summary>
        public static string Tr(string french)
            => french != null && _table.TryGetValue(french, out string? translated) ? translated : french!;

        /// <summary>
        /// Texte traduit puis formaté : Tr("{0} onglets ouverts", count).
        /// </summary>
        public static string Tr(string frenchFormat, params object?[] args)
            => string.Format(Culture, Tr(frenchFormat), args);

        /// <summary>
        /// Recherche sans repli, pour les textes posés dans le XAML.
        /// </summary>
        public static bool TryTranslate(string? french, out string translated)
        {
            if (french != null && _table.TryGetValue(french, out string? value))
            {
                translated = value;
                return true;
            }

            translated = french ?? string.Empty;
            return false;
        }
    }
}
