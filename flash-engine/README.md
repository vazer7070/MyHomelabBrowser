# Moteur Flash intégré (PommeFlashHost)

PommeBrowser lit les contenus Flash avec **Ruffle**. Pour ceux que Ruffle ne sait pas lire, le
moteur intégré utilise **le module Flash Player de l'utilisateur** (`NPSWF64_*.dll`, ou
`NPSWF32_*.dll` en 32 bits ; `libflashplayer.so` sous Linux), comme le faisait Basilisk, mais
sans Basilisk : PommeBrowser joue lui-même le rôle du navigateur auprès du module, par
l'interface NPAPI.

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
  Redirections (`BrowserHttpHandler.cs`) suivies étape par étape, 20 au plus, comme Firefox :
  pour un chargement notifié (`notifyData` non nul), un module qui le demande est consulté avant
  chacune (`NPP_URLRedirectNotify`, réponse par `NPN_URLRedirectResponse`, 20 s au plus) ; Flash
  y applique ses règles de sécurité, et un refus fait échouer le chargement. Un envoi (POST)
  redirigé par 307 ou 308 vers une autre origine n'est pas suivi. Les flux sont annoncés non
  « seekable » (`NPN_RequestRead` refusé et noté dans le journal) : Flash Player recherche dans une
  vidéo par de nouvelles requêtes.
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
- **Fenêtre** (`IHostDisplay` : `Win32Display.cs` sous Windows, `GtkDisplay.cs` sous Linux) :
  mode fenêtré, le module dessine dans sa fenêtre, que PommeBrowser loge dans l'onglet comme il
  le faisait pour Basilisk. Les modes `direct` et `gpu` demandés par la page sont gardés
  (fenêtrés sous Windows, ils donnent accès à Stage3D, dont beaucoup de jeux ont besoin) ;
  `opaque` et `transparent`, sans fenêtre, deviennent `window`. Sous Windows, un clic dans le
  contenu lui donne le clavier (`WM_MOUSEACTIVATE`, comme Firefox le faisait pour ses modules :
  Flash compte sur le navigateur), et PommeBrowser le reprend quand un de ses champs a le focus.
- **Linux** (`GtkDisplay.cs`, `Native/Gtk.cs`) : le module Flash de Linux est écrit pour GTK 2
  et XEmbed. L'hôte ouvre une fenêtre GTK 2 avec une prise (`GtkSocket`) donnée au module
  (`NPWindow.window` = XID de la prise, `ws_info` = `NPSetWindowCallbackStruct` avec l'affichage
  X11, le visuel, la palette et la profondeur) et répond `NPNVToolkit` = GTK 2,
  `NPNVSupportsXEmbedBool` = vrai, `NPNVxDisplay`. Le module y branche un `GtkPlug` avec la GTK
  que l'hôte a chargée. Boucle de messages : celle de GTK (`g_idle_add`, `g_timeout_add` pour les
  minuteries). Le module est chargé par `dlopen` et son `NP_Initialize(browser, plugin)` d'Unix.
  L'hôte meurt avec PommeBrowser (`prctl(PR_SET_PDEATHSIG)`) ; les erreurs X11 sont notées dans
  le journal avant le traitement de GDK. Il faut X11 ou XWayland (variable `DISPLAY`).
  Clavier et souris : la souris va directement à la fenêtre du module (X11) ; un clic dans le
  contenu donne le focus X11 au cadre, comme un navigateur (filtre GDK sur `ButtonPress`), et les
  touches reçues par le cadre sont transmises à la fenêtre du module (GTK ne le fait pas de
  lui-même quand rien n'y est focalisable). Fenêtre « cachée » (`--hidden`) : une fenêtre sans
  gestionnaire, hors de l'écran, que PommeBrowser loge dans l'onglet (X11Dock) ; elle ne demande
  jamais sa propre taille (`gtk_widget_set_size_request(prise, 1, 1)`). Diagnostic : avec
  `POMMEFLASH_XTRACE=1`, l'hôte note chaque événement X reçu ; avec `POMMEFLASH_XSYNC=1`, une
  erreur X11 est signalée à l'appel fautif, avec la pile de l'hôte et les derniers événements.

## Échanges avec PommeBrowser

Lancement :

```
PommeFlashHost.exe --plugin <NPSWF64_*.dll> --swf <adresse> --page <adresse de la page>
    [--flashvars <…>] [--width N] [--height N] [--id <…>] [--param nom=valeur]…
    [--user-agent <…>] [--private] [--share-cookies] [--hidden]
```

Sortie standard, un événement JSON par ligne : `ready` (fenêtre à loger), `status`,
`navigate` (url, cible), `script` (adresse `javascript:`, sans réponse), `eval` (id, code : script
de la page dont le module attend le résultat), `cookies` (id, url, http : cookies de la page pour
une adresse), `set-cookie` (url, cookie, http : cookie à garder dans la page), `audio`, `log`
(niveaux info, error, trace), `called` (id, ok, value : réponse à un appel de la page), `exit`.
Entrée standard : `close`, `result <id> {"ok":true,"value":…}` en réponse à `eval` et `cookies`,
et `call <id> {"request":"<invoke …>"}` (appel de la page vers le contenu), `ping <n>` (réponse
`pong`, voir plus bas).

**Robustesse.** Une fois le contenu affiché, PommeBrowser envoie `ping` toutes les 3 s ; le fil du
module répond `pong` quand il traite ses messages, ou aussitôt s'il attend PommeBrowser (script de
la page, cookies). Sans réponse depuis 20 s (plus que les 15 s après lesquelles Flash propose
lui-même d'arrêter un script trop long), l'onglet signale que le lecteur ne répond plus et propose
de le relancer. Un hôte qui s'arrête de lui-même après avoir affiché le contenu (plantage du
module) est relancé automatiquement au même endroit, avec le même module, une fois toutes les
5 minutes au plus ; au-delà, l'onglet propose « Relancer ». À la fermeture (`close`), les
chargements en cours sont interrompus (`NPRES_USER_BREAK`, puis `NPP_URLNotify`) avant
`NPP_Destroy`, sans attendre leur fin.

**Scripts de la page** (`ExternalInterface.call`, `NPN_Evaluate`) : le module attend le résultat,
comme dans un navigateur. L'hôte envoie `eval` puis attend la réponse (20 s au plus) en traitant les
messages que Windows lui envoie d'autres fils : PommeBrowser place et affiche la fenêtre du module
pendant ce temps, et les deux processus s'attendraient sinon l'un l'autre. PommeBrowser exécute le
script dans la page (document principal seulement : un contenu venu d'un cadre reçoit un refus) ;
le module applique lui-même `allowScriptAccess`.

**Appels de la page vers le contenu** (`ExternalInterface.addCallback`) : Flash déclare ses
fonctions dans la page par `__flash__addCallback(élément, nom)` ; elles appellent
`élément.CallFunction(<invoke …>)` et évaluent la réponse. PommeBrowser donne `CallFunction` à
l'élément du contenu (`RuffleContent.FlashBridgeScript`, et à l'emplacement qui le remplace dans la
page) : il passe par l'objet WebView2 `pommeFlash` (`AddHostObjectToScript`, document principal
seulement), ou sous WebKitGTK par une requête synchrone au schéma
`pomme-flash://call/?t=<jeton>&r=<requête>`, puis par la commande `call` jusqu'à l'objet
scriptable du module (`NPPVpluginScriptableNPObject`, méthode `CallFunction`). Le jeton, tiré au
hasard pour chaque lecteur, n'est connu que du script du pont injecté dans le document principal :
un cadre d'un autre site (publicité) qui appelle le schéma reçoit un refus (403) et n'atteint pas
les fonctions du jeu. Une requête de plus de 4 Mio est refusée. La page attend la réponse (8 s au
plus) ; PommeBrowser ne traite pendant ce temps que les messages que Windows envoie d'autres
processus. Si le lecteur ne répond plus (battement de cœur), ou si deux appels de suite restent
sans réponse, les appels suivants sont refusés aussitôt (10 s dans le second cas) : l'interface ne
se fige pas à chaque appel.

**Appels imbriqués.** Comme dans un navigateur, un script de la page demandé par le contenu peut
appeler le contenu avant de rendre son résultat (contenu → page → contenu) : pendant qu'il attend
un script, l'hôte exécute les appels de la page qui arrivent (`HostChannel.RunCalls`, 8 niveaux
au plus), au lieu de les laisser attendre la fin du script, qui les attend. Dans l'autre sens
(page → contenu → page), la page est bloquée par son appel et ne peut rien exécuter : le contenu
reçoit aussitôt `undefined` (`<undefined/>`), et son script est exécuté dans la page dès que
l'appel a répondu (la plupart des `ExternalInterface.call` n'attendent pas de résultat). Une
question de cookies du fil du module (`NPN_GetValueForURL`) pendant un appel de la page reçoit
aussitôt une réponse vide.

**Cookies partagés avec la page** (`--share-cookies`, `BrowserHttpHandler.cs`) : comme dans un
navigateur, chaque chargement du module porte les cookies de la page pour son adresse, HttpOnly
compris, et ceux que les réponses déposent (`Set-Cookie`) sont gardés dans la page. Les
redirections sont suivies par l'hôte, étape par étape, pour que chacune porte ses propres cookies.
`NPN_GetValueForURL` donne ce qu'un script verrait (sans HttpOnly), `NPN_SetValueForURL` pose un
cookie comme un script. PommeBrowser (`FlashCookies.cs`) ne partage que le site de la page (même
domaine enregistrable) : le lecteur exécute un module tiers, il n'a pas accès aux cookies des autres
sites. Chaque cookie reçu est vérifié (domaine, Secure, HttpOnly, préfixes `__Secure-` et
`__Host-`) avant d'aller dans le profil WebView2 de l'onglet, navigation privée comprise. Sans
l'option, l'hôte a ses propres cookies, vides au départ.

**Journal** : version du module, contenu, noms des flashvars (pas leurs valeurs), puis une trace de
chaque fichier chargé (et de la réponse), de chaque script demandé à la page et de ce que l'hôte ne
fournit pas (valeurs `NPN_GetValue`, membres des objets de la page) ; 300 traces au plus. Page
**Diagnostic** de PommeBrowser, lignes `[Flash]`. L'hôte y note aussi (`WindowWatch.cs`) chaque
fenêtre que le module ouvre, avec son titre et son texte (boîte de dialogue de Flash, message de
mise à jour), les programmes qu'il lance (FlashUtil…) et ceux de Flash déjà présents (service
d'aide de la version chinoise), ainsi que les pages demandées avec une cible : on sait alors si un
message vient de Flash lui-même ou du contenu, qui dessine les siens dans sa fenêtre.

**Comme Basilisk** : le module reçoit l'identité de navigateur de Basilisk (`NPN_UserAgent`,
`WOW64` pour l'hôte 32 bits, `X11; Linux x86_64` sous Linux) et le facteur d'échelle du contenu
(`NPNVcontentsScaleFactor`, 1 : l'hôte n'est pas adapté aux DPI, Windows met ses fenêtres à
l'échelle).

## Tests

`flash-engine/test-plugin/testplugin.c` est un greffon NPAPI de test : il se comporte comme un
module Flash vis-à-vis de l'hôte et rapporte ce qu'il observe (`NPN_Status("TEST …")`).
`tests/PommeFlash.Tests` lance le vrai hôte avec ce greffon et un serveur HTTP local, puis vérifie
tout le parcours : paramètres, fenêtre, flux par petites bouchées, notifications, 404, envoi,
objets de la page, fils, minuteries, navigation, fin propre. Et aussi :

- appels imbriqués (appel de la page pendant que le contenu attend un script), redirections
  soumises au module (accordée, refusée), fermeture pendant un téléchargement sans fin,
  battement de cœur (`ping`/`pong`) ;
- logement X11 (`DockedHostTests`, Linux) : la fenêtre cachée de l'hôte est logée dans celle
  d'un autre programme et redimensionnée par lui, puis clic et touche simulés (XTEST) ;
- tenue (`EnduranceTests`) : deux séries identiques de 100 chargements, avec objets créés et
  objets de la page lus à chaque fois (le greffon les enchaîne quand la page lui demande
  `boucle:N`) ; entre les deux, rien ne doit croître (commande de diagnostic `stats` : blocs de
  mémoire, objets de l'hôte, flux, minuteries) ;
- robustesse du protocole : lignes malformées, JSON d'un autre type, nombres hors limites,
  ligne de 1 Mo, octets au hasard ; l'hôte doit continuer de répondre et finir normalement.

Côté PommeBrowser, `tests/PommeBrowser.UiTests/FlashHostTests.cs` lance l'hôte réel (mêmes
variables, Linux) : processus arrêté (`SIGSTOP`) signalé comme figé puis rétabli, processus tué
reconnu comme un plantage.

Sous Linux, l'hôte de Linux (greffon GTK 2, sans écran) :

```sh
gcc -shared -fPIC -O2 -o /tmp/libnpPommeTest.so flash-engine/test-plugin/testplugin.c $(pkg-config --cflags --libs gtk+-2.0) -lX11 -lXtst -lpthread
dotnet publish flash-engine/PommeFlash.Host -c Release -r linux-x64 --self-contained -o /tmp/pommeflash-linux
POMMEFLASH_HOST=/tmp/pommeflash-linux/PommeFlashHost POMMEFLASH_TEST_PLUGIN=/tmp/libnpPommeTest.so \
xvfb-run -a dotnet test tests/PommeFlash.Tests
```

Sous Linux, l'hôte de Windows avec Wine :

```sh
x86_64-w64-mingw32-gcc -shared -O2 -o /tmp/npPommeTest.dll flash-engine/test-plugin/testplugin.c
dotnet publish flash-engine/PommeFlash.Host -c Release -r win-x64 --self-contained -o /tmp/pommeflash
POMMEFLASH_HOST=/tmp/pommeflash/PommeFlashHost.exe POMMEFLASH_TEST_PLUGIN=/tmp/npPommeTest.dll \
POMMEFLASH_LAUNCHER=wine xvfb-run -a dotnet test tests/PommeFlash.Tests
```

Sous Windows, le greffon se compile avec `clang -shared` (ou `cl /LD`) et `POMMEFLASH_LAUNCHER`
n'est pas nécessaire. En 32 bits : `clang --target=i686-pc-windows-msvc`, avec l'hôte publié en
`win-x86` ; la CI Windows passe les tests dans les deux architectures, et la CI Linux avec l'hôte
de Linux. Hors de Windows, l'hôte 32 bits demande Wine en 32 bits (`wine32`), souvent absent :
la CI Windows reste la référence pour le 32 bits. Les vérifications propres à un système (boîtes de dialogue et programmes du module sous
Windows ; GTK 2, XEmbed et affichage X11 sous Linux) ne sont faites que pour l'hôte concerné. Le
clavier est vérifié sur les deux : sous Windows, le greffon simule le clic (`WM_MOUSEACTIVATE`) ;
sous Linux, il met le clavier dans une autre fenêtre, clique dans le contenu et tape une touche
(XTEST, `libxtst-dev`), qui doit lui arriver même sans rien de focalisable dans sa fenêtre et le
pointeur ailleurs.

## Comparer avec Basilisk : l'espion

`flash-engine/spy` : un module espion qui se place entre le navigateur et le vrai module Flash et
note chaque échange (paramètres, questions et réponses, scripts, propriétés de la page,
chargements). Installé devant le module de PommeBrowser, il est chargé par Basilisk comme par le
moteur intégré : deux journaux pour la même page, à comparer. Voir `flash-engine/spy/README.md`.

## Dans PommeBrowser

Réglage **Paramètres › Avancé › Moteur Flash intégré (expérimental)**, Windows et Linux (X11).

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
3. **Cookies et session partagés avec la page** (fait) : chargements, redirections,
   `NPN_GetValueForURL` et `NPN_SetValueForURL`, limités au site de la page.
4. **`ExternalInterface`** (fait) : appels entre les scripts de la page et Flash, dans les deux
   sens (`ExternalInterface.call` et adresses `javascript:`, `ExternalInterface.addCallback`).
5. Linux (`libflashplayer.so`, GTK 2 / XEmbed) :
   a. **Hôte sous Linux** (fait) : fenêtre GTK 2 et prise XEmbed, boucle GLib, `dlopen`,
      greffon de test GTK 2, tests sans écran dans la CI Linux (`PommeFlashHost-Linux-x64`).
   b. **PommeBrowser sous Linux** (fait) : lance l'hôte (`libflashplayer.so` installé, sinon
      celui d'un dossier de modules du système), le loge dans l'onglet à la place de la page
      (X11Dock, comme Basilisk : le clic lui donne le clavier) et le livre dans l'AppImage x86_64
      (`usr/lib/pommebrowser/flash/PommeFlashHost`). Il faut GTK 2 sur la machine (`libgtk2.0-0`).
   c. **Contenu à sa place dans la page sous X11, cookies et appels de la page** (fait) : la
      fenêtre de l'hôte est logée par-dessus la page WebKitGTK à la place du contenu (X11Dock :
      place demandée, premier plan), les cookies passent par le gestionnaire de cookies de WebKit
      (`--share-cookies`), et la page appelle le contenu (`addCallback`) par une requête synchrone
      au schéma `pomme-flash://call/?t=…&r=…`, servie par PommeBrowser.
