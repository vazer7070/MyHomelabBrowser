#!/usr/bin/env bash
# Construit PommeBrowser.app (édition Avalonia) pour macOS, et son archive .zip.
#
#   avalonia/packaging/macos/build-app.sh            # Mac à puce Apple (arm64)
#   avalonia/packaging/macos/build-app.sh x64        # Mac à processeur Intel
#   avalonia/packaging/macos/build-app.sh all        # les deux
#
# Résultat : avalonia/packaging/out/PommeBrowser-<version>-macos-<arch>.zip (+ .sha256).
#
# À lancer sur un Mac (sips, iconutil, codesign, ditto). Sans identité de signature, l'app est
# signée « ad hoc » : elle s'ouvre sur ce Mac, et ailleurs après un clic droit > Ouvrir.
# Pour la distribuer largement :
#   POMMEBROWSER_MACOS_SIGN_IDENTITY="Developer ID Application: …"   signature Apple
#   POMMEBROWSER_NOTARY_PROFILE="profil-notarytool"                   notarisation (facultative)
set -euo pipefail

PACKAGING="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$PACKAGING/../../.." && pwd)"
PROJECT="$ROOT/avalonia/PommeBrowser.csproj"
OUT="$ROOT/avalonia/packaging/out"
ICON_SOURCE="$ROOT/Assets/PommeBrowser_geek.png"
BUNDLE_ID="io.github.vazer7070.PommeBrowser"

[ "$(uname -s)" = "Darwin" ] || { echo "Ce script se lance sur macOS." >&2; exit 1; }

VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$PROJECT" | head -n 1)"
[ -n "$VERSION" ] || { echo "Version introuvable dans $PROJECT" >&2; exit 1; }

# Icône de l'app (.icns) à partir de l'image 1024 × 1024 du navigateur.
make_icon() {
  local iconset="$1/PommeBrowser.iconset"
  mkdir -p "$iconset"
  for size in 16 32 128 256 512; do
    sips -z "$size" "$size" "$ICON_SOURCE" --out "$iconset/icon_${size}x${size}.png" >/dev/null
    sips -z $((size * 2)) $((size * 2)) "$ICON_SOURCE" --out "$iconset/icon_${size}x${size}@2x.png" >/dev/null
  done
  iconutil --convert icns "$iconset" --output "$1/PommeBrowser.icns"
}

build() {
  local arch="$1"
  local rid
  case "$arch" in
    arm64) rid="osx-arm64" ;;
    x64) rid="osx-x64" ;;
    *) echo "Architecture non prise en charge : $arch (arm64 ou x64)" >&2; exit 1 ;;
  esac

  local work="$OUT/work-macos-$arch"
  local app="$work/PommeBrowser.app"
  rm -rf "$work"
  mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"

  echo "Publication de PommeBrowser $VERSION ($rid)…"
  dotnet publish "$PROJECT" \
    --configuration Release \
    --runtime "$rid" \
    --self-contained true \
    -p:UseAppHost=true \
    -p:DebugType=none \
    -p:RuffleDownloadOptional=false \
    --output "$app/Contents/MacOS"

  [ -f "$app/Contents/MacOS/Assets/Ruffle/ruffle.js" ] || { echo "Ruffle absent de la publication." >&2; exit 1; }

  make_icon "$work"
  cp "$work/PommeBrowser.icns" "$app/Contents/Resources/PommeBrowser.icns"
  sed -e "s/__VERSION__/$VERSION/g" -e "s/__BUNDLE_ID__/$BUNDLE_ID/g" "$PACKAGING/Info.plist" > "$app/Contents/Info.plist"
  printf 'APPL????' > "$app/Contents/PkgInfo"

  if [ -n "${POMMEBROWSER_MACOS_SIGN_IDENTITY:-}" ]; then
    echo "Signature (Developer ID)…"
    # Les bibliothèques d'abord, puis l'app : runtime renforcé, avec les droits dont .NET a besoin.
    find "$app/Contents/MacOS" -type f \( -name "*.dylib" -o -perm -u+x \) -print0 |
      xargs -0 codesign --force --timestamp --options runtime \
        --entitlements "$PACKAGING/PommeBrowser.entitlements" --sign "$POMMEBROWSER_MACOS_SIGN_IDENTITY"
    codesign --force --timestamp --options runtime \
      --entitlements "$PACKAGING/PommeBrowser.entitlements" --sign "$POMMEBROWSER_MACOS_SIGN_IDENTITY" "$app"
  else
    echo "Signature ad hoc (aucune identité Developer ID fournie)…"
    codesign --force --deep --sign - "$app"
  fi
  codesign --verify --deep --strict "$app"

  local target="$OUT/PommeBrowser-$VERSION-macos-$arch.zip"
  rm -f "$target"
  ditto -c -k --sequesterRsrc --keepParent "$app" "$target"

  if [ -n "${POMMEBROWSER_MACOS_SIGN_IDENTITY:-}" ] && [ -n "${POMMEBROWSER_NOTARY_PROFILE:-}" ]; then
    echo "Notarisation…"
    xcrun notarytool submit "$target" --keychain-profile "$POMMEBROWSER_NOTARY_PROFILE" --wait
    xcrun stapler staple "$app"
    rm -f "$target"
    ditto -c -k --sequesterRsrc --keepParent "$app" "$target"
  fi

  (cd "$OUT" && shasum -a 256 "$(basename "$target")" > "$(basename "$target").sha256")
  rm -rf "$work"
  echo "Prêt : $target"
}

case "${1:-arm64}" in
  all) build arm64; build x64 ;;
  arm64|aarch64) build arm64 ;;
  x64|x86_64|intel) build x64 ;;
  *) echo "Usage : $0 [arm64|x64|all]" >&2; exit 1 ;;
esac
