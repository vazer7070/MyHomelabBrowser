#!/usr/bin/env bash
# Récupère les sources de Basilisk et de la plateforme UXP (sous-module platform/) au commit
# fixé dans engine.env.
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

echo "Basilisk $BASILISK_VERSION ($BASILISK_COMMIT)"
git fetch --quiet --depth 1 origin "$BASILISK_COMMIT"
git -c advice.detachedHead=false checkout --quiet --force FETCH_HEAD

# Seul platform/ est nécessaire (les autres sous-modules servent à d'autres compilations).
git -c protocol.version=2 submodule update --init --depth 1 platform
git submodule status platform
