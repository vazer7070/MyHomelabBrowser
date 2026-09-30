#!/usr/bin/env bash
# Place Pomme Legacy (moteur Flash d'origine) dans un paquet de PommeBrowser : archive de la
# version GitHub legacy-engine-<version> (voir .github/workflows/legacy-engine.yml), vérifiée
# par SHA-256, décompressée dans le dossier indiqué (legacy/ à côté de l'exécutable).
#
#   legacy-engine/fetch-engine.sh linux-x86_64 <dossier legacy>
#
# Sans version publiée, le paquet est construit sans moteur (avertissement) ; avec
# LEGACY_ENGINE_REQUIRED=1, c'est une erreur. LEGACY_ENGINE_ARCHIVE=<fichier> utilise une
# archive locale (compilation faite à la main) au lieu de la télécharger.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=engine.env
source "$HERE/engine.env"
PLATFORM="${1:?Plate-forme (linux-x86_64)}"
DEST="${2:?Dossier de destination}"
REPO="${LEGACY_ENGINE_REPO:-vazer7070/MyHomelabBrowser}"
TAG="legacy-engine-$BASILISK_VERSION"
NAME="pomme-legacy-$BASILISK_VERSION-$PLATFORM.tar.xz"
CACHE="${LEGACY_ENGINE_CACHE:-$HERE/out}"

missing() {
  if [ "${LEGACY_ENGINE_REQUIRED:-0}" = 1 ]; then
    echo "Pomme Legacy $BASILISK_VERSION ($PLATFORM) indisponible : $1" >&2
    exit 1
  fi
  echo "Avertissement : paquet sans Pomme Legacy ($1). Basilisk devra être installé à part." >&2
  exit 0
}

mkdir -p "$CACHE"
archive="$CACHE/$NAME"
if [ -n "${LEGACY_ENGINE_ARCHIVE:-}" ]; then
  archive="$LEGACY_ENGINE_ARCHIVE"
  [ -f "$archive.sha256" ] || missing "empreinte $archive.sha256 absente"
  expected="$(cut -d' ' -f1 < "$archive.sha256")"
elif [ ! -f "$archive" ] || [ ! -f "$archive.sha256" ]; then
  echo "Téléchargement de $NAME ($TAG)…"
  rm -f "$archive" "$archive.sha256"
  if command -v gh >/dev/null && gh auth status >/dev/null 2>&1; then
    gh release download "$TAG" --repo "$REPO" --dir "$CACHE" --pattern "$NAME" --pattern "$NAME.sha256" 2>/dev/null ||
      missing "version $TAG introuvable sur $REPO"
  else
    for file in "$NAME" "$NAME.sha256"; do
      curl --fail --location --retry 3 --silent --show-error --output "$CACHE/$file" \
        "https://github.com/$REPO/releases/download/$TAG/$file" 2>/dev/null ||
        { rm -f "$archive" "$archive.sha256"; missing "version $TAG introuvable sur $REPO"; }
    done
  fi
fi
expected="${expected:-$(cut -d' ' -f1 < "$archive.sha256")}"

if [ "$(sha256sum "$archive" | cut -d' ' -f1)" != "$expected" ]; then
  echo "Empreinte SHA-256 inattendue pour $archive." >&2
  exit 1
fi

rm -rf "$DEST"
mkdir -p "$DEST"
tar -xJf "$archive" -C "$DEST"
[ -x "$DEST/basilisk" ] || { echo "Exécutable basilisk absent de $archive." >&2; exit 1; }
echo "Pomme Legacy $BASILISK_VERSION placé dans $DEST"
