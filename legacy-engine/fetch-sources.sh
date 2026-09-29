#!/usr/bin/env bash
# Récupère les sources de Basilisk (et de la plateforme UXP, sous-module platform/) à la
# version fixée dans engine.env.
#
#   legacy-engine/fetch-sources.sh <dossier>
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=engine.env
source "$HERE/engine.env"
SRC="${1:?Dossier des sources}"

mkdir -p "$SRC"
cd "$SRC"
[ -d .git ] || git init --quiet
git remote remove origin 2>/dev/null || true
git remote add origin "$BASILISK_REPO"

# Étiquette de la version : le nom exact varie selon les époques du projet.
tags="$(git ls-remote --tags --refs origin | sed 's:.*refs/tags/::')"
tag=""
for candidate in "v$BASILISK_VERSION" "$BASILISK_VERSION" "v${BASILISK_VERSION}_Release" "${BASILISK_VERSION}_Release" "Release-$BASILISK_VERSION"; do
  if grep -qxF "$candidate" <<<"$tags"; then
    tag="$candidate"
    break
  fi
done
if [ -z "$tag" ]; then
  echo "Version $BASILISK_VERSION introuvable. Dernières étiquettes :" >&2
  sort -V <<<"$tags" | tail -n 30 >&2
  exit 1
fi

echo "Basilisk $tag"
git fetch --quiet --depth 1 origin "refs/tags/$tag:refs/tags/$tag"
git -c advice.detachedHead=false checkout --quiet "$tag"

# Plateforme UXP : copie superficielle si le serveur l'accepte, complète sinon.
if ! git -c protocol.version=2 submodule update --init --recursive --depth 1; then
  echo "Copie superficielle refusée : copie complète des sous-modules."
  git submodule update --init --recursive
fi
git submodule status
