# build-pack-velopack-github.ps1
# Run: powershell -ExecutionPolicy Bypass -File .\build-pack-velopack-github.ps1

$ErrorActionPreference = "Stop"

function Require-Command($name) {
    if (-not (Get-Command $name -ErrorAction SilentlyContinue)) {
        throw "Missing command: $name (not found in PATH)"
    }
}

function Tag-Exists($tag) {
    git show-ref --tags --verify --quiet "refs/tags/$tag"
    return ($LASTEXITCODE -eq 0)
}

try {
    Clear-Host
    Write-Host "=== Build + Pack + Velopack GitHub Upload ===`n"

    Require-Command dotnet
    Require-Command vpk
    Require-Command git
    Require-Command gh

    $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
    Set-Location $scriptDir

    $sln = Get-ChildItem -Path $scriptDir -Filter *.sln -File -ErrorAction SilentlyContinue | Select-Object -First 1
    $csproj = Get-ChildItem -Path $scriptDir -Filter *.csproj -File -ErrorAction SilentlyContinue | Select-Object -First 1

    $target = $null
    if ($sln) { $target = $sln.FullName }
    elseif ($csproj) { $target = $csproj.FullName }
    else { throw "No .sln or .csproj found in: $scriptDir" }

    $packTitle = Read-Host "Pack title (display name) (ex: PommeBrowser)"
    if ([string]::IsNullOrWhiteSpace($packTitle)) { throw "Invalid pack title." }

    $version = Read-Host "Version to pack (ex: 0.6.62)"
    if ([string]::IsNullOrWhiteSpace($version)) { throw "Invalid version." }

    $tag = "$version"
    $repoUrl = "https://github.com/vazer7070/PommeBrowser-release"
    $channel = "win"

    # Auth check (reliable)
    Write-Host "`n==> Checking GitHub auth..." -ForegroundColor Cyan
    $token = (gh auth token 2>$null)

    if ([string]::IsNullOrWhiteSpace($token)) {
        Write-Host "`nGitHub auth failed. Try this:" -ForegroundColor Red
        Write-Host "  gh auth logout"
        Write-Host "  gh auth login --web"
        throw "Cannot get token from gh (github.com)."
    }

    Write-Host "`n==> 1/4 Publish..." -ForegroundColor Cyan
    dotnet publish "$target" -c Release -r win-x64 --self-contained true
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (code: $LASTEXITCODE)" }

    Write-Host "`n==> 2/4 Pack (vpk)..." -ForegroundColor Cyan
    vpk pack `
        --packId "com.vazer7070.myhomelabbrowser" `
        --packVersion "$version" `
        --packTitle "$packTitle" `
        --mainExe "MyHomelabBrowser.exe" `
        --packDir "bin\Release\net10.0-windows\win-x64\publish" `
        --outputDir ".\Releases"
    if ($LASTEXITCODE -ne 0) { throw "vpk pack failed (code: $LASTEXITCODE)" }

    Write-Host "`n==> 3/4 Git tag..." -ForegroundColor Cyan

    if (Tag-Exists $tag) {
        Write-Host "Tag already exists: $tag" -ForegroundColor Yellow
        $overwrite = Read-Host "Overwrite existing tag (local + remote)? (y/n)"
        if ($overwrite -ne "y") { throw "Aborted (tag exists)." }

        Write-Host "Deleting local tag..." -ForegroundColor Yellow
        git tag -d $tag *> $null

        Write-Host "Deleting remote tag..." -ForegroundColor Yellow
        git push origin ":refs/tags/$tag"
        if ($LASTEXITCODE -ne 0) { throw "Failed to delete remote tag (code: $LASTEXITCODE)" }
    }

    Write-Host "Creating tag..." -ForegroundColor Cyan
    git tag $tag
    if ($LASTEXITCODE -ne 0) { throw "git tag failed (code: $LASTEXITCODE)" }

    Write-Host "Pushing tag..." -ForegroundColor Cyan
    git push origin $tag
    if ($LASTEXITCODE -ne 0) { throw "git push tag failed (code: $LASTEXITCODE)" }

    Write-Host "`n==> 4/4 Upload to GitHub (Velopack)..." -ForegroundColor Cyan
    vpk upload github `
        --repoUrl "$repoUrl" `
        --token "$token" `
        --tag "$tag" `
        --releaseName "$packTitle $version" `
        --channel "$channel" `
        --publish `
        --outputDir ".\Releases"

    if ($LASTEXITCODE -ne 0) { throw "vpk upload github failed (code: $LASTEXITCODE)" } 

# Upload extra file(s) to the same GitHub release
$changelogPath = Join-Path $scriptDir "Releases\changelog.json"
if (Test-Path $changelogPath) {
    Write-Host "`n==> Uploading changelog.json..." -ForegroundColor Cyan
    gh release upload "$tag" "$changelogPath" --repo "vazer7070/PommeBrowser-release" --clobber
}

    Write-Host "`nSUCCESS! Velopack GitHub release is ready: $tag" -ForegroundColor Green
}
catch {
    Write-Host "`nERROR:" -ForegroundColor Red
    Write-Host $_ -ForegroundColor Red
}
finally {
    Write-Host "`nPress Enter to close..."
    [void](Read-Host)
}