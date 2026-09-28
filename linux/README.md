# PommeBrowser pour Linux

Édition Linux de PommeBrowser, distribuée en **AppImage** : un seul fichier, sans installation.

Elle partage avec l'édition Windows la logique du navigateur :
- l'historique ;
- les favoris ;
- les services du homelab ;
- l'anti-pub ;
- l'HTTPS automatique ;
- les certificats approuvés ;
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

## Fonctions

**Navigation**
- Onglets réorganisables et épinglables. `Ctrl+Maj+T` rouvre le dernier fermé.
- Reprise de la session au démarrage.
- Fenêtre privée (`Ctrl+Maj+N`).

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

**Sécurité**
- HTTPS automatique, avec une page claire quand un site ne propose pas HTTPS.
- Protection contre le pistage (ITP) et cookies tiers bloqués.
- Autorisations demandées par les sites (position, caméra, micro, notifications…), mémorisées par site.
- Certificats non reconnus du homelab : vous pouvez les approuver, et PommeBrowser vous prévient s'ils changent.

**Téléchargements**
- Enregistrés dans le dossier Téléchargements, sans jamais écraser un fichier existant.

**Autres**
- Recherche dans la page, zoom mémorisé par site, impression, outils de développement.
- Thème clair, sombre ou celui du système. Interface en français ou en anglais.

### Pas encore dans l'édition Linux

- Basilisk (Flash d'origine) : c'est un programme Windows. Ruffle lit la grande majorité des contenus.
- Profils protégés par mot de passe, coffre à identifiants, codes TOTP.
- Espaces de travail et vue côte à côte.
- Formulaire « Signaler un problème ».
- Mise à jour automatique : remplacez l'AppImage par la nouvelle version.

## Où sont les données ?

| Emplacement | Contenu |
|---|---|
| `~/.config/MyHomelabBrowser/` | Apparence et langue |
| `~/.config/MyHomelabBrowser/profiles/default/` | Réglages (`settings-linux.json`), favoris, services, autorisations, certificats approuvés, zoom, anti-pub |
| `~/.local/share/pommebrowser/` | Historique (`history.db`), cookies et stockage des sites, session |
| `~/.cache/pommebrowser/` | Cache web, règles anti-pub compilées |

Les fichiers de `profiles/<nom>/` ont le même format que sous Windows. Vous pouvez copier `favorites.json` et `services.json` d'un système à l'autre.

## Compiler

Il faut le SDK .NET 10 et les bibliothèques ci-dessus.

```bash
dotnet run --project linux                      # lancer depuis les sources
linux/packaging/build-appimage.sh               # AppImage x86_64
linux/packaging/build-appimage.sh all           # x86_64 et aarch64
linux/packaging/smoke-test.sh linux/packaging/out/PommeBrowser-*-x86_64.AppImage
```

Le script télécharge `appimagetool` et le runtime AppImage dans une version fixée et vérifie leur empreinte SHA-256. Ruffle est téléchargé et vérifié de la même façon ; le fichier `build/Ruffle.targets` est commun aux deux éditions.

Les tests de la logique sans interface (conversion des listes anti-pub, barre d'adresse, favoris, historique, session…) font partie de `tests/PommeBrowser.Tests` :

```bash
dotnet test tests/PommeBrowser.Tests
```

## Organisation du code

| Dossier | Contenu |
|---|---|
| `Core/` | Logique sans interface, testée : conversion des listes anti-pub en règles WebKit, propositions de la barre d'adresse, favoris, historique, session, réglages, emplacements XDG |
| `Web/` | Moteur : sessions réseau, bloqueur, Ruffle (schéma `pomme-ruffle://`), téléchargements, appels directs à WebKitGTK |
| `Ui/` | Interface GTK 4 / libadwaita : fenêtres, onglets, barre d'adresse, pages internes, préférences |
| `packaging/` | AppImage : lanceur, `.desktop`, métadonnées AppStream, icônes, scripts |

Le reste du code partagé vient de `../classes/`, lié dans le projet et non copié.
