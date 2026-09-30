# Moteur Flash intégré (PommeFlashHost)

PommeBrowser lit les contenus Flash avec **Ruffle**. Pour ceux que Ruffle ne sait pas lire, le
moteur intégré utilise **le module Flash Player de l'utilisateur** (`NPSWF64_*.dll`), comme le
faisait Basilisk, mais sans Basilisk : PommeBrowser joue lui-même le rôle du navigateur auprès du
module, par l'interface NPAPI.

Adobe interdit de redistribuer Flash Player : PommeBrowser ne le fournit jamais. Chacun ajoute
sa copie dans **Paramètres › Avancé** (voir `avalonia/Legacy/LegacyEngine.cs`).

## Principe

```
PommeBrowser ──(ligne de commande, stdin/stdout JSON)──► PommeFlashHost.exe ──(NPAPI)──► NPSWF64_*.dll
      ▲                                                         │
      └──────────── fenêtre logée dans l'onglet ◄───────────────┘
```

- **Hors processus.** Le module tourne dans `PommeFlashHost.exe` : un plantage de Flash
  n'emporte que ce processus. L'hôte s'arrête de lui-même quand PommeBrowser se ferme (fin
  de l'entrée standard).
- **Côté navigateur de NPAPI** (`PommeFlash.Host/Native/BrowserFunctions.cs`) : les 58
  fonctions `NPN_*` de la version 0.29 de l'interface (celle de Firefox 52, donc de Basilisk).
- **Réseau** (`PluginStream.cs`) : les adresses demandées par le module (`NPN_GetURLNotify`,
  `NPN_PostURLNotify`) sont résolues par rapport à la page, téléchargées sur un autre fil puis
  remises au module sur son fil, au rythme qu'il accepte (`NPP_WriteReady` / `NPP_Write`).
  Les cibles (`_blank`, `_self`…) et les adresses `javascript:` remontent à PommeBrowser.
- **Page** (`PageObjects.cs`) : `window`, `location`, `document`, `navigator` et l'élément,
  en lecture seule. Flash y lit l'adresse de la page pour ses règles de sécurité
  (`top.location + "__flashplugin_unique__"`) : l'hôte répond lui-même.
- **Fenêtre** (`HostWindow.cs`) : mode fenêtré (`wmode=window`), le module dessine dans sa
  fenêtre, que PommeBrowser loge dans l'onglet comme il le faisait pour Basilisk.

## Échanges avec PommeBrowser

Lancement :

```
PommeFlashHost.exe --plugin <NPSWF64_*.dll> --swf <adresse> --page <adresse de la page>
    [--flashvars <…>] [--width N] [--height N] [--id <…>] [--param nom=valeur]…
    [--user-agent <…>] [--private] [--hidden]
```

Sortie standard, un événement JSON par ligne : `ready` (fenêtre à loger), `status`,
`navigate` (url, cible), `script` (code pour la page), `audio`, `log`, `exit`.
Entrée standard : `close`.

## Tests

`flash-engine/test-plugin/testplugin.c` est un greffon NPAPI de test : il se comporte comme un
module Flash vis-à-vis de l'hôte et rapporte ce qu'il observe (`NPN_Status("TEST …")`).
`tests/PommeFlash.Tests` lance le vrai hôte avec ce greffon et un serveur HTTP local, puis vérifie
tout le parcours : paramètres, fenêtre, flux par petites bouchées, notifications, 404, envoi,
objets de la page, fils, minuteries, navigation, fin propre.

Sous Linux, avec Wine :

```sh
x86_64-w64-mingw32-gcc -shared -O2 -o /tmp/npPommeTest.dll flash-engine/test-plugin/testplugin.c
dotnet publish flash-engine/PommeFlash.Host -c Release -r win-x64 --self-contained -o /tmp/pommeflash
POMMEFLASH_HOST=/tmp/pommeflash/PommeFlashHost.exe POMMEFLASH_TEST_PLUGIN=/tmp/npPommeTest.dll \
POMMEFLASH_LAUNCHER=wine xvfb-run -a dotnet test tests/PommeFlash.Tests
```

Sous Windows, le greffon se compile avec `clang -shared` (ou `cl /LD`) et `POMMEFLASH_LAUNCHER`
n'est pas nécessaire.

## Étapes

1. **Hôte NPAPI sous Windows** (fait) : le module lit un contenu dans une fenêtre logée dans l'onglet.
2. Contenu à sa place dans la page (suivi du défilement et des dimensions, `wmode`).
3. Cookies et session partagés avec la page.
4. `ExternalInterface` : appels entre les scripts de la page et Flash.
5. Linux (`libflashplayer.so`, GTK 2 / XEmbed).
