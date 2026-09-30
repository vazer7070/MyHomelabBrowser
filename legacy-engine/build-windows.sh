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
# le redistribuable). Dossiers donnés par l'environnement de Visual Studio (vcvars64) ; le
# shell MSYS de MozillaBuild 3.4 met les noms des variables en majuscules (VCTOOLSREDISTDIR).
redist="${VCTOOLSREDISTDIR:-${VCToolsRedistDir:-}}"
sdk="${WINDOWSSDKDIR:-${WindowsSdkDir:-}}"
crt=""
[ -z "$redist" ] || crt="$(find "$(unix_path "$redist")/x64" -maxdepth 1 -type d -name 'Microsoft.VC*.CRT' 2>/dev/null | head -n 1 || true)"
[ -n "$crt" ] || { echo "Bibliothèques d'exécution de Visual C++ introuvables (VCToolsRedistDir : ${redist:-absent}). Lancer depuis l'environnement de vcvars64." >&2; exit 1; }
echo "WIN32_REDIST_DIR=\"$(mixed_path "$crt")\"" >> "$SRC/.mozconfig"
# Le C universel fait partie de Windows 10 et 11 : joint s'il est trouvé, pour les systèmes plus anciens.
ucrt=""
if [ -n "$sdk" ]; then
  ucrt="$(ls -d "$(unix_path "$sdk")"/Redist/*/ucrt/DLLs/x64 2>/dev/null | sort | tail -n 1 || true)"
  [ -n "$ucrt" ] || ucrt="$(ls -d "$(unix_path "$sdk")"/Redist/ucrt/DLLs/x64 2>/dev/null || true)"
fi
[ -z "$ucrt" ] || echo "WIN_UCRT_REDIST_DIR=\"$(mixed_path "$ucrt")\"" >> "$SRC/.mozconfig"
echo "Réglages de compilation :"
cat "$SRC/.mozconfig"
cd "$SRC"

export MOZ_MAKE_FLAGS="-j${NUMBER_OF_PROCESSORS:-4}"
./mach build
./mach package

# « mach package » range le paquet dans obj-pomme/dist/basilisk, puis le compresse en .7z :
# l'archive livrée reprend ce dossier, en .zip (décompressé par PowerShell sans outil).
stage="obj-pomme/dist/basilisk"
[ -f "$stage/basilisk.exe" ] || { echo "Paquet de Pomme Legacy introuvable ($stage)." >&2; ls obj-pomme/dist >&2; exit 1; }
echo "Paquet : $stage"

mkdir -p "$OUT"
name="pomme-legacy-$BASILISK_VERSION-windows-x64.zip"
python3 - "$stage" "$OUT/$name" <<'PY'
import os, sys, zipfile
stage, target = sys.argv[1], sys.argv[2]
# Sans les bibliothèques de Visual C++, basilisk.exe ne démarre pas là où le redistribuable manque.
missing = [f for f in ("basilisk.exe", "xul.dll", "vcruntime140.dll", "msvcp140.dll") if not os.path.isfile(os.path.join(stage, f))]
if missing:
    sys.exit("Absent du paquet : " + ", ".join(missing))
with zipfile.ZipFile(target, "w", zipfile.ZIP_DEFLATED) as archive:
    for root, dirs, files in os.walk(stage):
        dirs.sort()
        for file in sorted(files):
            path = os.path.join(root, file)
            archive.write(path, os.path.relpath(path, stage).replace(os.sep, "/"))
    print(len(archive.namelist()), "fichiers")
PY
python3 - "$OUT/$name" <<'PY'
import hashlib, os, sys
path = sys.argv[1]
digest = hashlib.sha256(open(path, "rb").read()).hexdigest()
open(path + ".sha256", "w", newline="\n").write(f"{digest}  {os.path.basename(path)}\n")
print(digest, os.path.basename(path))
PY
