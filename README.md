# PommeBrowser

Navigateur pour homelab : services surveillés, coffre des mots de passe avec codes de double authentification, anti-pub, espaces de travail et lecture des contenus Flash (Ruffle, et Basilisk avec le lecteur d'origine).

## Édition maintenue : Avalonia

L'**[édition Avalonia](avalonia/README.md)** (`avalonia/`) est la seule édition maintenue. Elle fonctionne sous Windows, macOS et Linux et reprend les profils, favoris, coffre et réglages des éditions précédentes.

| Dossier | Contenu |
|---|---|
| `avalonia/` | Édition maintenue (Windows, macOS, Linux) |
| `classes/` | Code commun : réglages, profils, coffre, anti-pub, Flash, homelab… |
| `linux/Core/` | Code commun (chemins, historique, favoris), aussi utilisé par l'édition Avalonia |
| `legacy-engine/` | Pomme Legacy : Basilisk compilé depuis ses sources, pour le lecteur Flash d'origine |
| `support-server/` | Serveur qui reçoit les rapports « Signaler un problème » |
| `tests/` | Tests automatiques |

## Éditions gelées

L'édition **WPF** (Windows, fichiers à la racine : `MyHomelabBrowser.csproj`, `MainWindow*.cs`, `controles/`…) et l'édition **GTK** (`linux/`, hors `linux/Core/`) ne sont plus mises à jour ni construites par la CI. Leur code reste dans le dépôt pour référence.
