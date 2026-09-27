# build-pack-velopack-github.ps1
# Version du script : 5.0.3
# Build, package and publish PommeBrowser with Velopack + GitHub Releases.
# Run:
#   powershell -ExecutionPolicy Bypass -File .\build-pack-velopack-github.ps1

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Require-Command {
    param([Parameter(Mandatory)][string]$Name)

    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "Commande manquante : $Name. Installe-la puis relance le script."
    }
}

function Invoke-Native {
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [Parameter(Mandatory)][string[]]$Arguments,
        [Parameter(Mandatory)][string]$FailureMessage,
        [switch]$IgnoreExitCode
    )

    # PowerShell 7 peut transformer un code de sortie natif non nul en erreur
    # terminante avant que le script puisse lire $LASTEXITCODE. On neutralise
    # temporairement ce comportement pour conserver des messages d'erreur propres.
    $oldErrorActionPreference = $ErrorActionPreference
    $hasNativePreference = Test-Path variable:PSNativeCommandUseErrorActionPreference
    if ($hasNativePreference) {
        $oldNativePreference = $PSNativeCommandUseErrorActionPreference
    }

    try {
        $ErrorActionPreference = "Continue"
        if ($hasNativePreference) {
            $PSNativeCommandUseErrorActionPreference = $false
        }

        & $FilePath @Arguments 2>&1 | Out-Host
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $oldErrorActionPreference
        if ($hasNativePreference) {
            $PSNativeCommandUseErrorActionPreference = $oldNativePreference
        }
    }

    if (-not $IgnoreExitCode -and $exitCode -ne 0) {
        throw "$FailureMessage (code : $exitCode)"
    }
}

function Invoke-NativeOptional {
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [Parameter(Mandatory)][string[]]$Arguments
    )

    $oldErrorActionPreference = $ErrorActionPreference
    $hasNativePreference = Test-Path variable:PSNativeCommandUseErrorActionPreference
    if ($hasNativePreference) {
        $oldNativePreference = $PSNativeCommandUseErrorActionPreference
    }

    try {
        $ErrorActionPreference = "Continue"
        if ($hasNativePreference) {
            $PSNativeCommandUseErrorActionPreference = $false
        }

        $output = @(& $FilePath @Arguments 2>&1)
        $exitCode = $LASTEXITCODE

        if ($exitCode -eq 0 -and $output.Count -gt 0) {
            $output | Out-Host
        }

        return $exitCode
    }
    finally {
        $ErrorActionPreference = $oldErrorActionPreference
        if ($hasNativePreference) {
            $PSNativeCommandUseErrorActionPreference = $oldNativePreference
        }
    }
}

function Read-HostWithDefault {
    param(
        [Parameter(Mandatory)][string]$Prompt,
        [Parameter(Mandatory)][string]$DefaultValue
    )

    $value = Read-Host "$Prompt [$DefaultValue]"
    if ([string]::IsNullOrWhiteSpace($value)) {
        return $DefaultValue
    }

    return $value.Trim()
}

function Test-LocalTag {
    param([Parameter(Mandatory)][string]$Tag)

    # git tag --list renvoie toujours 0, même si le tag n'existe pas.
    $result = @(& git tag --list $Tag 2>$null)
    if ($LASTEXITCODE -ne 0) {
        throw "Impossible de consulter les tags Git locaux."
    }

    return ($result -contains $Tag)
}

function Test-RemoteTag {
    param([Parameter(Mandatory)][string]$Tag)

    # Sans --exit-code, l'absence du tag ne produit pas d'erreur.
    $result = @(& git ls-remote --tags origin "refs/tags/$Tag" 2>$null)
    if ($LASTEXITCODE -ne 0) {
        throw "Impossible de consulter les tags Git distants."
    }

    return ($result.Count -gt 0)
}

function Test-GitHubRelease {
    param(
        [Parameter(Mandatory)][string]$Tag,
        [Parameter(Mandatory)][string]$Repository
    )

    # "gh release view <tag>" renvoie l'erreur "release not found" lorsqu'une
    # version n'existe pas. Avec PowerShell 7 et ErrorActionPreference=Stop,
    # cela peut interrompre tout le script. On liste donc les releases : une
    # liste vide est un résultat normal avec un code de sortie 0.
    $json = & gh release list `
        --repo $Repository `
        --limit 1000 `
        --json tagName 2>$null

    if ($LASTEXITCODE -ne 0) {
        throw "Impossible de consulter les releases GitHub du dépôt $Repository."
    }

    if ([string]::IsNullOrWhiteSpace(($json | Out-String))) {
        return $false
    }

    try {
        $releases = @($json | ConvertFrom-Json)
    }
    catch {
        throw "Réponse GitHub invalide lors de la vérification des releases."
    }

    return (@($releases | Where-Object { $_.tagName -eq $Tag }).Count -gt 0)
}

function Get-ProjectMetadata {
    param([Parameter(Mandatory)][string]$ProjectPath)

    # Ne pas utiliser $projectXml.Project.ItemGroup.PackageReference :
    # sous Set-StrictMode, les ItemGroup sans PackageReference peuvent lever
    # "La propriété PackageReference est introuvable".
    [xml]$projectXml = [System.IO.File]::ReadAllText($ProjectPath)

    $upper = "ABCDEFGHIJKLMNOPQRSTUVWXYZ"
    $lower = "abcdefghijklmnopqrstuvwxyz"

    $velopackReference = $projectXml.SelectSingleNode(
        "/*[local-name()='Project']/*[local-name()='ItemGroup']/*[local-name()='PackageReference'][" +
        "translate(@Include,'$upper','$lower')='velopack' or " +
        "translate(@Update,'$upper','$lower')='velopack']"
    )

    $velopackVersion = $null

    if ($velopackReference) {
        # Forme courante : <PackageReference Include="Velopack" Version="x.y.z" />
        $versionAttribute = $velopackReference.Attributes["Version"]
        if ($versionAttribute) {
            $velopackVersion = [string]$versionAttribute.Value
        }

        # Forme alternative : <PackageReference ...><Version>x.y.z</Version></PackageReference>
        if ([string]::IsNullOrWhiteSpace($velopackVersion)) {
            $versionNode = $velopackReference.SelectSingleNode("./*[local-name()='Version']")
            if ($versionNode) {
                $velopackVersion = [string]$versionNode.InnerText
            }
        }
    }

    # Support de la gestion centralisée des versions NuGet (Directory.Packages.props).
    if ([string]::IsNullOrWhiteSpace($velopackVersion)) {
        $directory = Split-Path -Parent $ProjectPath

        while (-not [string]::IsNullOrWhiteSpace($directory)) {
            $centralProps = Join-Path $directory "Directory.Packages.props"

            if (Test-Path -LiteralPath $centralProps) {
                [xml]$centralXml = [System.IO.File]::ReadAllText($centralProps)
                $packageVersionNode = $centralXml.SelectSingleNode(
                    "/*[local-name()='Project']/*[local-name()='ItemGroup']/*[local-name()='PackageVersion'][" +
                    "translate(@Include,'$upper','$lower')='velopack' or " +
                    "translate(@Update,'$upper','$lower')='velopack']"
                )

                if ($packageVersionNode) {
                    $centralVersionAttribute = $packageVersionNode.Attributes["Version"]
                    if ($centralVersionAttribute) {
                        $velopackVersion = [string]$centralVersionAttribute.Value
                    }

                    if ([string]::IsNullOrWhiteSpace($velopackVersion)) {
                        $centralVersionNode = $packageVersionNode.SelectSingleNode("./*[local-name()='Version']")
                        if ($centralVersionNode) {
                            $velopackVersion = [string]$centralVersionNode.InnerText
                        }
                    }
                }

                break
            }

            $parent = Split-Path -Parent $directory
            if ($parent -eq $directory) { break }
            $directory = $parent
        }
    }

    if ([string]::IsNullOrWhiteSpace($velopackVersion)) {
        throw "Impossible de trouver la version NuGet de Velopack dans le projet ou dans Directory.Packages.props."
    }

    $versionNode = $projectXml.SelectSingleNode(
        "/*[local-name()='Project']/*[local-name()='PropertyGroup']/*[local-name()='Version'][normalize-space(text()) != '']"
    )

    if (-not $versionNode) {
        $versionNode = $projectXml.SelectSingleNode(
            "/*[local-name()='Project']/*[local-name()='PropertyGroup']/*[local-name()='VersionPrefix'][normalize-space(text()) != '']"
        )
    }

    $appVersion = if ($versionNode) { [string]$versionNode.InnerText } else { "1.0.0" }

    return [pscustomobject]@{
        VelopackVersion = $velopackVersion.Trim()
        AppVersion      = $appVersion.Trim()
    }
}

function Get-VpkExecutable {
    param([Parameter(Mandatory)][string]$VelopackVersion)

    # Installation locale à l'utilisateur : aucun PATH global n'est nécessaire.
    $toolRoot = Join-Path $env:LOCALAPPDATA "PommeBrowser\BuildTools\vpk\$VelopackVersion"
    $vpkExe = Join-Path $toolRoot "vpk.exe"

    # Ne jamais lancer « vpk --version » ici : certaines versions de vpk
    # exigent une sous-commande et répondent « Required command was not provided ».
    # La présence de l'exécutable installé localement suffit.
    if (Test-Path -LiteralPath $vpkExe) {
        return $vpkExe
    }

    # Nettoie une éventuelle installation locale incomplète avant réinstallation.
    if (Test-Path -LiteralPath $toolRoot) {
        Remove-Item -LiteralPath $toolRoot -Recurse -Force
    }

    Write-Host "`n==> Installation locale de vpk $VelopackVersion..." -ForegroundColor Cyan
    New-Item -ItemType Directory -Path $toolRoot -Force | Out-Null

    Invoke-Native -FilePath "dotnet" -Arguments @(
        "tool", "install", "vpk",
        "--tool-path", $toolRoot,
        "--version", $VelopackVersion
    ) -FailureMessage "Installation de vpk impossible"

    if (-not (Test-Path -LiteralPath $vpkExe)) {
        throw "vpk a été installé, mais $vpkExe est introuvable."
    }

    return $vpkExe
}

# Signature de code (facultative). Sans signature, Windows SmartScreen avertit à chaque
# nouvelle version. Rien de secret dans le dépôt : tout passe par des variables d'environnement.
#   POMMEBROWSER_AZURE_SIGN_METADATA : chemin du fichier metadata.json d'Azure Trusted Signing
#   POMMEBROWSER_SIGN_PARAMS         : arguments signtool, par exemple
#       /fd sha256 /tr http://timestamp.digicert.com /td sha256 /sha1 <empreinte du certificat>
function Get-SigningArguments {
    $azureMetadata = $env:POMMEBROWSER_AZURE_SIGN_METADATA
    if (-not [string]::IsNullOrWhiteSpace($azureMetadata)) {
        if (-not (Test-Path -LiteralPath $azureMetadata)) {
            throw "POMMEBROWSER_AZURE_SIGN_METADATA pointe vers un fichier introuvable : $azureMetadata"
        }

        Write-Host "Signature          : Azure Trusted Signing" -ForegroundColor DarkGray
        return @("--azureTrustedSignFile", $azureMetadata)
    }

    $signParams = $env:POMMEBROWSER_SIGN_PARAMS
    if (-not [string]::IsNullOrWhiteSpace($signParams)) {
        Write-Host "Signature          : signtool" -ForegroundColor DarkGray
        return @("--signParams", $signParams)
    }

    Write-Host "Signature          : aucune (SmartScreen avertira les utilisateurs)" -ForegroundColor Yellow
    return @()
}

try {
    Clear-Host
    Write-Host "=== PommeBrowser - Build + Velopack + GitHub ===`n" -ForegroundColor Cyan

    $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
    Set-Location $scriptDir

    $ruffleMainScript = Join-Path $scriptDir "Assets\Ruffle\ruffle.js"
    if (-not (Test-Path -LiteralPath $ruffleMainScript)) {
        $ruffleInstaller = Join-Path $scriptDir "install-ruffle-assets.ps1"
        if (-not (Test-Path -LiteralPath $ruffleInstaller)) {
            throw "Ruffle local est absent et install-ruffle-assets.ps1 est introuvable."
        }

        Write-Host "==> Installation du moteur Ruffle intégré..." -ForegroundColor Cyan
        & $ruffleInstaller

        if (-not (Test-Path -LiteralPath $ruffleMainScript)) {
            throw "L'installation de Ruffle n'a pas produit Assets\Ruffle\ruffle.js."
        }
    }

    Require-Command "dotnet"
    Require-Command "git"
    Require-Command "gh"

    # On publie directement le projet WPF. Utiliser une solution avec -o est fragile.
    $csproj = Get-ChildItem -Path $scriptDir -Filter *.csproj -File -ErrorAction SilentlyContinue |
        Select-Object -First 1

    if (-not $csproj) {
        throw "Aucun fichier .csproj trouvé dans : $scriptDir"
    }

    $metadata = Get-ProjectMetadata -ProjectPath $csproj.FullName
    $vpk = Get-VpkExecutable -VelopackVersion $metadata.VelopackVersion

    Write-Host "Projet             : $($csproj.Name)" -ForegroundColor DarkGray
    Write-Host "Velopack NuGet/vpk : $($metadata.VelopackVersion)" -ForegroundColor DarkGray

    $packTitle = Read-HostWithDefault -Prompt "Nom affiché du logiciel" -DefaultValue "PommeBrowser"
    $version = Read-HostWithDefault -Prompt "Version à publier" -DefaultValue $metadata.AppVersion

    if ($version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$') {
        throw "Version invalide : '$version'. Utilise SemVer, par exemple 0.9.1 ou 0.9.1-beta.1."
    }

    $packId = "com.vazer7070.myhomelabbrowser"
    $mainExe = "MyHomelabBrowser.exe"
    $repoSlug = "vazer7070/PommeBrowser-release"
    $repoUrl = "https://github.com/$repoSlug"
    $channel = "win"
    $tag = $version

    $publishDir = Join-Path $scriptDir "artifacts\publish\win-x64"
    $releasesDir = Join-Path $scriptDir "Releases"
    $iconPath = Join-Path $scriptDir "PommeBrowser_desktop.ico"

    Write-Host "`n==> Vérification de l'authentification GitHub..." -ForegroundColor Cyan

    $oldErrorActionPreference = $ErrorActionPreference
    $hasNativePreference = Test-Path variable:PSNativeCommandUseErrorActionPreference
    if ($hasNativePreference) {
        $oldNativePreference = $PSNativeCommandUseErrorActionPreference
    }

    try {
        $ErrorActionPreference = "Continue"
        if ($hasNativePreference) {
            $PSNativeCommandUseErrorActionPreference = $false
        }

        $tokenOutput = & gh auth token 2>$null
        $tokenExitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $oldErrorActionPreference
        if ($hasNativePreference) {
            $PSNativeCommandUseErrorActionPreference = $oldNativePreference
        }
    }

    $token = ($tokenOutput | Out-String).Trim()

    if ($tokenExitCode -ne 0 -or [string]::IsNullOrWhiteSpace($token)) {
        Write-Host "`nAuthentification GitHub absente." -ForegroundColor Red
        Write-Host "Exécute : gh auth login --web" -ForegroundColor Yellow
        throw "Impossible d'obtenir le jeton GitHub depuis gh."
    }

    # Évite de publier accidentellement sous une version/tag déjà utilisé.
    $localTagExists = Test-LocalTag -Tag $tag
    $remoteTagExists = Test-RemoteTag -Tag $tag
    $releaseExists = Test-GitHubRelease -Tag $tag -Repository $repoSlug

    if ($localTagExists -or $remoteTagExists -or $releaseExists) {
        Write-Host "`nLa version/tag '$tag' existe déjà." -ForegroundColor Yellow
        $overwrite = (Read-Host "Supprimer l'ancienne release et recréer cette version ? (o/n)").Trim().ToLowerInvariant()

        if ($overwrite -notin @("o", "oui", "y", "yes")) {
            throw "Publication annulée : version déjà existante."
        }

        if ($releaseExists) {
            Write-Host "Suppression de la release GitHub existante..." -ForegroundColor Yellow
            Invoke-Native -FilePath "gh" -Arguments @(
                "release", "delete", $tag,
                "--repo", $repoSlug,
                "--yes"
            ) -FailureMessage "Suppression de la release GitHub impossible"
        }

        if ($localTagExists) {
            Write-Host "Suppression du tag local..." -ForegroundColor Yellow
            Invoke-Native -FilePath "git" -Arguments @("tag", "-d", $tag) -FailureMessage "Suppression du tag local impossible"
        }

        if ($remoteTagExists) {
            Write-Host "Suppression du tag distant..." -ForegroundColor Yellow
            Invoke-Native -FilePath "git" -Arguments @("push", "origin", ":refs/tags/$tag") -FailureMessage "Suppression du tag distant impossible"
        }
    }

    New-Item -ItemType Directory -Path $releasesDir -Force | Out-Null

    # Récupère la dernière release pour permettre à Velopack de générer les deltas.
    Write-Host "`n==> 1/5 Récupération de la release précédente..." -ForegroundColor Cyan
    $downloadExitCode = Invoke-NativeOptional -FilePath $vpk -Arguments @(
        "download", "github",
        "--repoUrl", $repoUrl,
        "--token", $token,
        "--channel", $channel,
        "--outputDir", $releasesDir
    )

    if ($downloadExitCode -ne 0) {
        Write-Host "Aucune release précédente récupérée. Première publication ou téléchargement indisponible." -ForegroundColor Yellow
    }

    Write-Host "`n==> 2/5 Publication .NET..." -ForegroundColor Cyan
    if (Test-Path -LiteralPath $publishDir) {
        Remove-Item -LiteralPath $publishDir -Recurse -Force
    }
    New-Item -ItemType Directory -Path $publishDir -Force | Out-Null

    Invoke-Native -FilePath "dotnet" -Arguments @(
        "publish", $csproj.FullName,
        "-c", "Release",
        "-r", "win-x64",
        "--self-contained", "true",
        "-o", $publishDir,
        "-p:Version=$version"
    ) -FailureMessage "dotnet publish a échoué"

    $mainExePath = Join-Path $publishDir $mainExe
    if (-not (Test-Path -LiteralPath $mainExePath)) {
        throw "L'exécutable principal est absent après publication : $mainExePath"
    }

    Write-Host "`n==> 3/5 Création des paquets Velopack..." -ForegroundColor Cyan
    $packArguments = @(
        "pack",
        "--packId", $packId,
        "--packVersion", $version,
        "--packTitle", $packTitle,
        "--mainExe", $mainExe,
        "--packDir", $publishDir,
        "--outputDir", $releasesDir,
        "--channel", $channel,
        "--runtime", "win-x64",
        "--yes"
    )

    if (Test-Path -LiteralPath $iconPath) {
        $packArguments += @("--icon", $iconPath)
    }

    # @() : une fonction qui ne renvoie rien donnerait sinon un argument $null.
    $signingArguments = @(Get-SigningArguments)
    if ($signingArguments.Count -gt 0) {
        $packArguments += $signingArguments
    }

    Invoke-Native -FilePath $vpk -Arguments $packArguments -FailureMessage "vpk pack a échoué"

    Write-Host "`n==> 4/5 Création et envoi du tag Git..." -ForegroundColor Cyan
    Invoke-Native -FilePath "git" -Arguments @("tag", $tag) -FailureMessage "Création du tag Git impossible"
    Invoke-Native -FilePath "git" -Arguments @("push", "origin", $tag) -FailureMessage "Envoi du tag Git impossible"

    Write-Host "`n==> 5/5 Publication sur GitHub Releases..." -ForegroundColor Cyan
    Invoke-Native -FilePath $vpk -Arguments @(
        "upload", "github",
        "--repoUrl", $repoUrl,
        "--token", $token,
        "--tag", $tag,
        "--releaseName", "$packTitle $version",
        "--channel", $channel,
        "--publish",
        "--outputDir", $releasesDir
    ) -FailureMessage "vpk upload github a échoué"

    $changelogPath = Join-Path $releasesDir "changelog.json"
    if (Test-Path -LiteralPath $changelogPath) {
        Write-Host "`n==> Ajout de changelog.json à la release..." -ForegroundColor Cyan
        Invoke-Native -FilePath "gh" -Arguments @(
            "release", "upload", $tag,
            $changelogPath,
            "--repo", $repoSlug,
            "--clobber"
        ) -FailureMessage "Envoi de changelog.json impossible"
    }

    Write-Host "`nSUCCÈS : la release $tag est publiée." -ForegroundColor Green
    Write-Host "Dossier local : $releasesDir" -ForegroundColor DarkGray
}
catch {
    Write-Host "`nERREUR :" -ForegroundColor Red
    Write-Host $_.Exception.Message -ForegroundColor Red
}
finally {
    Write-Host "`nAppuie sur Entrée pour fermer..."
    [void](Read-Host)
}
