#!/usr/bin/env bash
# Compile Pomme Legacy pour Windows x64 et en fait l'archive livrée avec PommeBrowser.
# À lancer dans le shell de MozillaBuild, avec l'environnement de Visual Studio (vcvars64).
#
#   legacy-engine/build-windows.sh <sources> <dossier de sortie>
#
# Résultat : pomme-legacy-<version>-windows-x64.zip (+ .sha256), contenu du dossier legacy\
# de l'application (basilisk.exe, bibliothèques, omni.ja…).
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=engine.env
source "$HERE/engine.env"
SRC="$(cd "${1:?Dossier des sources}" && pwd)"
OUT="${2:?Dossier de sortie}"

"$HERE/brand.sh" "$SRC"
cp "$HERE/mozconfig-windows" "$SRC/.mozconfig"
# Bibliothèques d'exécution de Visual C++ et du C universel jointes au paquet (Windows sans
# le redistribuable). Dossiers donnés par l'environnement de Visual Studio (vcvars64).
if [ -n "${VCToolsRedistDir:-}" ]; then
  crt="$(find "$(cygpath -u "$VCToolsRedistDir")x64" -maxdepth 1 -type d -name 'Microsoft.VC*.CRT' | head -n 1)"
  [ -n "$crt" ] && echo "WIN32_REDIST_DIR=\"$(cygpath -m "$crt")\"" >> "$SRC/.mozconfig"
fi
if [ -n "${WindowsSdkDir:-}" ]; then
  ucrt="$(ls -d "$(cygpath -u "$WindowsSdkDir")"Redist/*/ucrt/DLLs/x64 2>/dev/null | sort -V | tail -n 1)"
  [ -n "$ucrt" ] && echo "WIN_UCRT_REDIST_DIR=\"$(cygpath -m "$ucrt")\"" >> "$SRC/.mozconfig"
fi
echo "Réglages de compilation :"
cat "$SRC/.mozconfig"
cd "$SRC"

export MOZ_MAKE_FLAGS="-j$(nproc)"
./mach build
./mach package

archive="$(find obj-pomme/dist -maxdepth 1 -name '*.zip' ! -name '*symbols*' ! -name '*tests*' | head -n 1)"
[ -n "$archive" ] || { echo "Archive de Pomme Legacy introuvable dans obj-pomme/dist." >&2; ls obj-pomme/dist >&2; exit 1; }
echo "Paquet : $archive"

mkdir -p "$OUT"
name="pomme-legacy-$BASILISK_VERSION-windows-x64.zip"
python3 - "$archive" "$OUT/$name" <<'PY'
import sys, zipfile
# Le paquet contient un dossier racine (basilisk/) : ses fichiers passent à la racine de l'archive.
with zipfile.ZipFile(sys.argv[1]) as source, zipfile.ZipFile(sys.argv[2], "w", zipfile.ZIP_DEFLATED) as target:
    names = [n for n in source.namelist() if not n.endswith("/")]
    root = names[0].split("/", 1)[0] + "/"
    if not any(n == root + "basilisk.exe" for n in names):
        sys.exit("basilisk.exe absent du paquet")
    for name in names:
        target.writestr(name[len(root):], source.read(name))
PY
(cd "$OUT" && sha256sum "$name" > "$name.sha256")
cat "$OUT/$name.sha256"
