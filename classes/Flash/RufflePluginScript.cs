namespace MyHomelabBrowser.classes.Flash
{
    /// <summary>
    /// Lecteur Flash annoncé aux pages (navigator.plugins, navigator.mimeTypes), comme le fait
    /// Ruffle une fois chargé : beaucoup de sites (SWFObject, détection d'Adobe) vérifient la
    /// présence de Flash avant d'ajouter leur contenu et affichent sinon « installez Flash
    /// Player ». À exécuter dans le monde de la page, avant ses scripts, dans tous les cadres.
    /// Le fichier annoncé, « ruffle.js », est celui qu'attend Ruffle pour remplacer ensuite les
    /// contenus Flash (sinon il croit Flash Player installé et ne fait rien). Commun aux trois éditions.
    /// </summary>
    public static class RufflePluginScript
    {
        public const string Source = """
            (() => {
              const nav = window.navigator;
              if (!nav.plugins || !nav.mimeTypes || nav.plugins.namedItem('Shockwave Flash')) return;
              const TYPES = [
                ['application/futuresplash', 'spl'],
                ['application/x-shockwave-flash', 'swf'],
                ['application/x-shockwave-flash2-preview', 'swf'],
                ['application/vnd.adobe.flash.movie', 'swf']
              ];
              const set = (target, name, value, enumerable) =>
                Object.defineProperty(target, name, { value, enumerable, configurable: true });
              const tagged = (tag) => Object.create({ get [Symbol.toStringTag]() { return tag; } });
              // Liste à la manière de PluginArray, MimeTypeArray et Plugin : index, noms, item(), namedItem().
              const list = (items, key, tag) => {
                const proto = {
                  item(index) { return items[index >>> 0] ?? null; },
                  namedItem(name) { return items.find((item) => item[key] === String(name)) ?? null; },
                  get length() { return items.length; },
                  [Symbol.iterator]() { return items[Symbol.iterator](); },
                  get [Symbol.toStringTag]() { return tag; }
                };
                if (tag === 'PluginArray') proto.refresh = () => { };
                const result = Object.create(proto);
                items.forEach((item, index) => {
                  set(result, index, item, true);
                  if (!(item[key] in result)) set(result, item[key], item, false);
                });
                return result;
              };
              const mimes = TYPES.map(([type, suffixes]) => {
                const mime = tagged('MimeType');
                set(mime, 'type', type, true);
                set(mime, 'suffixes', suffixes, true);
                set(mime, 'description', 'Shockwave Flash', true);
                return mime;
              });
              const plugin = list(mimes, 'type', 'Plugin');
              set(plugin, 'name', 'Shockwave Flash', true);
              set(plugin, 'description', 'Shockwave Flash 32.0 r0', true);
              set(plugin, 'filename', 'ruffle.js', true);
              for (const mime of mimes) set(mime, 'enabledPlugin', plugin, true);
              const plugins = list([...Array.from(nav.plugins), plugin], 'name', 'PluginArray');
              const mimeTypes = list([...Array.from(nav.mimeTypes), ...mimes], 'type', 'MimeTypeArray');
              Object.defineProperty(nav, 'plugins', { get: () => plugins, enumerable: true, configurable: true });
              Object.defineProperty(nav, 'mimeTypes', { get: () => mimeTypes, enumerable: true, configurable: true });
            })();
            """;
    }
}
