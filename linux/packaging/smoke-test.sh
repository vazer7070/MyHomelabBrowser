#!/usr/bin/env bash
# Essai de lancement : démarre l'AppImage sous Xvfb pendant quelques secondes, avec un
# profil vierge, et échoue si elle s'arrête ou si une exception apparaît dans le journal.
#
#   linux/packaging/smoke-test.sh linux/packaging/out/PommeBrowser-0.9.8-x86_64.AppImage
#
# Nécessite : Xvfb (xvfb-run), dbus-run-session, WebKitGTK 6.0 et libadwaita.
set -euo pipefail

APPIMAGE="$(readlink -f "${1:?Chemin de l\'AppImage}")"
SECONDS_TO_RUN="${2:-20}"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

export HOME="$WORK/home" XDG_RUNTIME_DIR="$WORK/run" APPIMAGE_EXTRACT_AND_RUN=1 GTK_A11Y=none
mkdir -p "$HOME" && mkdir -m 700 "$XDG_RUNTIME_DIR"

set +e
timeout --preserve-status --signal=TERM "$SECONDS_TO_RUN" \
  xvfb-run --auto-servernum --server-args="-screen 0 1280x800x24" \
  dbus-run-session -- "$APPIMAGE" > "$WORK/app.log" 2>&1
status=$?
set -e

cat "$WORK/app.log"
# Arrêtée par timeout (SIGTERM, code 143) : elle tournait encore, c'est le résultat attendu.
if [ "$status" -ne 143 ] && [ "$status" -ne 0 ]; then
  echo "PommeBrowser s'est arrêté (code $status)." >&2
  exit 1
fi
if grep -qiE "unhandled|exception" "$WORK/app.log"; then
  echo "Exception dans le journal de PommeBrowser." >&2
  exit 1
fi
[ -d "$HOME/.config/MyHomelabBrowser/profiles/default" ] || { echo "Profil non créé." >&2; exit 1; }
echo "Essai de lancement réussi."
