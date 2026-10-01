# Moteur Flash intégré (PommeFlashHost)

PommeBrowser lit les contenus Flash avec **Ruffle**. Pour ceux que Ruffle ne sait pas lire, le
moteur intégré utilise **le module Flash Player de l'utilisateur** (`NPSWF64_*.dll`, ou
`NPSWF32_*.dll` en 32 bits), comme le
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
- **32 et 64 bits, choisis seuls.** Un processus ne charge que les modules de son architecture :
  l'hôte est publié en `win-x64` (`flash\PommeFlashHost.exe`) et en `win-x86`
  (`flash\x86\PommeFlashHost.exe`). L'architecture d'un module est lue dans son en-tête PE, pas
  dans son nom (`NPSWF32.dll` des anciennes versions compris), et PommeBrowser lance l'hôte qui va
  avec. Le moteur intégré essaie un module par architecture : la copie installée (`plugins` en
  64 bits, `plugins\x86` en 32 bits, hors de la vue de Basilisk), sinon le Flash Player installé
  dans Windows (`System32` et `SysWOW64\Macromed\Flash`), utilisé en place. Ordre : une version
  sans le blocage de 2021 d'abord, puis le 32 bits (certains jeux ne fonctionnent qu'avec lui).
  Si l'hôte s'arrête avant d'afficher le contenu (module refusé, code 4 ou 5, plantage), l'autre
  module prend le relais, dans la page comme dans l'onglet. « Rechercher le module Flash »
  installe le meilleur module de chaque architecture trouvé. En 32 bits, les conventions d'appel comptent : fonctions NPAPI en
  `cdecl`, points d'entrée `NP_*` en `stdcall` (déclarées explicitement, sans effet en 64 bits) ;
  `NPVariant` fait 16 octets et la table `NPNetscapeFuncs` 236.
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
**Diagnostic** de PommeBrowser, lignes `[Flash]`. L'hôte y note aussi (`WindowWatch.cs`) chaque
fenêtre que le module ouvre, avec son titre et son texte (boîte de dialogue de Flash, message de
mise à jour), les programmes qu'il lance (FlashUtil…) et ceux de Flash déjà présents (service
d'aide de la version chinoise), ainsi que les pages demandées avec une cible : on sait alors si un
message vient de Flash lui-même ou du contenu, qui dessine les siens dans sa fenêtre.

**Comme Basilisk** : le module reçoit l'identité de navigateur de Basilisk (`NPN_UserAgent`,
`WOW64` pour l'hôte 32 bits) et le facteur d'échelle du contenu (`NPNVcontentsScaleFactor`, 1 :
l'hôte n'est pas adapté aux DPI, Windows met ses fenêtres à l'échelle).

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
n'est pas nécessaire. En 32 bits : `clang --target=i686-pc-windows-msvc`, avec l'hôte publié en
`win-x86` ; la CI Windows passe les tests dans les deux architectures.

## Comparer avec Basilisk : l'espion

`flash-engine/spy` : un module espion qui se place entre le navigateur et le vrai module Flash et
note chaque échange (paramètres, questions et réponses, scripts, propriétés de la page,
chargements). Installé devant le module de PommeBrowser, il est chargé par Basilisk comme par le
moteur intégré : deux journaux pour la même page, à comparer. Voir `flash-engine/spy/README.md`.

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
