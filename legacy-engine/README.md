# Pomme Legacy : le moteur Flash d'origine de PommeBrowser

Ruffle lit la plupart des contenus Flash directement dans la page. Pour les autres, PommeBrowser ouvre la page dans **Basilisk**, un navigateur de la famille Firefox qui accepte encore le module Flash Player d'origine (NPAPI). La page s'affiche dans l'onglet, sans barre d'outils.

Basilisk est livré avec PommeBrowser (édition Avalonia, Windows x64 et Linux x86_64) : rien à installer.

## Pourquoi « Pomme Legacy »

Le code de Basilisk est libre (MPL 2.0), mais son nom et ses logos sont réservés à ses compilations officielles : une compilation modifiée ou livrée dans un autre logiciel doit porter un autre nom. PommeBrowser compile donc Basilisk depuis ses sources, à une version fixée, avec sa propre marque : **Pomme Legacy**.

- Sources : miroir GitHub de l'équipe Basilisk, [Basilisk-Development-Team/basilisk-mirror](https://github.com/Basilisk-Development-Team/basilisk-mirror) (branche `release`), et la plateforme UXP ([UXP-mirror](https://github.com/Basilisk-Development-Team/UXP-mirror), sous-module `platform/`). Dépôt d'origine : [repo.palemoon.org/Basilisk-Dev/Basilisk](https://repo.palemoon.org/Basilisk-Dev/Basilisk). Licence MPL 2.0.
- Seul changement : la marque (copie de la marque « non officielle » des sources, renommée, voir `brand.sh`). Mise à jour intégrée désactivée : c'est PommeBrowser qui livre le moteur.

## Flash Player

Adobe interdit de redistribuer Flash Player et ne le propose plus au téléchargement. Pomme Legacy est donc livré **sans Flash**. Chacun ajoute sa propre copie dans **Paramètres › Avancé › Choisir le module Flash…** :

| Système | Fichier |
|---|---|
| Linux x86_64 | `libflashplayer.so` (version « NPAPI ») |
| Windows x64 | `NPSWF64_….dll` (version 64 bits « NPAPI », pour Firefox) |

Les dernières versions de Flash Player bloquent les contenus depuis le 12 janvier 2021 : il faut une version plus ancienne.

Le module est rangé dans les données communes aux profils (`plugins/`), que Basilisk reçoit par `MOZ_PLUGIN_PATH` (Windows et Linux).

Sous Linux, le moteur utilise les bibliothèques du système, comme le reste de l'AppImage. Pour Flash, il faut aussi GTK 2 (le module Flash en dépend) :

```bash
sudo apt install libdbus-glib-1-2 libxt6 libgtk2.0-0     # Debian, Ubuntu, Mint
sudo dnf install dbus-glib libXt gtk2                    # Fedora
sudo pacman -S --needed dbus-glib libxt gtk2             # Arch
```

## Compiler et publier

Tout se fait dans la CI (`.github/workflows/legacy-engine.yml`), qui compile le moteur quand ce dossier change :

1. `fetch-sources.sh` récupère Basilisk au commit de `engine.env` (version et commit de la branche `release` du miroir).
2. `brand.sh` crée la marque Pomme Legacy.
3. `build-linux.sh` (Ubuntu 22.04, GCC 11) et `build-windows.sh` (Visual Studio 2022 + MozillaBuild 3.4) compilent, avec les réglages des compilations officielles (`mozconfigs/` des sources), sans AV1. Ils produisent :
   - `pomme-legacy-<version>-linux-x86_64.tar.xz`
   - `pomme-legacy-<version>-windows-x64.zip`

   avec leur `.sha256`. Le moteur Linux est lancé une fois pour vérifier qu'il démarre.
4. **Actions › Moteur Flash d'origine › Run workflow**, case *publish* cochée : les archives sont publiées dans la version GitHub `legacy-engine-<version>`.

Les paquets de PommeBrowser prennent ensuite le moteur dans cette version :

- AppImage (`avalonia/packaging/linux/build-appimage.sh`) : `fetch-engine.sh`, x86_64 seulement (Flash Player n'a jamais existé pour ARM).
- Velopack (`build-pack-velopack.ps1 -Edition avalonia`) : `fetch-engine.ps1`.

L'archive est vérifiée par SHA-256 et placée dans le dossier `legacy/` de l'application. Tant qu'aucune version n'est publiée, les paquets sont construits sans moteur, avec un avertissement : `LEGACY_ENGINE_REQUIRED=1` en fait une erreur. `LEGACY_ENGINE_ARCHIVE=<fichier>` utilise une archive compilée à la main.

**Nouvelle version de Basilisk** : dans `engine.env`, changer `BASILISK_VERSION` et `BASILISK_COMMIT` (commit de la branche `release` du miroir), laisser la CI compiler, publier, puis construire les paquets.

Dans les paramètres, un Basilisk choisi à la main reste prioritaire sur le moteur livré.
