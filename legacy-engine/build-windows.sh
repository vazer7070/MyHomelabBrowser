#!/usr/bin/env bash
# Compile Pomme Legacy pour Windows x64 et en fait l'archive livrée avec PommeBrowser.
# À lancer dans le shell de MozillaBuild 3.4 (celui qu'attend UXP), avec l'environnement de
# Visual Studio (vcvars64).
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
# Dossier de sortie en chemin absolu : la compilation se fait depuis le dossier des sources.
mkdir -p "${2:?Dossier de sortie}"
OUT="$(cd "$2" && pwd)"

"$HERE/brand.sh" "$SRC"
cp "$HERE/mozconfig-windows" "$SRC/.mozconfig"
# Chemins Windows (C:\x\y) en chemins du shell (/c/x/y) et en chemins mixtes (C:/x/y) :
# le shell MSYS de MozillaBuild 3.4 n'a pas cygpath.
unix_path() { echo "$1" | sed -e 's#\\#/#g' -e 's#^\([A-Za-z]\):#/\L\1#'; }
mixed_path() { echo "$1" | sed -e 's#\\#/#g' -e 's#^/\([A-Za-z]\)/#\U\1:/#'; }

# Bibliothèques d'exécution de Visual C++ et du C universel jointes au paquet (Windows sans
# le redistribuable). Dossiers donnés par l'environnement de Visual Studio (vcvars64).
if [ -n "${VCToolsRedistDir:-}" ]; then
  crt="$(find "$(unix_path "$VCToolsRedistDir")/x64" -maxdepth 1 -type d -name 'Microsoft.VC*.CRT' | head -n 1)"
  [ -n "$crt" ] && echo "WIN32_REDIST_DIR=\"$(mixed_path "$crt")\"" >> "$SRC/.mozconfig"
fi
if [ -n "${WindowsSdkDir:-}" ]; then
  ucrt="$(ls -d "$(unix_path "$WindowsSdkDir")"/Redist/*/ucrt/DLLs/x64 2>/dev/null | sort | tail -n 1)"
  [ -n "$ucrt" ] && echo "WIN_UCRT_REDIST_DIR=\"$(mixed_path "$ucrt")\"" >> "$SRC/.mozconfig"
fi
echo "Réglages de compilation :"
cat "$SRC/.mozconfig"
cd "$SRC"

export MOZ_MAKE_FLAGS="-j${NUMBER_OF_PROCESSORS:-4}"
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
python3 - "$OUT/$name" <<'PY'
import hashlib, os, sys
path = sys.argv[1]
digest = hashlib.sha256(open(path, "rb").read()).hexdigest()
open(path + ".sha256", "w", newline="\n").write(f"{digest}  {os.path.basename(path)}\n")
print(digest, os.path.basename(path))
PY
