#!/usr/bin/env bash
# Marque « Pomme Legacy ». La politique de marque de Basilisk réserve son nom et ses logos aux
# compilations officielles ; une compilation modifiée doit porter un autre nom. La marque
# « non officielle » des sources (« Snake ») est copiée puis renommée.
#
#   legacy-engine/brand.sh <sources>
set -euo pipefail

SRC="${1:?Dossier des sources}"
cd "$SRC/basilisk/branding"
echo "Marques disponibles : $(ls | tr '\n' ' ')"

base=unofficial
[ -d "$base" ] || { echo "Marque « $base » absente des sources." >&2; exit 1; }
rm -rf pomme
cp -r "$base" pomme
grep -rl "branding/$base" pomme | while read -r file; do sed -i "s#branding/$base#branding/pomme#g" "$file"; done

find pomme -name brand.dtd -exec sed -i -E \
  -e 's/(<!ENTITY[[:space:]]+(brandShorterName|brandShortName|brandFullName)[[:space:]]+)"[^"]*"/\1"Pomme Legacy"/' \
  -e 's/(<!ENTITY[[:space:]]+(vendorShortName|vendorFullName)[[:space:]]+)"[^"]*"/\1"PommeBrowser"/' {} +
find pomme -name brand.properties -exec sed -i -E \
  -e 's/^(brandShorterName|brandShortName|brandFullName)=.*/\1=Pomme Legacy/' \
  -e 's/^(vendorShortName|vendorFullName)=.*/\1=PommeBrowser/' {} +
[ -f pomme/configure.sh ] && sed -i -E 's/^MOZ_APP_DISPLAYNAME=.*/MOZ_APP_DISPLAYNAME=PommeLegacy/' pomme/configure.sh

echo "Marque « Pomme Legacy » :"
grep -rhE "brand(Shorter|Short|Full)Name|MOZ_APP_DISPLAYNAME" pomme | sed 's/^/  /'
