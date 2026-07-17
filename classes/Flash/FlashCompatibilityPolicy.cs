using System;
using System.Collections.Generic;

namespace MyHomelabBrowser.classes.Flash
{
    /// <summary>
    /// Produit un diagnostic générique à partir du contenu détecté et de l'état réel
    /// du lecteur. Aucun domaine ni jeu n'est codé en dur.
    /// </summary>
    public static class FlashCompatibilityPolicy
    {
        public static string BuildRuffleFailureMessage(
            FlashDetectionResult detection,
            string technicalReason,
            RuffleStatus? status = null)
        {
            var details = new List<string>();

            if (status?.MetadataLoaded == true)
            {
                string actionScript = status.IsActionScript3 ? "ActionScript 3" : "ActionScript 1/2";
                details.Add($"Format détecté : {actionScript}, SWF v{status.SwfVersion}.");
            }

            if (detection.Hints.RequestsScriptAccess)
            {
                details.Add(detection.Hints.CanGrantScriptAccess
                    ? "Le contenu demande une communication avec la page web ; elle a été reproduite selon les paramètres d'origine."
                    : "Le contenu demande une communication inter-origines avec la page, qui ne peut pas être accordée automatiquement en sécurité.");
            }

            if (detection.Hints.IsCrossOrigin)
                details.Add("Le fichier SWF est chargé depuis une origine différente de la page.");

            if (detection.Hints.IsDynamicEmbed)
                details.Add("Le lecteur est créé dynamiquement par le site.");

            string detailBlock = details.Count == 0
                ? string.Empty
                : "\n\n" + string.Join("\n", details);

            return "Le moteur Flash intégré n'a pas réussi à exécuter ce contenu.\n\n" +
                   "PommeBrowser a reproduit les paramètres du lecteur d'origine et tenté " +
                   "le chargement avec Ruffle. Ce contenu peut utiliser une API Flash, un codec, " +
                   "une connexion réseau ou une interaction navigateur qui n'est pas encore prise en charge." +
                   detailBlock +
                   "\n\nDiagnostic : " + NormalizeReason(technicalReason);
        }

        private static string NormalizeReason(string? reason)
        {
            if (string.IsNullOrWhiteSpace(reason))
                return "échec sans détail fourni par le moteur.";

            string normalized = reason.Trim();
            return normalized.EndsWith('.') ? normalized : normalized + ".";
        }
    }
}
