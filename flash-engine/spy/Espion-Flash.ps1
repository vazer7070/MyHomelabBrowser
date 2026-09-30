<#
.SYNOPSIS
    Installe ou retire l'espion NPAPI de PommeBrowser devant le module Flash.

.DESCRIPTION
    L'espion (npPommeEspion.dll, ou npPommeEspion32.dll devant un module 32 bits NPSWF32_*.dll)
    se place entre le navigateur et le vrai module Flash et note
    chaque échange entre eux. Il prend le nom du module dans le dossier des modules de
    PommeBrowser : Pomme Legacy (Basilisk) et le moteur intégré le chargent alors tous les deux,
    et chacun laisse un journal dans <dossier>\espion\journaux.

    Le vrai module est rangé dans <dossier>\espion, et la description de version de l'espion est
    copiée de celle du module (Basilisk y lit le type des contenus et le mode de dessin pris en
    charge) : pour Basilisk, rien ne change.

    Fermez PommeBrowser (et Basilisk) avant d'installer ou de retirer l'espion.

.PARAMETER Retirer
    Remet le vrai module à sa place et copie les journaux sur le Bureau.

.PARAMETER Dossier
    Dossier des modules (par défaut : celui de PommeBrowser ; son module 32 bits est dans
    %LOCALAPPDATA%\PommeBrowser\plugins\x86). Pour un Basilisk lancé à part, qui prend le Flash
    installé dans Windows : C:\Windows\System32\Macromed\Flash (Basilisk 64 bits) ou
    C:\Windows\SysWOW64\Macromed\Flash (Basilisk 32 bits), PowerShell en administrateur. Le navigateur n'ayant alors pas le droit d'écrire à côté du module, les
    journaux vont dans %LOCALAPPDATA%\PommeBrowser\espion-journaux.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\Espion-Flash.ps1
    powershell -ExecutionPolicy Bypass -File .\Espion-Flash.ps1 -Retirer
    powershell -ExecutionPolicy Bypass -File .\Espion-Flash.ps1 -Dossier C:\Windows\System32\Macromed\Flash
    powershell -ExecutionPolicy Bypass -File .\Espion-Flash.ps1 -Dossier C:\Windows\SysWOW64\Macromed\Flash
#>
param(
    [switch]$Retirer,
    [string]$Dossier = (Join-Path $env:LOCALAPPDATA 'PommeBrowser\plugins')
)

$ErrorActionPreference = 'Stop'
$rangement = Join-Path $Dossier 'espion'
$journaux = Join-Path $rangement 'journaux'
# Journaux écrits par un navigateur qui n'a pas le droit d'écrire à côté du module (voir npspy.c).
$journauxSecours = Join-Path $env:LOCALAPPDATA 'PommeBrowser\espion-journaux'

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class RessourcesEspion
{
    const uint LoadLibraryAsDatafile = 0x2;
    const uint LoadLibraryAsImageResource = 0x20;
    static readonly IntPtr Version = (IntPtr)16;
    static readonly IntPtr Premiere = (IntPtr)1;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr LoadLibraryExW(string path, IntPtr file, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool FreeLibrary(IntPtr module);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr FindResourceW(IntPtr module, IntPtr name, IntPtr type);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr LoadResource(IntPtr module, IntPtr info);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr LockResource(IntPtr data);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern uint SizeofResource(IntPtr module, IntPtr info);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr BeginUpdateResourceW(string file, bool deleteExisting);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool UpdateResourceW(IntPtr update, IntPtr type, IntPtr name, ushort language, byte[] data, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool EndUpdateResourceW(IntPtr update, bool discard);

    // Types de System.Private.CoreLib seulement : Add-Type ne référence pas les mêmes
    // assemblys selon la version de PowerShell (Win32Exception y manque parfois).
    static Exception Erreur(string message)
    {
        return new InvalidOperationException(message + " (erreur Windows " + Marshal.GetLastWin32Error() + ")");
    }

    /// Description de version (RT_VERSION) de source copiée dans cible. Faux si source n'en a pas.
    public static bool CopierVersion(string source, string cible)
    {
        IntPtr module = LoadLibraryExW(source, IntPtr.Zero, LoadLibraryAsDatafile | LoadLibraryAsImageResource);
        if (module == IntPtr.Zero)
            throw Erreur("Module illisible : " + source);
        byte[] donnees;
        try
        {
            IntPtr info = FindResourceW(module, Premiere, Version);
            if (info == IntPtr.Zero)
                return false;
            uint taille = SizeofResource(module, info);
            donnees = new byte[taille];
            Marshal.Copy(LockResource(LoadResource(module, info)), donnees, 0, (int)taille);
        }
        finally
        {
            FreeLibrary(module);
        }

        IntPtr mise = BeginUpdateResourceW(cible, false);
        if (mise == IntPtr.Zero)
            throw Erreur("Espion non modifiable : " + cible);
        if (!UpdateResourceW(mise, Version, Premiere, 0x0409, donnees, (uint)donnees.Length))
        {
            Exception erreur = Erreur("Description de version non copiée.");
            EndUpdateResourceW(mise, true);
            throw erreur;
        }
        if (!EndUpdateResourceW(mise, false))
            throw Erreur("Description de version non enregistrée.");
        return true;
    }
}
'@

$ouverts = Get-Process -Name 'MyHomelabBrowser', 'PommeBrowser', 'PommeFlashHost', 'basilisk', 'plugin-container' -ErrorAction SilentlyContinue
if ($ouverts) {
    throw "Fermez d'abord PommeBrowser et Basilisk ($(@($ouverts.ProcessName | Sort-Object -Unique) -join ', '))."
}
if (-not (Test-Path $Dossier)) {
    throw "Dossier des modules introuvable : $Dossier. Importez d'abord votre module Flash dans PommeBrowser."
}
$administrateur = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if ($Dossier.StartsWith($env:WINDIR, [StringComparison]::OrdinalIgnoreCase) -and -not $administrateur) {
    throw "Le dossier $Dossier appartient à Windows : lancez PowerShell en tant qu'administrateur."
}

if ($Retirer) {
    $reels = @(Get-ChildItem -Path $rangement -Filter 'NPSWF*.dll' -File -ErrorAction SilentlyContinue)
    if ($reels.Count -eq 0) {
        Write-Host "L'espion n'est pas installé dans $Dossier."
    }
    foreach ($reel in $reels) {
        Move-Item -Force -Path $reel.FullName -Destination (Join-Path $Dossier $reel.Name)
        Write-Host "Module remis en place : $($reel.Name)"
    }
    $bureau = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Journaux espion Flash'
    foreach ($source in @($journaux, $journauxSecours)) {
        if (Test-Path (Join-Path $source '*.log')) {
            New-Item -ItemType Directory -Force -Path $bureau | Out-Null
            Copy-Item -Force -Path (Join-Path $source '*.log') -Destination $bureau
            Write-Host "Journaux copiés dans : $bureau"
        }
    }
    return
}

if (@(Get-ChildItem -Path $rangement -Filter 'NPSWF*.dll' -File -ErrorAction SilentlyContinue).Count -gt 0) {
    throw "L'espion est déjà installé (le vrai module est dans $rangement). Retirez-le d'abord avec -Retirer."
}
$modules = @(Get-ChildItem -Path $Dossier -Filter 'NPSWF*.dll' -File | Where-Object { $_.Name -match '^NPSWF(64|32)_' })
if ($modules.Count -ne 1) {
    throw "Un seul module Flash (NPSWF64_*.dll ou NPSWF32_*.dll) attendu dans $Dossier, $($modules.Count) trouvé(s)."
}

$module = $modules[0]
# Espion de l'architecture du module : un processus ne charge que des DLL de la sienne.
$nomEspion = if ($module.Name -match '^NPSWF32_') { 'npPommeEspion32.dll' } else { 'npPommeEspion.dll' }
$espion = Join-Path $PSScriptRoot $nomEspion
if (-not (Test-Path $espion)) {
    throw "$nomEspion introuvable à côté du script ($PSScriptRoot)."
}
New-Item -ItemType Directory -Force -Path $journaux | Out-Null
$reel = Join-Path $rangement $module.Name
Move-Item -Path $module.FullName -Destination $reel
try {
    Copy-Item -Path $espion -Destination $module.FullName
    if ([RessourcesEspion]::CopierVersion($reel, $module.FullName)) {
        $version = (Get-Item $module.FullName).VersionInfo
        Write-Host "Description de version copiée : $($version.FileDescription) $($version.FileVersion)"
    } else {
        Write-Warning "Le module n'a pas de description de version : Basilisk ne reconnaîtra pas l'espion comme Flash."
    }
} catch {
    Remove-Item -Force -ErrorAction SilentlyContinue -Path $module.FullName
    Move-Item -Force -Path $reel -Destination $module.FullName
    throw
}

Write-Host ""
Write-Host "Espion installé devant $($module.Name)."
Write-Host "1. Ouvrez la page dans Basilisk (Pomme Legacy) depuis PommeBrowser, jusqu'au problème, puis fermez l'onglet."
Write-Host "2. Ouvrez la même page avec le moteur Flash intégré, jusqu'au problème, puis fermez l'onglet."
Write-Host "3. Fermez PommeBrowser et lancez : .\Espion-Flash.ps1 -Retirer"
Write-Host "Journaux : $journaux (ou $journauxSecours si le navigateur ne peut pas écrire à côté du module)"
