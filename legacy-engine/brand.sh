#!/usr/bin/env bash
# Marque « Pomme Legacy ». La politique de marque de Basilisk réserve son nom et ses logos aux
# compilations officielles ; une compilation modifiée doit porter un autre nom. La marque
# « non officielle » des sources est copiée puis renommée.
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
grep -rl "branding/$base" pomme | xargs -r sed -i "s#branding/$base#branding/pomme#g"

find pomme -name brand.dtd -print0 | xargs -0 -r sed -i -E \
  -e 's/(<!ENTITY[[:space:]]+(brandShorterName|brandShortName|brandFullName)[[:space:]]+)"[^"]*"/\1"Pomme Legacy"/' \
  -e 's/(<!ENTITY[[:space:]]+vendorShortName[[:space:]]+)"[^"]*"/\1"PommeBrowser"/'
find pomme -name brand.properties -print0 | xargs -0 -r sed -i -E \
  -e 's/^(brandShorterName|brandShortName|brandFullName)=.*/\1=Pomme Legacy/' \
  -e 's/^vendorShortName=.*/vendorShortName=PommeBrowser/'
[ -f pomme/configure.sh ] && sed -i -E 's/^MOZ_APP_DISPLAYNAME=.*/MOZ_APP_DISPLAYNAME=PommeLegacy/' pomme/configure.sh

echo "Marque « Pomme Legacy » :"
grep -rhE "brand(Shorter|Short|Full)Name|MOZ_APP_DISPLAYNAME" pomme | sed 's/^/  /'
