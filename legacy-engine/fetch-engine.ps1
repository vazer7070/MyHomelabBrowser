# Place Pomme Legacy (moteur Flash d'origine) dans un paquet Windows de PommeBrowser : archive de
# la version GitHub legacy-engine-<version>, vérifiée par SHA-256, décompressée dans le dossier
# indiqué (legacy\ à côté de MyHomelabBrowser.exe). Même fonctionnement que fetch-engine.sh.
#
#   .\legacy-engine\fetch-engine.ps1 -Destination <dossier legacy>
#
# Sans version publiée, le paquet est construit sans moteur (avertissement) ; avec
# -Required ou LEGACY_ENGINE_REQUIRED=1, c'est une erreur. LEGACY_ENGINE_ARCHIVE=<fichier>
# utilise une archive locale.
param(
    [Parameter(Mandatory = $true)][string]$Destination,
    [switch]$Required
)
$ErrorActionPreference = 'Stop'

$here = $PSScriptRoot
$version = (Get-Content (Join-Path $here 'engine.env') | Where-Object { $_ -match '^BASILISK_VERSION=' }) -replace '^BASILISK_VERSION=', ''
$repo = if ($env:LEGACY_ENGINE_REPO) { $env:LEGACY_ENGINE_REPO } else { 'vazer7070/MyHomelabBrowser' }
$tag = "legacy-engine-$version"
$name = "pomme-legacy-$version-windows-x64.zip"
$cache = if ($env:LEGACY_ENGINE_CACHE) { $env:LEGACY_ENGINE_CACHE } else { Join-Path $here 'out' }
$Required = $Required -or $env:LEGACY_ENGINE_REQUIRED -eq '1'

function Missing([string]$reason) {
    if ($Required) { throw "Pomme Legacy $version (windows-x64) indisponible : $reason" }
    Write-Warning "Paquet sans Pomme Legacy ($reason). Basilisk devra être installé à part."
    exit 0
}

New-Item -ItemType Directory -Force -Path $cache | Out-Null
$archive = Join-Path $cache $name
if ($env:LEGACY_ENGINE_ARCHIVE) {
    $archive = $env:LEGACY_ENGINE_ARCHIVE
    if (-not (Test-Path "$archive.sha256")) { Missing "empreinte $archive.sha256 absente" }
}
elseif (-not (Test-Path $archive) -or -not (Test-Path "$archive.sha256")) {
    Write-Host "Téléchargement de $name ($tag)…"
    Remove-Item -Force -ErrorAction SilentlyContinue $archive, "$archive.sha256"
    $gh = Get-Command gh -ErrorAction SilentlyContinue
    if ($gh) {
        & gh release download $tag --repo $repo --dir $cache --pattern $name --pattern "$name.sha256" 2>$null
        if ($LASTEXITCODE -ne 0) { Missing "version $tag introuvable sur $repo" }
    }
    else {
        try {
            foreach ($file in @($name, "$name.sha256")) {
                Invoke-WebRequest "https://github.com/$repo/releases/download/$tag/$file" -OutFile (Join-Path $cache $file)
            }
        }
        catch {
            Remove-Item -Force -ErrorAction SilentlyContinue $archive, "$archive.sha256"
            Missing "version $tag introuvable sur $repo"
        }
    }
}

$expected = ((Get-Content "$archive.sha256" -Raw).Trim() -split '\s+')[0]
$actual = (Get-FileHash -Algorithm SHA256 $archive).Hash
if ($actual -ne $expected.ToUpperInvariant()) { throw "Empreinte SHA-256 inattendue pour $archive." }

if (Test-Path $Destination) { Remove-Item -Recurse -Force $Destination }
New-Item -ItemType Directory -Force -Path $Destination | Out-Null
Expand-Archive -Path $archive -DestinationPath $Destination
if (-not (Test-Path (Join-Path $Destination 'basilisk.exe'))) { throw "basilisk.exe absent de $archive." }
Write-Host "Pomme Legacy $version placé dans $Destination"
