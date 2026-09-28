using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace MyHomelabBrowser.classes.Flash
{
    /// <summary>
    /// Réglages imposés aux profils Basilisk de PommeBrowser. user.js est relu par
    /// Basilisk à chaque démarrage et réécrit par PommeBrowser à chaque lancement :
    /// les profils existants reçoivent donc aussi les nouveaux réglages.
    /// </summary>
    public static class LegacyProfilePreferences
    {
        const string Header = "// Fichier géré par PommeBrowser : il est réécrit à chaque lancement de Basilisk.\n";

        /// <summary>Barres d'outils masquées : seule la page est intégrée dans l'onglet.</summary>
        public const string UserChrome =
            "/* Fichier géré par PommeBrowser. */\n" +
            "#navigator-toolbox, #TabsToolbar, #nav-bar, #toolbar-menubar, #PersonalToolbar,\n" +
            "#titlebar, #sidebar-box, #sidebar-splitter { visibility: collapse !important; }\n";

        public static IReadOnlyList<(string Name, object Value)> For(bool isPrivate)
        {
            var prefs = new List<(string, object)>
            {
                // Intégration dans l'onglet
                ("toolkit.legacyUserProfileCustomizations.stylesheets", true),
                ("browser.tabs.autoHide", true),
                ("browser.fullscreen.autohide", true),
                ("browser.tabs.warnOnClose", false),
                ("browser.tabs.warnOnCloseOtherTabs", false),
                ("browser.warnOnQuit", false),

                // Démarrage direct sur la page demandée, sans assistant ni restauration
                ("browser.shell.checkDefaultBrowser", false),
                ("browser.startup.page", 0),
                ("browser.startup.homepage_override.mstone", "ignore"),
                ("startup.homepage_welcome_url", ""),
                ("startup.homepage_welcome_url.additional", ""),
                ("browser.rights.3.shown", true),
                ("browser.sessionstore.resume_from_crash", false),
                ("browser.sessionstore.max_resumed_crashes", 0),
                ("toolkit.startup.max_resumed_crashes", -1),
                ("browser.disableResetPrompt", true),

                // Pas de mises à jour, de télémétrie ni de rapports de plantage
                ("app.update.enabled", false),
                ("app.update.auto", false),
                ("extensions.update.enabled", false),
                ("extensions.getAddons.cache.enabled", false),
                ("datareporting.healthreport.uploadEnabled", false),
                ("datareporting.policy.dataSubmissionEnabled", false),
                ("toolkit.telemetry.enabled", false),
                ("toolkit.telemetry.unified", false),
                ("browser.crashReports.unsubmittedCheck.enabled", false),

                // Chiffrement : TLS 1.2 minimum, contenu actif mixte bloqué
                ("security.tls.version.min", 3),
                ("security.mixed_content.block_active_content", true),
                ("security.cert_pinning.enforcement_level", 1),

                // Vie privée : ni WebRTC (fuite d'adresse IP), ni géolocalisation, ni notifications
                ("media.peerconnection.enabled", false),
                ("geo.enabled", false),
                ("dom.webnotifications.enabled", false),
                ("dom.push.enabled", false),
                ("beacon.enabled", false),
                ("browser.send_pings", false),
                ("network.prefetch-next", false),
                ("network.dns.disablePrefetch", true),
                ("network.predictor.enabled", false),
                ("network.http.speculative-parallel-limit", 0),

                // Plugins : Flash seulement (Java, Silverlight et les autres restent désactivés)
                ("plugin.state.flash", 2),
                ("plugin.default.state", 0),
                ("plugin.state.java", 0),
                ("plugin.state.npctrl", 0),
                ("plugin.scan.plid.all", false),

                // Fenêtres surgissantes bloquées, cache disque limité à 256 Mo
                ("dom.disable_open_during_load", true),
                ("browser.cache.disk.capacity", 262144)
            };

            if (isPrivate)
            {
                // Onglet privé : profil jetable, rien n'est conservé même avant sa suppression.
                prefs.Add(("browser.privatebrowsing.autostart", true));
                prefs.Add(("browser.cache.disk.enable", false));
                prefs.Add(("places.history.enabled", false));
            }

            return prefs;
        }

        public static string BuildUserJs(bool isPrivate)
        {
            var builder = new StringBuilder(Header);
            foreach ((string name, object value) in For(isPrivate))
                builder.Append("user_pref(\"").Append(name).Append("\", ").Append(Format(value)).Append(");\n");
            return builder.ToString();
        }

        /// <summary>Écrit user.js et userChrome.css dans le profil (création si besoin).</summary>
        public static void Apply(string profileDirectory, bool isPrivate)
        {
            Directory.CreateDirectory(Path.Combine(profileDirectory, "chrome"));
            AtomicFile.WriteAllText(Path.Combine(profileDirectory, "user.js"), BuildUserJs(isPrivate));
            AtomicFile.WriteAllText(Path.Combine(profileDirectory, "chrome", "userChrome.css"), UserChrome);
        }

        static string Format(object value) => value switch
        {
            bool b => b ? "true" : "false",
            int i => i.ToString(CultureInfo.InvariantCulture),
            string s => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"",
            _ => value.ToString() ?? "null"
        };
    }
}
