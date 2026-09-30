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
- **Fenêtre** (`HostWindow.cs`) : mode fenêtré, le module dessine dans sa fenêtre, que
  PommeBrowser loge dans l'onglet comme il le faisait pour Basilisk. Les modes `direct` et `gpu`
  demandés par la page sont gardés (fenêtrés sous Windows, ils donnent accès à Stage3D, dont
  beaucoup de jeux ont besoin) ; `opaque` et `transparent`, sans fenêtre, deviennent `window`.

## Échanges avec PommeBrowser

Lancement :

```
PommeFlashHost.exe --plugin <NPSWF64_*.dll> --swf <adresse> --page <adresse de la page>
    [--flashvars <…>] [--width N] [--height N] [--id <…>] [--param nom=valeur]…
    [--user-agent <…>] [--private] [--hidden]
```

Sortie standard, un événement JSON par ligne : `ready` (fenêtre à loger), `status`,
`navigate` (url, cible), `script` (adresse `javascript:`, sans réponse), `eval` (id, code : script
de la page dont le module attend le résultat), `audio`, `log` (niveaux info, error, trace), `exit`.
Entrée standard : `close`, et `result <id> {"ok":true,"value":…}` en réponse à `eval`.

**Scripts de la page** (`ExternalInterface.call`, `NPN_Evaluate`) : le module attend le résultat,
comme dans un navigateur. L'hôte envoie `eval` puis attend la réponse (20 s au plus) en traitant les
messages que Windows lui envoie d'autres fils : PommeBrowser place et affiche la fenêtre du module
pendant ce temps, et les deux processus s'attendraient sinon l'un l'autre. PommeBrowser exécute le
script dans la page (document principal seulement : un contenu venu d'un cadre reçoit un refus) ;
le module applique lui-même `allowScriptAccess`.

**Journal** : version du module, contenu, noms des flashvars (pas leurs valeurs), puis une trace de
chaque fichier chargé (et de la réponse), de chaque script demandé à la page et de ce que l'hôte ne
fournit pas (valeurs `NPN_GetValue`, membres des objets de la page) ; 300 traces au plus. Page
**Diagnostic** de PommeBrowser, lignes `[Flash]`.

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

## Dans PommeBrowser

Réglage **Paramètres › Avancé › Moteur Flash intégré (expérimental)**, Windows seulement.

1. Le script de détection de Ruffle décrit le contenu Flash principal de la page (le plus
   grand) : adresse du SWF, page, flashvars, taille, identifiant et paramètres
   (`avalonia/Engine/FlashContent.cs`, données de la page bornées et vérifiées).
2. Quand Ruffle échoue (ou au clic sur ⚡), l'onglet lance `flash\PommeFlashHost.exe` avec le module
   Flash importé (`avalonia/Legacy/FlashHostProcess.cs`), dans un job Windows qui le ferme avec
   PommeBrowser.
3. **Contenu du document principal : à sa place dans la page.** Le script de suivi
   (`RuffleContent.FlashTrackerScript`) remplace l'élément repéré par la détection
   (`data-pomme-flash`, gardé par Ruffle quand il remplace l'élément) par un emplacement noir de
   même taille, ce qui arrête Ruffle, puis envoie sa position à chaque changement (défilement,
   taille de la fenêtre, mise en page, zoom) : `rect:{x, y, w, h, dpr, visible}` en pixels CSS
   (`FlashRect`, bornée). L'onglet loge la fenêtre de l'hôte dans une vue posée par-dessus la
   page web (`BrowserTab.FlashOverlay.cs`) : la vue couvre la partie visible du contenu, et la
   fenêtre du lecteur y garde sa taille entière, décalée quand le contenu dépasse de la zone
   (`FlashRect.Place`, `Win32Dock.SetClientPlacement`). Les éléments de la page qui passent
   par-dessus le contenu restent dessous, comme avec `wmode=window` dans les anciens navigateurs.
4. **Contenu d'un cadre (iframe), ou élément introuvable** : la fenêtre de l'hôte est logée à la
   place de la page, par le même mécanisme que Basilisk.
5. ⚡ ou « Lire avec Ruffle » revient à Ruffle pour le site pendant la session. Si le lecteur
   s'arrête, « Relancer » reprend l'emplacement laissé dans la page.

Sans le réglage, sans module Flash ou sans description du contenu, Basilisk reste le moteur de secours.

## Étapes

1. **Hôte NPAPI sous Windows** (fait) : le contenu principal est lu dans une fenêtre logée dans l'onglet.
2. **Contenu à sa place dans la page** (fait) : suivi du défilement, des dimensions et du zoom.
3. Cookies et session partagés avec la page.
4. `ExternalInterface` : appels entre les scripts de la page et Flash. **Flash → page fait**
   (`ExternalInterface.call`, adresses `javascript:`) ; reste page → Flash (`addCallback`).
5. Linux (`libflashplayer.so`, GTK 2 / XEmbed).
