# Installe la version auto-hébergée de Ruffle utilisée par PommeBrowser.
# La compilation (build/Ruffle.targets) fait déjà la même chose ; ce script sert à
# réinstaller les fichiers à la main. L'archive est vérifiée par SHA-256.
# Run: powershell -ExecutionPolicy Bypass -File .\install-ruffle-assets.ps1

$ErrorActionPreference = "Stop"

$version = "0.6.0"
# Même valeur que RuffleSha256 dans build/Ruffle.targets.
$expectedSha256 = "e8acfacc37443303872379d0e215999af846854d1dd3fa8fac0a765445b43dbf"
$archiveName = "ruffle-$version-web-selfhosted.zip"
$downloadUrl = "https://github.com/ruffle-rs/ruffle/releases/download/v$version/$archiveName"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$assetDir = Join-Path $scriptDir "Assets\Ruffle"
$tempRoot = Join-Path $env:TEMP ("PommeBrowser-Ruffle-" + [Guid]::NewGuid().ToString("N"))
$archivePath = Join-Path $tempRoot $archiveName
$extractDir = Join-Path $tempRoot "extract"

try {
    New-Item -ItemType Directory -Force -Path $tempRoot, $extractDir, $assetDir | Out-Null

    Write-Host "Téléchargement de Ruffle $version..." -ForegroundColor Cyan
    Invoke-WebRequest -Uri $downloadUrl -OutFile $archivePath -UseBasicParsing

    $actualSha256 = (Get-FileHash -Path $archivePath -Algorithm SHA256).Hash
    if ($actualSha256 -ne $expectedSha256) {
        throw "Empreinte SHA-256 inattendue pour $archiveName : $actualSha256. L'archive n'est pas installée."
    }

    Write-Host "Extraction..." -ForegroundColor Cyan
    Expand-Archive -Path $archivePath -DestinationPath $extractDir -Force

    $ruffleJs = Get-ChildItem -Path $extractDir -Filter "ruffle.js" -File -Recurse | Select-Object -First 1
    if (-not $ruffleJs) {
        throw "ruffle.js est introuvable dans l'archive téléchargée."
    }

    Get-ChildItem -Path $assetDir -Force | Where-Object { $_.Name -ne "README.txt" } | Remove-Item -Recurse -Force

    $sourceRoot = $ruffleJs.Directory.FullName
    Copy-Item -Path (Join-Path $sourceRoot "*") -Destination $assetDir -Recurse -Force

    if (-not (Test-Path (Join-Path $assetDir "ruffle.js"))) {
        throw "L'installation de Ruffle n'a pas produit Assets\Ruffle\ruffle.js."
    }

    Set-Content -Path (Join-Path $assetDir "VERSION.txt") -Value $version -Encoding UTF8
    Write-Host "Ruffle $version installé dans Assets\Ruffle." -ForegroundColor Green
}
finally {
    Remove-Item -Path $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
}
