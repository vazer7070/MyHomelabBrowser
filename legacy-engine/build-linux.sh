#!/usr/bin/env bash
# Compile Pomme Legacy pour Linux x86_64 et en fait l'archive livrée avec PommeBrowser.
#
#   legacy-engine/build-linux.sh <sources> <dossier de sortie>
#
# Résultat : pomme-legacy-<version>-linux-x86_64.tar.xz (+ .sha256), contenu du dossier
# legacy/ de l'application (exécutable basilisk, bibliothèques, omni.ja…).
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=engine.env
source "$HERE/engine.env"
SRC="$(cd "${1:?Dossier des sources}" && pwd)"
# Dossier de sortie en chemin absolu : la compilation se fait depuis le dossier des sources.
mkdir -p "${2:?Dossier de sortie}"
OUT="$(cd "$2" && pwd)"

"$HERE/brand.sh" "$SRC"
cp "$HERE/mozconfig-linux" "$SRC/.mozconfig"
cd "$SRC"

export MOZ_MAKE_FLAGS="-j$(nproc)"
./mach build
./mach package

archive="$(find obj-pomme/dist -maxdepth 1 -name '*.tar.*' ! -name '*symbols*' ! -name '*tests*' | head -n 1)"
[ -n "$archive" ] || { echo "Archive de Pomme Legacy introuvable dans obj-pomme/dist." >&2; ls obj-pomme/dist >&2; exit 1; }
echo "Paquet : $archive"

work="$(mktemp -d)"
tar -xf "$archive" -C "$work"
root="$(find "$work" -mindepth 1 -maxdepth 1 -type d | head -n 1)"
[ -x "$root/basilisk" ] || { echo "Exécutable basilisk absent du paquet." >&2; ls "$root" >&2; exit 1; }

mkdir -p "$OUT"
name="pomme-legacy-$BASILISK_VERSION-linux-x86_64.tar.xz"
tar -C "$root" -cJf "$OUT/$name" .
(cd "$OUT" && sha256sum "$name" > "$name.sha256")
rm -rf "$work"
cat "$OUT/$name.sha256"
