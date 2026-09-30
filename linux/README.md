# PommeBrowser pour Linux

Édition Linux de PommeBrowser, distribuée en **AppImage** : un seul fichier, sans installation.

> L'**édition Avalonia** (dossier [`avalonia`](../avalonia/README.md)) réunit Windows, macOS et Linux dans une même application, avec les mêmes données. Son AppImage porte le même nom : publiée à la place de celle-ci, elle arrive comme une mise à jour.

Elle partage avec l'édition Windows la logique du navigateur :
- les profils protégés par mot de passe et le coffre à identifiants ;
- l'historique, les favoris et les espaces de travail ;
- les services du homelab ;
- l'anti-pub ;
- l'HTTPS automatique et les certificats approuvés ;
- les réglages de Basilisk pour le Flash d'origine ;
- le formulaire « Signaler un problème » ;
- les traductions.

L'interface est écrite avec **GTK 4 / libadwaita** et le moteur est **WebKitGTK 6.0**, le moteur de GNOME Web (Epiphany).

## Installer

### 1. Bibliothèques du système

Le moteur web et l'interface viennent de votre distribution. Ils reçoivent ainsi ses mises à jour de sécurité, que l'AppImage n'a pas à embarquer.

| Distribution | Commande |
|---|---|
| Ubuntu 24.04+, Linux Mint 22+, Debian 13+ | `sudo apt install libwebkitgtk-6.0-4 libadwaita-1-0 gstreamer1.0-plugins-good` |
| Fedora 40+ | `sudo dnf install webkitgtk6.0 libadwaita gstreamer1-plugins-good` |
| Arch, Manjaro, EndeavourOS | `sudo pacman -S --needed webkitgtk-6.0 libadwaita gst-plugins-good` |
| openSUSE Tumbleweed | `sudo zypper install libwebkitgtk-6_0-4 libadwaita-1-0 gstreamer-plugins-good` |

Versions minimales : WebKitGTK 2.40, GTK 4.12, libadwaita 1.5. Si une bibliothèque manque, PommeBrowser l'indique au lancement avec la commande à exécuter.

### 2. L'AppImage

Deux fichiers sont produits :
- `PommeBrowser-<version>-x86_64.AppImage` pour les PC ;
- `PommeBrowser-<version>-aarch64.AppImage` pour l'ARM 64 bits (Raspberry Pi 4/5…).

Ils sont disponibles dans les artefacts de la compilation GitHub (onglet **Actions**, job *Linux (AppImage)*).

```bash
chmod +x PommeBrowser-*-x86_64.AppImage
./PommeBrowser-*-x86_64.AppImage
```

Pour ajouter PommeBrowser au menu des applications, utilisez [Gear Lever](https://flathub.org/apps/it.mijorus.gearlever) ou AppImageLauncher.

L'AppImage n'a pas besoin de `libfuse2`. Sans FUSE (conteneur…), lancez-la avec `--appimage-extract-and-run`.

Les versions suivantes s'installent toutes seules (voir **Mises à jour** plus bas) : gardez l'AppImage dans un dossier où vous pouvez écrire, par exemple `~/Applications`.

### 3. Basilisk (facultatif)

Pour les contenus Flash que Ruffle ne lit pas :
1. téléchargez Basilisk pour Linux sur [basilisk-browser.org](https://www.basilisk-browser.org/download.shtml) et décompressez l'archive dans `~/.local/share/basilisk` (ou indiquez l'emplacement de `basilisk` dans les préférences) ;
2. copiez le module Flash `libflashplayer.so` dans `~/.local/share/pommebrowser/plugins` (ou `~/.mozilla/plugins`).

## Fonctions

**Navigation**
- Onglets réorganisables et épinglables. `Ctrl+Maj+T` rouvre le dernier fermé.
- Reprise de la session au démarrage.
- Fenêtre privée (`Ctrl+Maj+N`).
- Vue côte à côte : deux onglets affichés ensemble (menu principal, ou « Afficher à côté » dans le menu d'un onglet). Un clic dans un volet le rend actif.
- Espaces de travail : les onglets ouverts, enregistrés sous un nom (« Réseau », « Médias »…) et rouverts d'un clic. Même fichier que l'édition Windows.

**Profils**
- Profils protégés par mot de passe, avec leurs propres réglages, favoris, historique, cookies et mots de passe. Même fichier `profiles.json` que l'édition Windows.
- Menu du profil dans la barre d'en-tête : changer de profil, créer, renommer, changer le mot de passe, supprimer. Changer de profil relance PommeBrowser, qui rouvre les onglets de ce profil.
- Le profil par défaut n'a pas de mot de passe.

**Mots de passe**
- Coffre chiffré (AES-GCM, clé dérivée du mot de passe du coffre), au même format que sous Windows.
- Après une connexion, PommeBrowser propose d'enregistrer le mot de passe (« Jamais pour ce site », « Toujours »). Les formulaires sont ensuite remplis à l'ouverture de la page.
- La clé de la barre d'adresse remplit l'identifiant, ou le code de double authentification sur la page qui le demande.
- Page « Mots de passe » : recherche, affichage, copie (effacée du presse-papiers après 30 s), suppression, codes 2FA (TOTP) en direct.
- Les scripts du coffre tournent à l'écart de la page (monde isolé de WebKit) : une page ne peut ni envoyer de faux identifiants ni intercepter ceux qui sont remplis.

**Barre d'adresse**
- Propositions au fil de la saisie : services du homelab, favoris, puis pages les plus visitées.
- Recherche avec le moteur choisi. DuckDuckGo est proposé par défaut.

**Page d'accueil**
- Services du homelab, avec leur état en direct.
- Favoris et pages récentes.

**Services du homelab**
- Surveillance régulière, avec une notification quand un service tombe ou revient.
- Accès rapide depuis la barre d'en-tête.

**Favoris**
- Même fichier que l'édition Windows.
- Import depuis Firefox, Chrome, Chromium, Brave, Vivaldi, Edge (paquets classiques, Snap et Flatpak) ou depuis un fichier HTML.

**Anti-pub**
- Listes EasyList et EasyPrivacy, avec les mêmes réglages que sous Windows.
- Les listes sont converties en règles WebKit, compilées une fois (environ 10 s), puis appliquées par le moteur lui-même.
- Désactivable par site. Le réseau local n'est pas filtré par défaut.

**Flash**
- Contenus lus avec Ruffle, intégré et vérifié par SHA-256 à la compilation.
- Ruffle n'est chargé que sur les pages qui contiennent du Flash.
- Comme l'extension Ruffle, PommeBrowser annonce un lecteur Flash aux pages : les sites qui vérifient la présence de Flash avant d'afficher leur contenu (SWFObject, détection d'Adobe) l'affichent, et Ruffle le lit.
- Basilisk pour le reste : « Ouvrir dans Basilisk » (menu principal) ouvre la page dans une fenêtre Basilisk, avec le lecteur Flash d'origine. « Toujours » l'y ouvre d'office ensuite.
- Profils Basilisk durcis, un par site (jetable en navigation privée), communs avec l'édition Windows. Basilisk se ferme avec son onglet, et avec PommeBrowser même en cas d'arrêt brutal.

**Sécurité**
- HTTPS automatique, avec une page claire quand un site ne propose pas HTTPS.
- Protection contre le pistage (ITP) et cookies tiers bloqués.
- Autorisations demandées par les sites (position, caméra, micro, notifications…), mémorisées par site.
- Certificats non reconnus du homelab : vous pouvez les approuver, et PommeBrowser vous prévient s'ils changent.

**Téléchargements**
- Enregistrés dans le dossier Téléchargements, sans jamais écraser un fichier existant.

**Mises à jour**
- Au démarrage, PommeBrowser cherche une nouvelle version sur le dépôt des versions (le même que l'édition Windows).
- L'AppImage est téléchargée à côté de l'ancienne, vérifiée (taille, empreinte SHA-256 publiée avec la version, format AppImage), puis la remplace d'un seul coup. Elle s'applique au redémarrage.
- Réglage et bouton « Vérifier » dans les préférences. Si le dossier de l'AppImage n'est pas modifiable, PommeBrowser indique seulement la nouvelle version.

**Signaler un problème**
- Même rapport que l'édition Windows, envoyé au serveur de support s'il est configuré, sinon enregistré dans `Documents/PommeBrowser/Rapports`.
- Journal, informations système et page affichée (sans ses paramètres) joints au choix. Aucun mot de passe ni cookie.

**Autres**
- Recherche dans la page, zoom mémorisé par site, impression, outils de développement.
- Thème clair, sombre ou celui du système. Interface en français ou en anglais.

## Où sont les données ?

| Emplacement | Contenu |
|---|---|
| `~/.config/MyHomelabBrowser/` | Apparence et langue, liste des profils (`profiles.json`, mots de passe hachés) et dernier profil ouvert |
| `~/.config/MyHomelabBrowser/profiles/default/` | Profil par défaut : réglages (`settings-linux.json`), favoris, services, espaces de travail, coffre (`vault.json.enc`), autorisations, certificats approuvés, zoom, anti-pub, règles Flash |
| `~/.config/MyHomelabBrowser/profiles/<nom>/` | Même contenu, pour un profil créé |
| `~/.local/share/pommebrowser/` | Profil par défaut : historique (`history.db`), cookies et stockage des sites, session, profils Basilisk (`basilisk/`) |
| `~/.local/share/pommebrowser/profiles/<nom>/` | Même contenu, pour un profil créé |
| `~/.local/share/pommebrowser/plugins/` | Module Flash pour Basilisk (`libflashplayer.so`) |
| `~/.cache/pommebrowser/` | Cache web, règles anti-pub compilées (`profiles/<nom>/` pour un profil créé) |

Les fichiers de `profiles/<nom>/` ont le même format que sous Windows. Vous pouvez copier `favorites.json`, `services.json`, `workspaces.json` ou `vault.json.enc` d'un système à l'autre.

## Compiler

Il faut le SDK .NET 10 et les bibliothèques ci-dessus.

```bash
dotnet run --project linux                      # lancer depuis les sources
linux/packaging/build-appimage.sh               # AppImage x86_64
linux/packaging/build-appimage.sh all           # x86_64 et aarch64
linux/packaging/smoke-test.sh linux/packaging/out/PommeBrowser-*-x86_64.AppImage
linux/packaging/publish-release.sh              # AppImage ajoutées à la version GitHub
```

Le script télécharge `appimagetool` et le runtime AppImage dans une version fixée et vérifie leur empreinte SHA-256. Ruffle est téléchargé et vérifié de la même façon ; le fichier `build/Ruffle.targets` est commun aux deux éditions.

**Publier une version** : publiez d'abord l'édition Windows (`build-pack-velopack.ps1`), puis lancez `publish-release.sh` (GitHub CLI connecté). Les AppImage et leurs fichiers `.sha256` sont ajoutés à la version du même numéro sur `vazer7070/PommeBrowser-release` : c'est là que PommeBrowser cherche ses mises à jour.

**Serveur de support** : comme pour l'édition Windows, `-p:SupportApiUrl=https://…` (ou la variable `POMMEBROWSER_SUPPORT_API_URL`) à la compilation. Sans adresse, les rapports sont enregistrés localement.

Les tests de la logique sans interface (conversion des listes anti-pub, barre d'adresse, favoris, historique, session, profils, relance, mise à jour de l'AppImage, Basilisk, rapports, scripts du coffre…) font partie de `tests/PommeBrowser.Tests` :

```bash
dotnet test tests/PommeBrowser.Tests
```

## Organisation du code

| Dossier | Contenu |
|---|---|
| `Core/` | Logique sans interface, testée : conversion des listes anti-pub en règles WebKit, propositions de la barre d'adresse, favoris, historique, session, réglages, emplacements XDG, dossiers des profils, relance, mise à jour de l'AppImage, détection de Basilisk, rapport de support |
| `Web/` | Moteur : sessions réseau, bloqueur, Ruffle (schéma `pomme-ruffle://`), capture des identifiants, téléchargements, Basilisk, mise à jour, appels directs à WebKitGTK |
| `Ui/` | Interface GTK 4 / libadwaita : fenêtres, onglets, vue côte à côte, espaces de travail, barre d'adresse, pages internes (dont « Mots de passe »), profils, coffre, préférences, rapport |
| `packaging/` | AppImage : lanceur, `.desktop`, métadonnées AppStream, icônes, scripts |

Le reste du code partagé vient de `../classes/`, lié dans le projet et non copié.
