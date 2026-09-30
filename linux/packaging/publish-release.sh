#!/usr/bin/env bash
# Publie les AppImage dans la version GitHub du même numéro, sur le dépôt des versions de
# l'édition Windows : PommeBrowser pour Linux y cherche ses mises à jour.
#
#   linux/packaging/build-appimage.sh all
#   linux/packaging/publish-release.sh
#
#   avalonia/packaging/linux/build-appimage.sh all      # édition Avalonia, à la place
#   linux/packaging/publish-release.sh --avalonia
#
# Prérequis : GitHub CLI (gh) connecté avec le droit d'écrire sur le dépôt. Publiez d'abord
# l'édition Windows (build-pack-velopack.ps1) : ce script ajoute les AppImage à sa version,
# ou la crée si elle n'existe pas encore.
set -euo pipefail

REPO="${POMMEBROWSER_RELEASE_REPO:-vazer7070/PommeBrowser-release}"
PACKAGING="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT="$PACKAGING/../PommeBrowser.Linux.csproj"
OUT="$PACKAGING/out"
if [ "${1:-}" = "--avalonia" ]; then
  PROJECT="$PACKAGING/../../avalonia/PommeBrowser.csproj"
  OUT="$PACKAGING/../../avalonia/packaging/out"
fi

command -v gh >/dev/null || { echo "GitHub CLI (gh) introuvable : https://cli.github.com" >&2; exit 1; }

VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$PROJECT" | head -n 1)"
[ -n "$VERSION" ] || { echo "Version introuvable dans $PROJECT" >&2; exit 1; }

shopt -s nullglob
images=("$OUT"/PommeBrowser-"$VERSION"-*.AppImage)
[ ${#images[@]} -gt 0 ] || { echo "Aucune AppImage $VERSION dans $OUT : lancez d'abord build-appimage.sh." >&2; exit 1; }

# Les empreintes publiées sont celles que PommeBrowser vérifie avant d'installer une mise à jour.
files=()
for image in "${images[@]}"; do
  [ -f "$image.sha256" ] || { echo "Empreinte manquante : $image.sha256" >&2; exit 1; }
  (cd "$OUT" && sha256sum --check --status "$(basename "$image").sha256") || { echo "Empreinte incorrecte pour $image" >&2; exit 1; }
  files+=("$image" "$image.sha256")
done

if ! gh release view "$VERSION" --repo "$REPO" >/dev/null 2>&1; then
  echo "Création de la version $VERSION sur $REPO…"
  gh release create "$VERSION" --repo "$REPO" --title "PommeBrowser $VERSION" --notes "PommeBrowser $VERSION"
fi

echo "Envoi de ${#images[@]} AppImage vers $REPO ($VERSION)…"
gh release upload "$VERSION" "${files[@]}" --repo "$REPO" --clobber
echo "Publié : https://github.com/$REPO/releases/tag/$VERSION"
