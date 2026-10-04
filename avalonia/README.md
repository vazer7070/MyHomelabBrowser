# PommeBrowser — édition Avalonia (Windows, macOS, Linux)

Une seule application pour les trois systèmes. L'interface est écrite avec **Avalonia 12** ; les pages sont affichées par le moteur web de chaque système, via Avalonia.Controls.WebView :

| Système | Moteur | Paquet |
|---|---|---|
| Windows 10 / 11 | **WebView2** (Edge Chromium, fourni par Windows) | Velopack (installation et mises à jour), ou dossier publié |
| macOS 12 et suivants | **WKWebView** (le moteur de Safari) | `PommeBrowser.app` dans une archive `.zip` |
| Linux | **WebKitGTK 4.1** (le moteur de GNOME Web) | AppImage |

Elle reprend les fonctions de l'édition Windows (WPF) et de l'édition Linux (GTK) :
- le code du navigateur, partagé et lié depuis `../classes` et `../linux/Core` ;
- les formats de fichiers ;
- les dossiers de données.

Chaque installation existante retrouve donc ses profils, favoris, coffre, historique et réglages.

## Installer

### Windows

WebView2 est déjà présent sur Windows 11 et sur Windows 10 à jour (sinon : [WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/)).

- **Version publiée** : l'installateur Velopack (`…-Setup.exe`) de la page des versions. Les mises à jour se font ensuite toutes seules.
- **Compilation GitHub** : onglet **Actions**, artefact *PommeBrowser-Avalonia-Windows-x64*. Décompressez-le et lancez `MyHomelabBrowser.exe`.

L'édition Avalonia est publiée avec le même identifiant et le même nom d'exécutable que l'édition WPF (voir **Publier**). Une installation existante la reçoit comme une mise à jour, avec ses données.

### macOS

Téléchargez `PommeBrowser-<version>-macos-arm64.zip` (Mac à puce Apple) ou `…-macos-x64.zip` (Mac Intel). Décompressez-le et placez `PommeBrowser.app` dans **Applications**.

Tant que l'app n'est pas signée avec un certificat Developer ID, macOS demande une confirmation au premier lancement : clic droit sur l'app, puis **Ouvrir**.

### Linux

Le moteur web et GTK viennent de votre distribution, qui leur apporte ses mises à jour de sécurité :

| Distribution | Commande |
|---|---|
| Ubuntu 22.04+, Linux Mint 21+, Debian 12+ | `sudo apt install libwebkit2gtk-4.1-0 libgtk-3-0 gstreamer1.0-plugins-good` |
| Fedora 38+ | `sudo dnf install webkit2gtk4.1 gtk3 gstreamer1-plugins-good` |
| Arch, Manjaro, EndeavourOS | `sudo pacman -S --needed webkit2gtk-4.1 gtk3 gst-plugins-good` |
| openSUSE Tumbleweed | `sudo zypper install libwebkit2gtk-4_1-0 gtk3 gstreamer-plugins-good` |

WebKitGTK 2.40 minimum. Si une bibliothèque manque, PommeBrowser l'indique au lancement, avec la commande à exécuter.

Lancez ensuite l'AppImage (x86_64 pour les PC, aarch64 pour l'ARM 64 bits : Raspberry Pi 4/5…) :

```bash
chmod +x PommeBrowser-*-x86_64.AppImage
./PommeBrowser-*-x86_64.AppImage
```

Elle porte le même nom que celle de l'édition GTK. Gardez-la dans un dossier modifiable (`~/Applications`…) : les versions suivantes s'installent toutes seules.

## Fonctions

**Venir de Firefox ou de Chrome**
- Au premier lancement, un accueil propose de reprendre les favoris et l'historique du navigateur le plus utilisé de l'ordinateur (Chrome, Edge, Brave, Vivaldi, Opera, Firefox), de choisir le moteur de recherche, de faire de PommeBrowser le navigateur par défaut et d'importer les mots de passe. Tout reste dans **Réglages › Général** (« Navigateur par défaut », « Venir d'un autre navigateur »).
- Mots de passe : depuis le fichier CSV exporté par Chrome, Edge, Brave, Firefox, Safari ou Bitwarden, vers le coffre chiffré, sans remplacer ceux déjà présents ; la suppression du fichier en clair est proposée aussitôt.
- Navigateur par défaut : Windows (PommeBrowser s'inscrit à l'installation ; la page « Applications par défaut » s'ouvre à son nom), Linux (fichier .desktop de l'AppImage et xdg-settings), macOS (http et https déclarés, confirmation du système).
- Une seule instance, comme Firefox et Chrome : un lien ouvert depuis une autre application (courriel, document, messagerie) arrive dans un onglet de la fenêtre ouverte, qui passe au premier plan ; relancer PommeBrowser ouvre une nouvelle fenêtre.

**Navigation**
- Onglets réorganisables et épinglables. `Ctrl+Maj+T` rouvre le dernier fermé.
- Onglets privés (`Ctrl+Maj+N`), avec leur couleur propre.
- Reprise de la session, plusieurs fenêtres, déplacement d'un onglet dans une nouvelle fenêtre.
- Vue côte à côte : un clic dans un volet en fait l'onglet actif.
- Espaces de travail et mise en veille des onglets inactifs (comptée depuis qu'on a quitté l'onglet ; jamais un onglet dont le Flash joue avec le moteur intégré).

**Barre d'adresse et page d'accueil**
- Propositions au fil de la saisie : services du homelab, favoris, pages visitées.
- Commandes (`:`) et accès direct à un onglet (`@`).
- Page d'accueil avec les services du homelab, leur état en direct, les favoris et les pages récentes.

**Profils et mots de passe**
- Profils protégés par mot de passe, avec leurs propres réglages, favoris, historique, cookies et coffre.
- Changement de profil sans redémarrer sous Windows et macOS : les fenêtres du profil quitté se ferment et celles du nouveau profil s'ouvrent au même endroit, avec ses onglets. Sous Linux, WebKitGTK fixe ses dossiers de données au démarrage : PommeBrowser redémarre. Renommer ou supprimer un profil redémarre partout (ses dossiers ne se déplacent qu'au démarrage).
- Coffre chiffré (AES-256-GCM, clé dérivée du mot de passe avec Argon2id : 64 Mio, 3 passes, 4 voies) :
  - PommeBrowser propose d'enregistrer les identifiants et remplit les formulaires ;
  - codes de double authentification (TOTP) ;
  - page « Mots de passe », verrouillage automatique après un délai sans utilisation ;
  - un coffre créé par une version précédente (clé PBKDF2) est converti à son premier déverrouillage, sans rien demander. Les éditions WPF et GTK, gelées, ne lisent le nouveau format qu'à partir de cette version.

**Anti-pub** (listes EasyList et EasyPrivacy, mêmes réglages partout)
- Linux et macOS : les listes sont converties en règles WebKit, compilées une fois, puis appliquées par le moteur.
- Windows : chaque requête passe par le moteur de règles de l'édition WPF, avec le même masquage des emplacements publicitaires.
- Désactivable par site. Le réseau local n'est pas filtré par défaut.

**Sécurité**
- HTTPS automatique, avec une page claire quand un site ne le propose pas.
- Certificats du homelab : la page de PommeBrowser montre l'émetteur et l'empreinte, vous pouvez les approuver, et vous êtes prévenu s'ils changent.
- Autorisations des sites (position, caméra, micro, notifications…) mémorisées par site.
- Protection contre le pistage.

**Flash**
- Ruffle, intégré et vérifié par SHA-256 à la compilation, sans aucun CDN. Comme l'extension Ruffle, PommeBrowser annonce un lecteur Flash aux pages (`navigator.plugins`) : les sites qui vérifient la présence de Flash avant d'afficher leur contenu (SWFObject…) l'affichent, et Ruffle le lit. Le bouton Basilisk de la barre d'adresse ouvre la page avec le lecteur Flash d'origine, puis propose d'y ouvrir toujours ce site. Quand Ruffle s'arrête sur une erreur (fichier illisible, contenu non pris en charge…), la page passe d'elle-même dans Basilisk, sauf si un autre contenu de la page est déjà lu ou si l'on est revenu à Ruffle pour ce site (réglage dans **Paramètres › Avancé**).
- **Moteur Flash intégré** (expérimental, Windows et Linux X11, à activer dans **Paramètres › Avancé**) : les contenus que Ruffle ne lit pas sont lus avec le module Flash de l'utilisateur par `PommeFlashHost`, un hôte NPAPI livré avec PommeBrowser, sans Basilisk. Le contenu est lu à sa place dans la page (il suit le défilement), y compris dans un cadre du même site (la page reste active), ou à la place de la page s'il est dans un cadre d'un autre site. Comme dans un navigateur avec Flash, une page passée au moteur intégré y lit tous ses contenus, un lecteur chacun, y compris ceux qu'elle ajoute ou met à la place d'un autre (un jeu qui charge son client à part pendant qu'il montre un logo, comme Evony), et ses scripts disposent des méthodes de Flash (`PercentLoaded`, `GetVariable`…) ; sur une page « jeu + publicités Flash », le contenu principal est le plus grand, mais une bannière aux formats publicitaires courants passe après lui, et les contenus cachés ou minuscules sont ignorés. Comme dans un navigateur, ses scripts et ceux de la page s'appellent dans les deux sens, y compris imbriqués (`ExternalInterface`), et ses chargements portent les cookies de la page (session de jeu comprise), pour le site de la page seulement ; un cadre d'un autre site ne peut pas appeler le contenu. Un lecteur qui plante est relancé seul, au même endroit (une fois toutes les 5 minutes au plus) ; un lecteur figé est signalé, avec « Relancer ». Sous Windows, il est net sur les écrans à plus de 100 % (adapté à la densité, comme Basilisk). **Diagnostic** montre les modules trouvés, leur version et le dernier arrêt inattendu. Voir [`flash-engine/README.md`](../flash-engine/README.md).
- **Quelle version du module Flash ?** La **32.0.0.371** (ou une plus ancienne) : les versions suivantes d'Adobe refusent les contenus depuis le 12 janvier 2021 (logo « Flash Player est bloqué »). PommeBrowser essaie d'abord un module sans ce blocage, signale les autres dans **Paramètres › Avancé** et dans **Diagnostic**, et ne fournit jamais Flash Player (Adobe interdit de le redistribuer).
- Pour les contenus que Ruffle ne lit pas : Basilisk, livré avec PommeBrowser sous le nom « Pomme Legacy » (Windows x64, Linux x86_64). La page s'ouvre dans l'onglet, avec le lecteur Flash d'origine. Avec **Paramètres › Avancé › Utiliser Basilisk** décoché, Basilisk ne se lance jamais (ni par le bouton Flash, ni d'office, ni pour les sites réglés sur Basilisk) : le moteur de secours est alors le moteur intégré. Adobe interdit de redistribuer Flash Player : chacun ajoute sa copie dans **Paramètres › Avancé**. Le bouton **Rechercher le module Flash** la trouve sur l'ordinateur (dossier d'installation de Flash, Bureau, Téléchargements, Documents, autres disques sous Windows), par exemple dans un Basilisk ou un Pale Moon portable ; sans résultat, il propose de choisir le dossier où chercher. Le meilleur module de chaque architecture est vérifié (architecture lue dans le fichier) puis copié dans les données de PommeBrowser, sans remplacer un module installé qui vaut mieux ; les versions qui bloquent les contenus depuis janvier 2021 sont signalées, et « Voir les modules trouvés » permet d'en choisir un autre. Sous Windows, le moteur intégré lit les modules 32 et 64 bits et choisit seul : il utilise aussi le Flash Player installé dans Windows, et si un module ne lit pas un contenu, l'autre prend le relais. Sous Linux (X11), il utilise `libflashplayer.so` (le module installé, sinon celui d'un dossier de modules du système) et demande GTK 2 (`libgtk2.0-0`) ; le contenu garde sa place dans la page, les cookies passent par WebKitGTK et la page peut appeler le contenu. Basilisk garde le module 64 bits. Détails dans [`legacy-engine/README.md`](../legacy-engine/README.md) et [`flash-engine/README.md`](../flash-engine/README.md).

**Autres**
- Téléchargements sans boîte de dialogue, jamais par-dessus un fichier existant.
- Recherche dans la page, zoom mémorisé par site, impression, outils de développement, plein écran.
- Import des favoris des autres navigateurs (dont Safari sous macOS).
- « Signaler un problème » : même rapport que les autres éditions. Un arrêt brutal de PommeBrowser (plantage dans du code natif, arrêt forcé) est consigné au démarrage suivant dans `errors.log`, joint au rapport : fin du journal de la session et, sous Windows, entrées du journal d'événements (module fautif, pile d'appels de .NET).
- Mises à jour : Velopack sous Windows, AppImage sous Linux.
- Thème clair, sombre ou celui du système. Français ou anglais.

**Différences selon le moteur**

| | Windows (WebView2) | macOS (WKWebView) | Linux (WebKitGTK) |
|---|---|---|---|
| Scripts de PommeBrowser (coffre, Ruffle) | dans la page | monde isolé, invisible pour la page | monde isolé, invisible pour la page |
| Ruffle servi depuis | `https://ruffle.pommebrowser.invalid/` (intercepté) | `http://127.0.0.1:<port>/ruffle/` | schéma `pomme-ruffle://` |
| Raccourcis | `Ctrl` | `Cmd` (ou `Ctrl`) | `Ctrl` |

## Où sont les données ?

| Système | Réglages des profils | Historique, session, cookies | Cache |
|---|---|---|---|
| Windows | `%APPDATA%\MyHomelabBrowser\` (comme l'édition WPF) | `%APPDATA%\MyHomelabBrowser\profiles\<nom>\`, et WebView2 dans `%LOCALAPPDATA%\PommeBrowser\Profiles\<nom>\WebView2` | `%LOCALAPPDATA%\PommeBrowser\Cache\` |
| macOS | `~/Library/Application Support/PommeBrowser/` | `~/Library/Application Support/PommeBrowser/profiles/<nom>/` | `~/Library/Caches/PommeBrowser/` |
| Linux | `~/.config/MyHomelabBrowser/` (comme l'édition GTK) | `~/.local/share/pommebrowser/` | `~/.cache/pommebrowser/` |

Les fichiers des profils ont le même format sur les trois systèmes. Vous pouvez copier `favorites.json`, `services.json`, `workspaces.json` ou `vault.json.enc` d'un système à l'autre.

## Compiler

Il faut le SDK .NET 10 et, sous Linux, les bibliothèques ci-dessus.

```bash
dotnet run --project avalonia                     # lancer depuis les sources
dotnet test tests/PommeBrowser.Tests              # tests de la logique commune
```

Paquets :

```bash
avalonia/packaging/linux/build-appimage.sh all    # AppImage x86_64 et aarch64 (depuis Linux)
linux/packaging/smoke-test.sh avalonia/packaging/out/PommeBrowser-*-x86_64.AppImage
avalonia/packaging/macos/build-app.sh all         # PommeBrowser.app arm64 et x64 (depuis un Mac)
```

```powershell
.\build-pack-velopack.ps1 -Edition avalonia       # Windows : Velopack + version GitHub
```

La CI GitHub (`.github/workflows/build.yml`) lance les tests et construit les paquets Windows, macOS et Linux à chaque modification. Elle essaie aussi de lancer l'AppImage. Les tests de l'interface (`tests/PommeBrowser.UiTests`) ouvrent le vrai navigateur sans écran (Avalonia.Headless), avec des données temporaires, et le pilotent au clavier et à la souris : propositions de la barre d'adresse, page du coffre, verrouillage automatique, pages et réglages. Une page qui se reconstruirait sans fin fait échouer le test au lieu de le bloquer.

- **Ruffle** et les outils AppImage sont téléchargés dans une version fixée et vérifiés par SHA-256. Chaque lundi, `.github/workflows/ruffle-update.yml` compare la version de Ruffle à la dernière publiée et ouvre une issue (version, SHA-256, fichiers à modifier) quand une nouvelle version sort.
- **Pomme Legacy** (moteur Flash d'origine) : compilé par sa propre CI, puis pris dans la version GitHub `legacy-engine-<version>` (voir [`legacy-engine/README.md`](../legacy-engine/README.md)).
- **Signature macOS** (facultative) : `POMMEBROWSER_MACOS_SIGN_IDENTITY` (certificat Developer ID) et `POMMEBROWSER_NOTARY_PROFILE` (notarisation).
- **Signature Windows** : mêmes variables que l'édition WPF (`POMMEBROWSER_AZURE_SIGN_METADATA` ou `POMMEBROWSER_SIGN_PARAMS`).
- **Serveur de support** : `-p:SupportApiUrl=https://…` à la compilation, ou la variable `POMMEBROWSER_SUPPORT_API_URL`.

## Publier

Les trois éditions cherchent leurs mises à jour sur `vazer7070/PommeBrowser-release`.

1. **Windows** : `.\build-pack-velopack.ps1 -Edition avalonia`. Même identifiant de paquet (`com.vazer7070.myhomelabbrowser`), même canal et même `MyHomelabBrowser.exe` que l'édition WPF : ses utilisateurs passent à l'édition Avalonia à la mise à jour suivante.
2. **Linux** : `avalonia/packaging/linux/build-appimage.sh all`, puis `linux/packaging/publish-release.sh --avalonia`. Mêmes noms que les AppImage de l'édition GTK : ses utilisateurs passent à l'édition Avalonia de la même façon.
3. **macOS** : ajoutez les archives `.zip` et leurs `.sha256` à la version.

## Organisation du code

| Dossier | Contenu |
|---|---|
| `Core/` | Logique propre à l'édition : emplacements des données par système, anti-pub, coffre, téléchargements, relance, migrations |
| `Engine/` | Interface commune des moteurs (`IEngineTab`, `EngineHost`), raccourcis, Ruffle |
| `Engine/Gtk/` | WebKitGTK : appels directs à WebKit, clavier de la vue intégrée sous X11, fenêtres surgissantes |
| `Engine/WebView2/` | WebView2 : onglet, téléchargements, fenêtres de connexion, raccourcis |
| `Engine/Apple/` | WKWebView : runtime Objective-C (délégués, blocs), onglet, téléchargements, fenêtres |
| `Views/` | Fenêtre, onglets, barre d'adresse, pages internes, boîtes de dialogue |
| `Legacy/` | Basilisk (Linux et Windows) : lancement, fenêtre logée dans l'onglet (X11, Win32), moteur livré et module Flash |
| `Updates/` | Velopack (Windows), AppImage (Linux) |
| `packaging/` | AppImage, app macOS |
