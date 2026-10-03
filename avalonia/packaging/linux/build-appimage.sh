#!/usr/bin/env bash
# Construit l'AppImage de PommeBrowser (édition Avalonia) pour Linux.
#
#   avalonia/packaging/linux/build-appimage.sh            # x86_64
#   avalonia/packaging/linux/build-appimage.sh aarch64    # ARM 64 bits (Raspberry Pi 4/5…)
#   avalonia/packaging/linux/build-appimage.sh all        # les deux
#
# Résultat : avalonia/packaging/out/PommeBrowser-<version>-<arch>.AppImage (+ .sha256).
# Même nom et même identifiant que l'AppImage de l'édition GTK : publiée à sa place, elle
# arrive chez ses utilisateurs comme une mise à jour, avec leurs données.
#
# L'AppImage contient le navigateur, .NET, Avalonia, Ruffle et, en x86_64, le moteur Flash
# d'origine (Pomme Legacy, voir legacy-engine/). Le moteur web (WebKitGTK 4.1)
# et GTK 3 viennent du système : ils reçoivent ainsi les correctifs de sécurité de la
# distribution. Les outils AppImage sont téléchargés dans une version fixée et vérifiés
# par SHA-256 avant d'être utilisés.
set -euo pipefail

APPIMAGETOOL_VERSION="1.9.1"
RUNTIME_VERSION="20251108"
declare -A APPIMAGETOOL_SHA256=(
  [x86_64]="ed4ce84f0d9caff66f50bcca6ff6f35aae54ce8135408b3fa33abfc3cb384eb0"
  [aarch64]="f0837e7448a0c1e4e650a93bb3e85802546e60654ef287576f46c71c126a9158"
)
declare -A RUNTIME_SHA256=(
  [x86_64]="2fca8b443c92510f1483a883f60061ad09b46b978b2631c807cd873a47ec260d"
  [aarch64]="00cbdfcf917cc6c0ff6d3347d59e0ca1f7f45a6df1a428a0d6d8a78664d87444"
)
declare -A RUNTIME_IDS=([x86_64]="linux-x64" [aarch64]="linux-arm64")

APP_ID="io.github.vazer7070.PommeBrowser"
PACKAGING="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$PACKAGING/../../.." && pwd)"
PROJECT="$ROOT/avalonia/PommeBrowser.csproj"
# Fichiers de bureau (lanceur, métadonnées, icônes) : ceux de l'édition GTK, même identifiant.
DESKTOP="$ROOT/linux/packaging"
OUT="$ROOT/avalonia/packaging/out"
TOOLS="$OUT/tools"

VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$PROJECT" | head -n 1)"
[ -n "$VERSION" ] || { echo "Version introuvable dans $PROJECT" >&2; exit 1; }

# Télécharge un fichier une fois, puis vérifie son empreinte à chaque utilisation.
fetch() {
  local url="$1" file="$2" sha="$3"
  if [ ! -f "$file" ]; then
    echo "Téléchargement de $(basename "$file")…"
    curl --fail --location --retry 3 --silent --show-error --output "$file.part" "$url"
    mv "$file.part" "$file"
  fi
  if ! echo "$sha  $file" | sha256sum --check --status; then
    echo "Empreinte SHA-256 inattendue pour $file : fichier supprimé." >&2
    rm -f "$file"
    exit 1
  fi
}

build() {
  local arch="$1"
  local rid="${RUNTIME_IDS[$arch]:-}"
  [ -n "$rid" ] || { echo "Architecture non prise en charge : $arch (x86_64 ou aarch64)" >&2; exit 1; }

  local host_arch
  host_arch="$(uname -m)"
  [ -n "${APPIMAGETOOL_SHA256[$host_arch]:-}" ] || { echo "Construction impossible depuis $host_arch" >&2; exit 1; }

  mkdir -p "$TOOLS"
  local tool="$TOOLS/appimagetool-$APPIMAGETOOL_VERSION-$host_arch.AppImage"
  local runtime="$TOOLS/runtime-$RUNTIME_VERSION-$arch"
  fetch "https://github.com/AppImage/appimagetool/releases/download/$APPIMAGETOOL_VERSION/appimagetool-$host_arch.AppImage" \
        "$tool" "${APPIMAGETOOL_SHA256[$host_arch]}"
  fetch "https://github.com/AppImage/type2-runtime/releases/download/$RUNTIME_VERSION/runtime-$arch" \
        "$runtime" "${RUNTIME_SHA256[$arch]}"
  chmod +x "$tool"

  local appdir="$OUT/work-$arch/PommeBrowser.AppDir"
  rm -rf "$OUT/work-$arch"
  mkdir -p "$appdir/usr/lib/pommebrowser" "$appdir/usr/share/applications" "$appdir/usr/share/metainfo"

  echo "Publication de PommeBrowser $VERSION ($rid)…"
  dotnet publish "$PROJECT" \
    --configuration Release \
    --runtime "$rid" \
    --self-contained true \
    -p:PublishReadyToRun=true \
    -p:DebugType=none \
    -p:RuffleDownloadOptional=false \
    --output "$appdir/usr/lib/pommebrowser"

  [ -f "$appdir/usr/lib/pommebrowser/Assets/Ruffle/ruffle.js" ] || { echo "Ruffle absent de la publication." >&2; exit 1; }

  # Moteur Flash d'origine (Pomme Legacy), compilé pour x86_64 seulement : Flash Player n'a
  # jamais existé pour les processeurs ARM.
  if [ "$arch" = x86_64 ]; then
    "$ROOT/legacy-engine/fetch-engine.sh" linux-x86_64 "$appdir/usr/lib/pommebrowser/legacy"
  fi

  # Moteur Flash intégré (PommeFlashHost, voir flash-engine/) : hôte NPAPI de Linux, x86_64
  # seulement, repris d'une publication déjà faite (POMMEFLASH_HOST_DIR, comme dans la CI) ou
  # compilé ici en code natif (NativeAOT, clang requis).
  if [ "$arch" = x86_64 ]; then
    local flash="$appdir/usr/lib/pommebrowser/flash"
    if [ -n "${POMMEFLASH_HOST_DIR:-}" ] && [ -x "$POMMEFLASH_HOST_DIR/PommeFlashHost" ]; then
      install -D -m 0755 "$POMMEFLASH_HOST_DIR/PommeFlashHost" "$flash/PommeFlashHost"
    elif command -v clang >/dev/null 2>&1; then
      echo "Publication de PommeFlashHost (linux-x64, NativeAOT)…"
      dotnet publish "$ROOT/flash-engine/PommeFlash.Host/PommeFlash.Host.csproj" \
        --configuration Release --runtime linux-x64 -p:PublishAot=true -p:DebugType=none --output "$flash"
    else
      echo "clang absent : le moteur Flash intégré (PommeFlashHost) n'est pas inclus dans l'AppImage." >&2
    fi
  fi

  install -m 0755 "$PACKAGING/AppRun" "$appdir/AppRun"
  install -m 0644 "$DESKTOP/$APP_ID.desktop" "$appdir/$APP_ID.desktop"
  install -m 0644 "$DESKTOP/$APP_ID.desktop" "$appdir/usr/share/applications/$APP_ID.desktop"
  install -m 0644 "$DESKTOP/$APP_ID.metainfo.xml" "$appdir/usr/share/metainfo/$APP_ID.metainfo.xml"
  cp -r "$DESKTOP/icons" "$appdir/usr/share/"
  install -m 0644 "$DESKTOP/icons/hicolor/256x256/apps/$APP_ID.png" "$appdir/$APP_ID.png"
  ln -sf "$APP_ID.png" "$appdir/.DirIcon"

  local target="$OUT/PommeBrowser-$VERSION-$arch.AppImage"
  rm -f "$target"
  echo "Création de $(basename "$target")…"
  # APPIMAGE_EXTRACT_AND_RUN : appimagetool fonctionne aussi sans FUSE (conteneurs, CI).
  ARCH="$arch" APPIMAGE_EXTRACT_AND_RUN=1 "$tool" --no-appstream --runtime-file "$runtime" "$appdir" "$target"

  (cd "$OUT" && sha256sum "$(basename "$target")" > "$(basename "$target").sha256")
  rm -rf "$OUT/work-$arch"
  echo "Prêt : $target"
}

case "${1:-x86_64}" in
  all) build x86_64; build aarch64 ;;
  x86_64|amd64|x64) build x86_64 ;;
  aarch64|arm64) build aarch64 ;;
  *) echo "Usage : $0 [x86_64|aarch64|all]" >&2; exit 1 ;;
esac
