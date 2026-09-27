# build-pack-velopack-github.ps1
# Version du script : 5.1.0
# Publication PommeBrowser : build .NET, obfuscation prudente, Velopack et GitHub Releases.
# Compatible Windows PowerShell 5.1 et PowerShell 7.
#
# Exécution normale :
#   powershell -ExecutionPolicy Bypass -File .\build-pack-velopack-github.ps1
#
# Build de diagnostic sans obfuscation :
#   powershell -ExecutionPolicy Bypass -File .\build-pack-velopack-github.ps1 -SkipObfuscation

[CmdletBinding()]
param(
    [switch]$SkipObfuscation,
    [switch]$KeepSymbols,
    [string]$ObfuscarVersion = "2.2.50"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Require-Command {
    param([Parameter(Mandatory)][string]$Name)

    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "Commande manquante : $Name. Installe-la puis relance le script."
    }
}

function ConvertTo-NativeArgument {
    param([AllowEmptyString()][string]$Argument)

    if ($null -eq $Argument -or $Argument.Length -eq 0) {
        return '""'
    }

    if ($Argument -notmatch '[\s"]') {
        return $Argument
    }

    $builder = New-Object System.Text.StringBuilder
    [void]$builder.Append('"')
    $backslashes = 0

    foreach ($character in $Argument.ToCharArray()) {
        if ($character -eq '\') {
            $backslashes++
            continue
        }

        if ($character -eq '"') {
            [void]$builder.Append(('\' * (($backslashes * 2) + 1)))
            [void]$builder.Append('"')
            $backslashes = 0
            continue
        }

        if ($backslashes -gt 0) {
            [void]$builder.Append(('\' * $backslashes))
            $backslashes = 0
        }

        [void]$builder.Append($character)
    }

    if ($backslashes -gt 0) {
        [void]$builder.Append(('\' * ($backslashes * 2)))
    }

    [void]$builder.Append('"')
    return $builder.ToString()
}

function Invoke-NativeProcess {
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [string[]]$Arguments = @(),
        [string]$FailureMessage = "La commande native a échoué",
        [switch]$AllowFailure,
        [switch]$Quiet
    )

    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = $FilePath
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.CreateNoWindow = $true
    $startInfo.WorkingDirectory = (Get-Location).Path

    $argumentListProperty = $startInfo.PSObject.Properties['ArgumentList']
    if ($null -ne $argumentListProperty) {
        foreach ($argument in $Arguments) {
            [void]$startInfo.ArgumentList.Add([string]$argument)
        }
    }
    else {
        $startInfo.Arguments = (($Arguments | ForEach-Object {
            ConvertTo-NativeArgument -Argument ([string]$_)
        }) -join ' ')
    }

    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $startInfo

    try {
        if (-not $process.Start()) {
            throw "Impossible de démarrer : $FilePath"
        }

        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()

        $process.WaitForExit()
        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        $exitCode = $process.ExitCode
    }
    finally {
        $process.Dispose()
    }

    if (-not $Quiet) {
        if (-not [string]::IsNullOrWhiteSpace($stdout)) {
            Write-Host ($stdout.TrimEnd())
        }

        if (-not [string]::IsNullOrWhiteSpace($stderr)) {
            $stderrColor = if ($exitCode -eq 0) { 'DarkGray' } else { 'Red' }
            Write-Host ($stderr.TrimEnd()) -ForegroundColor $stderrColor
        }
    }

    $result = [pscustomobject]@{
        ExitCode = $exitCode
        StdOut   = $stdout
        StdErr   = $stderr
    }

    if (-not $AllowFailure -and $exitCode -ne 0) {
        $details = (($stderr + "`n" + $stdout).Trim())
        if ($details.Length -gt 1600) {
            $details = $details.Substring($details.Length - 1600)
        }

        if ([string]::IsNullOrWhiteSpace($details)) {
            throw "$FailureMessage (code : $exitCode)"
        }

        throw "$FailureMessage (code : $exitCode)`n$details"
    }

    return $result
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

function Get-ProjectMetadata {
    param([Parameter(Mandatory)][string]$ProjectPath)

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
        $versionAttribute = $velopackReference.Attributes["Version"]
        if ($versionAttribute) {
            $velopackVersion = [string]$versionAttribute.Value
        }

        if ([string]::IsNullOrWhiteSpace($velopackVersion)) {
            $versionNode = $velopackReference.SelectSingleNode("./*[local-name()='Version']")
            if ($versionNode) {
                $velopackVersion = [string]$versionNode.InnerText
            }
        }
    }

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
        throw "Impossible de trouver la version NuGet de Velopack dans le projet ou Directory.Packages.props."
    }

    $appVersionNode = $projectXml.SelectSingleNode(
        "/*[local-name()='Project']/*[local-name()='PropertyGroup']/*[local-name()='Version'][normalize-space(text()) != '']"
    )

    if (-not $appVersionNode) {
        $appVersionNode = $projectXml.SelectSingleNode(
            "/*[local-name()='Project']/*[local-name()='PropertyGroup']/*[local-name()='VersionPrefix'][normalize-space(text()) != '']"
        )
    }

    $appVersion = if ($appVersionNode) { [string]$appVersionNode.InnerText } else { "1.0.0" }

    return [pscustomobject]@{
        VelopackVersion = $velopackVersion.Trim()
        AppVersion      = $appVersion.Trim()
    }
}

function Get-LocalDotNetTool {
    param(
        [Parameter(Mandatory)][string]$PackageId,
        [Parameter(Mandatory)][string]$Version,
        [Parameter(Mandatory)][string[]]$ExecutableCandidates,
        [Parameter(Mandatory)][string]$ToolFolderName
    )

    $toolRoot = Join-Path $env:LOCALAPPDATA "PommeBrowser\BuildTools\$ToolFolderName\$Version"

    foreach ($candidate in $ExecutableCandidates) {
        $candidatePath = Join-Path $toolRoot $candidate
        if (Test-Path -LiteralPath $candidatePath) {
            return $candidatePath
        }
    }

    if (Test-Path -LiteralPath $toolRoot) {
        Remove-Item -LiteralPath $toolRoot -Recurse -Force
    }

    Write-Host "`n==> Installation locale de $PackageId $Version..." -ForegroundColor Cyan
    New-Item -ItemType Directory -Path $toolRoot -Force | Out-Null

    [void](Invoke-NativeProcess -FilePath "dotnet" -Arguments @(
        "tool", "install", $PackageId,
        "--tool-path", $toolRoot,
        "--version", $Version
    ) -FailureMessage "Installation de $PackageId impossible")

    foreach ($candidate in $ExecutableCandidates) {
        $candidatePath = Join-Path $toolRoot $candidate
        if (Test-Path -LiteralPath $candidatePath) {
            return $candidatePath
        }
    }

    $foundExecutable = Get-ChildItem -LiteralPath $toolRoot -Filter *.exe -File -ErrorAction SilentlyContinue |
        Select-Object -First 1

    if ($foundExecutable) {
        return $foundExecutable.FullName
    }

    throw "$PackageId a été installé, mais aucun exécutable n'a été trouvé dans $toolRoot."
}

function Get-GitHubReleaseState {
    param(
        [Parameter(Mandatory)][string]$Repository,
        [Parameter(Mandatory)][string]$Tag
    )

    $releaseResult = Invoke-NativeProcess -FilePath "gh" -Arguments @(
        "release", "list",
        "--repo", $Repository,
        "--limit", "1000",
        "--json", "tagName",
        "--jq", ".[] | select(.tagName == `"$Tag`") | .tagName"
    ) -FailureMessage "Impossible de consulter les releases GitHub" -Quiet

    $releaseExists = -not [string]::IsNullOrWhiteSpace($releaseResult.StdOut)

    $tagResult = Invoke-NativeProcess -FilePath "gh" -Arguments @(
        "api", "repos/$Repository/git/matching-refs/tags/$Tag",
        "--jq", "length"
    ) -FailureMessage "Impossible de consulter les tags du dépôt de releases" -Quiet

    $tagCount = 0
    $null = [int]::TryParse($tagResult.StdOut.Trim(), [ref]$tagCount)

    return [pscustomobject]@{
        ReleaseExists = $releaseExists
        TagExists     = ($tagCount -gt 0)
    }
}

function Copy-DirectoryContents {
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$Destination
    )

    if (Test-Path -LiteralPath $Destination) {
        Remove-Item -LiteralPath $Destination -Recurse -Force
    }

    New-Item -ItemType Directory -Path $Destination -Force | Out-Null

    Get-ChildItem -LiteralPath $Source -Force | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $Destination -Recurse -Force
    }
}

function Get-XamlClassNames {
    param([Parameter(Mandatory)][string]$ProjectRoot)

    $names = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::Ordinal)

    Get-ChildItem -LiteralPath $ProjectRoot -Filter *.xaml -File -Recurse -ErrorAction SilentlyContinue |
        Where-Object {
            $_.FullName -notmatch '[\\/](bin|obj|artifacts|Releases)[\\/]'
        } |
        ForEach-Object {
            $content = [System.IO.File]::ReadAllText($_.FullName)
            $matches = [System.Text.RegularExpressions.Regex]::Matches(
                $content,
                'x:Class\s*=\s*"([^"]+)"',
                [System.Text.RegularExpressions.RegexOptions]::IgnoreCase
            )

            foreach ($match in $matches) {
                $name = $match.Groups[1].Value.Trim()
                if (-not [string]::IsNullOrWhiteSpace($name)) {
                    [void]$names.Add($name)
                }
            }
        }

    return @($names | Sort-Object)
}

function Get-CustomObfuscationExclusions {
    param([Parameter(Mandatory)][string]$Path)

    $types = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::Ordinal)
    $namespaces = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::Ordinal)

    if (Test-Path -LiteralPath $Path) {
        foreach ($rawLine in [System.IO.File]::ReadAllLines($Path)) {
            $line = $rawLine.Trim()
            if ([string]::IsNullOrWhiteSpace($line) -or $line.StartsWith('#')) {
                continue
            }

            if ($line.StartsWith('type:', [System.StringComparison]::OrdinalIgnoreCase)) {
                $value = $line.Substring(5).Trim()
                if (-not [string]::IsNullOrWhiteSpace($value)) {
                    [void]$types.Add($value)
                }
                continue
            }

            if ($line.StartsWith('namespace:', [System.StringComparison]::OrdinalIgnoreCase)) {
                $value = $line.Substring(10).Trim()
                if (-not [string]::IsNullOrWhiteSpace($value)) {
                    [void]$namespaces.Add($value)
                }
                continue
            }

            throw "Ligne invalide dans $Path : '$line'. Utilise type:Nom.Complet ou namespace:Nom.*"
        }
    }

    return [pscustomobject]@{
        Types      = @($types | Sort-Object)
        Namespaces = @($namespaces | Sort-Object)
    }
}

function Write-ObfuscarVar {
    param(
        [Parameter(Mandatory)][System.Xml.XmlWriter]$Writer,
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Value
    )

    $Writer.WriteStartElement('Var')
    $Writer.WriteAttributeString('name', $Name)
    $Writer.WriteAttributeString('value', $Value)
    $Writer.WriteEndElement()
}

function Write-ObfuscarSkipType {
    param(
        [Parameter(Mandatory)][System.Xml.XmlWriter]$Writer,
        [Parameter(Mandatory)][string]$Name
    )

    $Writer.WriteStartElement('SkipType')
    $Writer.WriteAttributeString('name', $Name)
    $Writer.WriteAttributeString('skipMethods', 'true')
    $Writer.WriteAttributeString('skipFields', 'true')
    $Writer.WriteAttributeString('skipProperties', 'true')
    $Writer.WriteAttributeString('skipEvents', 'true')
    $Writer.WriteAttributeString('skipStringHiding', 'true')
    $Writer.WriteEndElement()
}

function New-ObfuscarConfiguration {
    param(
        [Parameter(Mandatory)][string]$InputDirectory,
        [Parameter(Mandatory)][string]$OutputDirectory,
        [Parameter(Mandatory)][string]$AssemblyPath,
        [Parameter(Mandatory)][string]$MappingPath,
        [Parameter(Mandatory)][string]$ConfigurationPath,
        [string[]]$XamlClasses = @(),
        [string[]]$ExtraTypes = @(),
        [string[]]$ExtraNamespaces = @()
    )

    $settings = New-Object System.Xml.XmlWriterSettings
    $settings.Indent = $true
    $settings.Encoding = New-Object System.Text.UTF8Encoding($false)
    $settings.NewLineChars = "`r`n"

    $writer = [System.Xml.XmlWriter]::Create($ConfigurationPath, $settings)
    try {
        $writer.WriteStartDocument()
        $writer.WriteStartElement('Obfuscator')

        Write-ObfuscarVar -Writer $writer -Name 'InPath' -Value $InputDirectory
        Write-ObfuscarVar -Writer $writer -Name 'OutPath' -Value $OutputDirectory
        Write-ObfuscarVar -Writer $writer -Name 'LogFile' -Value $MappingPath
        Write-ObfuscarVar -Writer $writer -Name 'XmlMapping' -Value 'true'

        # Mode prudent : l'API publique et les noms de propriétés/événements sont
        # conservés pour protéger WPF, JSON, les bindings et les intégrations.
        Write-ObfuscarVar -Writer $writer -Name 'KeepPublicApi' -Value 'true'
        Write-ObfuscarVar -Writer $writer -Name 'HidePrivateApi' -Value 'true'
        Write-ObfuscarVar -Writer $writer -Name 'RenameProperties' -Value 'false'
        Write-ObfuscarVar -Writer $writer -Name 'RenameEvents' -Value 'false'
        Write-ObfuscarVar -Writer $writer -Name 'RenameFields' -Value 'true'
        Write-ObfuscarVar -Writer $writer -Name 'HideStrings' -Value 'true'
        Write-ObfuscarVar -Writer $writer -Name 'ReuseNames' -Value 'true'
        Write-ObfuscarVar -Writer $writer -Name 'UseUnicodeNames' -Value 'false'
        Write-ObfuscarVar -Writer $writer -Name 'UseKoreanNames' -Value 'false'
        Write-ObfuscarVar -Writer $writer -Name 'OptimizeMethods' -Value 'false'
        Write-ObfuscarVar -Writer $writer -Name 'SuppressIldasm' -Value 'true'
        Write-ObfuscarVar -Writer $writer -Name 'AnalyzeXaml' -Value 'true'

        $writer.WriteStartElement('AssemblySearchPath')
        $writer.WriteAttributeString('path', $InputDirectory)
        $writer.WriteEndElement()

        $writer.WriteStartElement('Module')
        $writer.WriteAttributeString('file', $AssemblyPath)

        $allTypes = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::Ordinal)
        foreach ($typeName in $XamlClasses) { [void]$allTypes.Add($typeName) }
        foreach ($typeName in $ExtraTypes) { [void]$allTypes.Add($typeName) }

        foreach ($typeName in @($allTypes | Sort-Object)) {
            Write-ObfuscarSkipType -Writer $writer -Name $typeName
        }

        foreach ($namespaceName in @($ExtraNamespaces | Sort-Object -Unique)) {
            $writer.WriteStartElement('SkipNamespace')
            $writer.WriteAttributeString('name', $namespaceName)
            $writer.WriteEndElement()
        }

        $writer.WriteEndElement() # Module
        $writer.WriteEndElement() # Obfuscator
        $writer.WriteEndDocument()
    }
    finally {
        $writer.Dispose()
    }
}

function Write-ReflectionRiskReport {
    param(
        [Parameter(Mandatory)][string]$ProjectRoot,
        [Parameter(Mandatory)][string]$OutputPath
    )

    $patterns = @(
        'Type\.GetType\s*\(',
        'Assembly\.GetType\s*\(',
        '\.GetMethod\s*\(\s*"',
        '\.GetProperty\s*\(\s*"',
        '\.GetField\s*\(\s*"',
        '\.GetEvent\s*\(\s*"',
        'Activator\.CreateInstance\s*\('
    )

    $findings = New-Object 'System.Collections.Generic.List[string]'

    Get-ChildItem -LiteralPath $ProjectRoot -Filter *.cs -File -Recurse -ErrorAction SilentlyContinue |
        Where-Object {
            $_.FullName -notmatch '[\\/](bin|obj|artifacts|Releases)[\\/]'
        } |
        ForEach-Object {
            $lineNumber = 0
            foreach ($line in [System.IO.File]::ReadLines($_.FullName)) {
                $lineNumber++
                foreach ($pattern in $patterns) {
                    if ($line -match $pattern) {
                        $relative = $_.FullName.Substring($ProjectRoot.Length).TrimStart('\', '/')
                        $findings.Add("$relative`:$lineNumber`t$($line.Trim())")
                        break
                    }
                }
            }
        }

    $header = @(
        'Recherche indicative des appels par réflexion.',
        'Une ligne trouvée n’est pas forcément un problème.',
        'Si un type ou membre privé est recherché par son nom original, ajoute son type à obfuscation-exclusions.txt.',
        ''
    )

    [System.IO.File]::WriteAllLines($OutputPath, @($header + @($findings)), (New-Object System.Text.UTF8Encoding($false)))
    return $findings.Count
}

function Invoke-Obfuscation {
    param(
        [Parameter(Mandatory)][string]$ProjectRoot,
        [Parameter(Mandatory)][string]$RawPublishDirectory,
        [Parameter(Mandatory)][string]$PackageDirectory,
        [Parameter(Mandatory)][string]$AssemblyFileName,
        [Parameter(Mandatory)][string]$Version,
        [Parameter(Mandatory)][string]$ObfuscarExecutable,
        [Parameter(Mandatory)][string]$ExclusionFile,
        [Parameter(Mandatory)][string]$ObfuscationRoot
    )

    $rawAssembly = Join-Path $RawPublishDirectory $AssemblyFileName
    if (-not (Test-Path -LiteralPath $rawAssembly)) {
        throw "Assemblage à obfusquer introuvable : $rawAssembly"
    }

    $outputDirectory = Join-Path $ObfuscationRoot 'out'
    $mapDirectory = Join-Path $ObfuscationRoot 'maps'
    $configurationPath = Join-Path $ObfuscationRoot 'obfuscar.generated.xml'
    $mappingPath = Join-Path $mapDirectory "mapping-$Version.xml"
    $riskReportPath = Join-Path $ObfuscationRoot 'reflection-scan.txt'

    if (Test-Path -LiteralPath $outputDirectory) {
        Remove-Item -LiteralPath $outputDirectory -Recurse -Force
    }

    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
    New-Item -ItemType Directory -Path $mapDirectory -Force | Out-Null

    $xamlClasses = Get-XamlClassNames -ProjectRoot $ProjectRoot
    $customExclusions = Get-CustomObfuscationExclusions -Path $ExclusionFile
    $riskCount = Write-ReflectionRiskReport -ProjectRoot $ProjectRoot -OutputPath $riskReportPath

    Write-Host "Classes WPF protégées : $($xamlClasses.Count)" -ForegroundColor DarkGray
    Write-Host "Exclusions ajoutées    : $($customExclusions.Types.Count) type(s), $($customExclusions.Namespaces.Count) espace(s) de noms" -ForegroundColor DarkGray
    Write-Host "Appels par réflexion   : $riskCount occurrence(s), rapport dans artifacts\obfuscation" -ForegroundColor DarkGray

    New-ObfuscarConfiguration `
        -InputDirectory $RawPublishDirectory `
        -OutputDirectory $outputDirectory `
        -AssemblyPath $rawAssembly `
        -MappingPath $mappingPath `
        -ConfigurationPath $configurationPath `
        -XamlClasses $xamlClasses `
        -ExtraTypes $customExclusions.Types `
        -ExtraNamespaces $customExclusions.Namespaces

    $rawHash = (Get-FileHash -LiteralPath $rawAssembly -Algorithm SHA256).Hash

    [void](Invoke-NativeProcess -FilePath $ObfuscarExecutable -Arguments @(
        $configurationPath
    ) -FailureMessage "Obfuscar a échoué")

    $obfuscatedAssembly = Join-Path $outputDirectory $AssemblyFileName
    if (-not (Test-Path -LiteralPath $obfuscatedAssembly)) {
        throw "Obfuscar n'a pas produit l'assemblage attendu : $obfuscatedAssembly"
    }

    $obfuscatedHash = (Get-FileHash -LiteralPath $obfuscatedAssembly -Algorithm SHA256).Hash
    if ($rawHash -eq $obfuscatedHash) {
        throw "L'assemblage généré est identique à l'original : l'obfuscation n'a pas été appliquée."
    }

    $fileInfo = Get-Item -LiteralPath $obfuscatedAssembly
    if ($fileInfo.Length -lt 4096) {
        throw "L'assemblage obfusqué est anormalement petit : $($fileInfo.Length) octets."
    }

    $stream = [System.IO.File]::OpenRead($obfuscatedAssembly)
    try {
        $first = $stream.ReadByte()
        $second = $stream.ReadByte()
    }
    finally {
        $stream.Dispose()
    }

    if ($first -ne 0x4D -or $second -ne 0x5A) {
        throw "L'assemblage obfusqué ne possède pas un en-tête PE Windows valide."
    }

    Copy-Item -LiteralPath $obfuscatedAssembly -Destination (Join-Path $PackageDirectory $AssemblyFileName) -Force

    $hashReport = @(
        "Version=$Version",
        "OriginalSHA256=$rawHash",
        "ObfuscatedSHA256=$obfuscatedHash",
        "Mapping=$mappingPath",
        "Configuration=$configurationPath"
    )
    [System.IO.File]::WriteAllLines(
        (Join-Path $ObfuscationRoot "hashes-$Version.txt"),
        $hashReport,
        (New-Object System.Text.UTF8Encoding($false))
    )

    Write-Host "Assemblage obfusqué et contrôlé." -ForegroundColor Green
    Write-Host "Table privée : $mappingPath" -ForegroundColor Yellow
}

try {
    Clear-Host
    Write-Host "=== PommeBrowser - Build + Obfuscation + Velopack + GitHub ===`n" -ForegroundColor Cyan

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

    Require-Command 'dotnet'
    Require-Command 'gh'

    $csproj = Get-ChildItem -Path $scriptDir -Filter *.csproj -File -ErrorAction SilentlyContinue |
        Select-Object -First 1

    if (-not $csproj) {
        throw "Aucun fichier .csproj trouvé dans : $scriptDir"
    }

    $metadata = Get-ProjectMetadata -ProjectPath $csproj.FullName
    $vpk = Get-LocalDotNetTool `
        -PackageId 'vpk' `
        -Version $metadata.VelopackVersion `
        -ExecutableCandidates @('vpk.exe') `
        -ToolFolderName 'vpk'

    $obfuscar = $null
    if (-not $SkipObfuscation) {
        $obfuscar = Get-LocalDotNetTool `
            -PackageId 'Obfuscar.GlobalTool' `
            -Version $ObfuscarVersion `
            -ExecutableCandidates @('obfuscar.console.exe', 'Obfuscar.Console.exe', 'obfuscar.exe') `
            -ToolFolderName 'obfuscar'
    }

    Write-Host "Projet             : $($csproj.Name)" -ForegroundColor DarkGray
    Write-Host "Velopack NuGet/vpk : $($metadata.VelopackVersion)" -ForegroundColor DarkGray
    if ($SkipObfuscation) {
        Write-Host "Obfuscation        : désactivée pour ce build" -ForegroundColor Yellow
    }
    else {
        Write-Host "Obfuscar           : $ObfuscarVersion, mode prudent WPF" -ForegroundColor DarkGray
    }

    $packTitle = Read-HostWithDefault -Prompt 'Nom affiché du logiciel' -DefaultValue 'PommeBrowser'
    $version = Read-HostWithDefault -Prompt 'Version à publier' -DefaultValue $metadata.AppVersion

    if ($version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$') {
        throw "Version invalide : '$version'. Utilise SemVer, par exemple 0.9.6 ou 0.9.6-beta.1."
    }

    $packId = 'com.vazer7070.myhomelabbrowser'
    $mainExe = 'MyHomelabBrowser.exe'
    $mainAssembly = 'MyHomelabBrowser.dll'
    $repoSlug = 'vazer7070/PommeBrowser-release'
    $repoUrl = "https://github.com/$repoSlug"
    $channel = 'win'
    $tag = $version

    $rawPublishDir = Join-Path $scriptDir 'artifacts\publish\win-x64-raw'
    $packageDir = Join-Path $scriptDir 'artifacts\publish\win-x64'
    $obfuscationRoot = Join-Path $scriptDir 'artifacts\obfuscation'
    $releasesDir = Join-Path $scriptDir 'Releases'
    $iconPath = Join-Path $scriptDir 'PommeBrowser_desktop.ico'
    $exclusionFile = Join-Path $scriptDir 'obfuscation-exclusions.txt'

    Write-Host "`n==> Vérification de l'authentification GitHub..." -ForegroundColor Cyan
    $tokenResult = Invoke-NativeProcess -FilePath 'gh' -Arguments @('auth', 'token') `
        -FailureMessage "Impossible d'obtenir le jeton GitHub depuis gh" -Quiet
    $token = $tokenResult.StdOut.Trim()

    if ([string]::IsNullOrWhiteSpace($token)) {
        throw 'Jeton GitHub vide. Exécute : gh auth login --web'
    }

    $state = Get-GitHubReleaseState -Repository $repoSlug -Tag $tag
    if ($state.ReleaseExists -or $state.TagExists) {
        Write-Host "`nLa version '$tag' existe déjà dans le dépôt de releases." -ForegroundColor Yellow
        $overwrite = (Read-Host 'Supprimer la release/le tag existant et recréer cette version ? (o/n)').Trim().ToLowerInvariant()

        if ($overwrite -notin @('o', 'oui', 'y', 'yes')) {
            throw 'Publication annulée : version déjà existante.'
        }

        if ($state.ReleaseExists) {
            [void](Invoke-NativeProcess -FilePath 'gh' -Arguments @(
                'release', 'delete', $tag,
                '--repo', $repoSlug,
                '--yes',
                '--cleanup-tag'
            ) -FailureMessage "Suppression de l'ancienne release impossible")
        }
        elseif ($state.TagExists) {
            [void](Invoke-NativeProcess -FilePath 'gh' -Arguments @(
                'api', '--method', 'DELETE',
                "repos/$repoSlug/git/refs/tags/$tag"
            ) -FailureMessage "Suppression de l'ancien tag de release impossible")
        }
    }

    New-Item -ItemType Directory -Path $releasesDir -Force | Out-Null

    Write-Host "`n==> 1/5 Récupération de la release précédente..." -ForegroundColor Cyan
    $downloadResult = Invoke-NativeProcess -FilePath $vpk -Arguments @(
        'download', 'github',
        '--repoUrl', $repoUrl,
        '--token', $token,
        '--channel', $channel,
        '--outputDir', $releasesDir
    ) -FailureMessage 'Téléchargement de la release précédente impossible' -AllowFailure

    if ($downloadResult.ExitCode -ne 0) {
        Write-Host 'Aucune release précédente récupérée : première publication ou aucune version compatible.' -ForegroundColor Yellow
    }

    Write-Host "`n==> 2/5 Publication .NET brute..." -ForegroundColor Cyan
    if (Test-Path -LiteralPath $rawPublishDir) {
        Remove-Item -LiteralPath $rawPublishDir -Recurse -Force
    }
    New-Item -ItemType Directory -Path $rawPublishDir -Force | Out-Null

    [void](Invoke-NativeProcess -FilePath 'dotnet' -Arguments @(
        'publish', $csproj.FullName,
        '-c', 'Release',
        '-r', 'win-x64',
        '--self-contained', 'true',
        '-o', $rawPublishDir,
        "-p:Version=$version",
        '-p:DebugType=portable',
        '-p:DebugSymbols=true'
    ) -FailureMessage 'dotnet publish a échoué')

    $mainExePath = Join-Path $rawPublishDir $mainExe
    $mainAssemblyPath = Join-Path $rawPublishDir $mainAssembly
    if (-not (Test-Path -LiteralPath $mainExePath)) {
        throw "L'exécutable principal est absent après publication : $mainExePath"
    }
    if (-not (Test-Path -LiteralPath $mainAssemblyPath)) {
        throw "L'assemblage principal est absent après publication : $mainAssemblyPath"
    }

    Copy-DirectoryContents -Source $rawPublishDir -Destination $packageDir

    Write-Host "`n==> 3/5 Protection du code..." -ForegroundColor Cyan
    if ($SkipObfuscation) {
        Write-Host 'Obfuscation ignorée à la demande. Le package contiendra le code .NET normal.' -ForegroundColor Yellow
    }
    else {
        Invoke-Obfuscation `
            -ProjectRoot $scriptDir `
            -RawPublishDirectory $rawPublishDir `
            -PackageDirectory $packageDir `
            -AssemblyFileName $mainAssembly `
            -Version $version `
            -ObfuscarExecutable $obfuscar `
            -ExclusionFile $exclusionFile `
            -ObfuscationRoot $obfuscationRoot
    }

    if (-not $KeepSymbols) {
        Get-ChildItem -LiteralPath $packageDir -Filter *.pdb -File -Recurse -ErrorAction SilentlyContinue |
            Remove-Item -Force
    }

    Write-Host "`n==> 4/5 Création des paquets Velopack..." -ForegroundColor Cyan
    $packArguments = @(
        'pack',
        '--packId', $packId,
        '--packVersion', $version,
        '--packTitle', $packTitle,
        '--mainExe', $mainExe,
        '--packDir', $packageDir,
        '--outputDir', $releasesDir,
        '--channel', $channel,
        '--runtime', 'win-x64',
        '--yes'
    )

    if (Test-Path -LiteralPath $iconPath) {
        $packArguments += @('--icon', $iconPath)
    }

    [void](Invoke-NativeProcess -FilePath $vpk -Arguments $packArguments -FailureMessage 'vpk pack a échoué')

    Write-Host "`n==> 5/5 Publication sur GitHub Releases..." -ForegroundColor Cyan
    [void](Invoke-NativeProcess -FilePath $vpk -Arguments @(
        'upload', 'github',
        '--repoUrl', $repoUrl,
        '--token', $token,
        '--tag', $tag,
        '--releaseName', "$packTitle $version",
        '--channel', $channel,
        '--publish',
        '--outputDir', $releasesDir
    ) -FailureMessage 'vpk upload github a échoué')

    $changelogPath = Join-Path $releasesDir 'changelog.json'
    if (Test-Path -LiteralPath $changelogPath) {
        Write-Host "`n==> Ajout de changelog.json à la release..." -ForegroundColor Cyan
        [void](Invoke-NativeProcess -FilePath 'gh' -Arguments @(
            'release', 'upload', $tag,
            $changelogPath,
            '--repo', $repoSlug,
            '--clobber'
        ) -FailureMessage 'Envoi de changelog.json impossible')
    }

    Write-Host "`nSUCCÈS : la release $tag est publiée." -ForegroundColor Green
    Write-Host "Dépôt             : $repoUrl" -ForegroundColor DarkGray
    Write-Host "Paquets locaux    : $releasesDir" -ForegroundColor DarkGray
    Write-Host "Build brut privé  : $rawPublishDir" -ForegroundColor DarkGray
    if (-not $SkipObfuscation) {
        Write-Host "Mapping privé     : $(Join-Path $obfuscationRoot "maps\mapping-$version.xml")" -ForegroundColor Yellow
    }
}
catch {
    Write-Host "`nERREUR :" -ForegroundColor Red
    Write-Host $_.Exception.Message -ForegroundColor Red
}
finally {
    Write-Host "`nAppuie sur Entrée pour fermer..."
    [void](Read-Host)
}
