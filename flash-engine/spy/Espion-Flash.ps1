<#
.SYNOPSIS
    Installe ou retire l'espion NPAPI de PommeBrowser devant le module Flash.

.DESCRIPTION
    L'espion (npPommeEspion.dll) se place entre le navigateur et le vrai module Flash et note
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
    Dossier des modules (par défaut : celui de PommeBrowser).

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\Espion-Flash.ps1
    powershell -ExecutionPolicy Bypass -File .\Espion-Flash.ps1 -Retirer
#>
param(
    [switch]$Retirer,
    [string]$Dossier = (Join-Path $env:LOCALAPPDATA 'PommeBrowser\plugins')
)

$ErrorActionPreference = 'Stop'
$rangement = Join-Path $Dossier 'espion'
$journaux = Join-Path $rangement 'journaux'

Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
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

    /// Description de version (RT_VERSION) de source copiée dans cible. Faux si source n'en a pas.
    public static bool CopierVersion(string source, string cible)
    {
        IntPtr module = LoadLibraryExW(source, IntPtr.Zero, LoadLibraryAsDatafile | LoadLibraryAsImageResource);
        if (module == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Module illisible : " + source);
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
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Espion non modifiable : " + cible);
        if (!UpdateResourceW(mise, Version, Premiere, 0x0409, donnees, (uint)donnees.Length))
        {
            int erreur = Marshal.GetLastWin32Error();
            EndUpdateResourceW(mise, true);
            throw new Win32Exception(erreur, "Description de version non copiée.");
        }
        if (!EndUpdateResourceW(mise, false))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Description de version non enregistrée.");
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

if ($Retirer) {
    $reels = @(Get-ChildItem -Path $rangement -Filter 'NPSWF64_*.dll' -File -ErrorAction SilentlyContinue)
    if ($reels.Count -eq 0) {
        Write-Host "L'espion n'est pas installé dans $Dossier."
    }
    foreach ($reel in $reels) {
        Move-Item -Force -Path $reel.FullName -Destination (Join-Path $Dossier $reel.Name)
        Write-Host "Module remis en place : $($reel.Name)"
    }
    if (Test-Path $journaux) {
        $bureau = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Journaux espion Flash'
        New-Item -ItemType Directory -Force -Path $bureau | Out-Null
        Copy-Item -Force -Path (Join-Path $journaux '*') -Destination $bureau
        Write-Host "Journaux copiés dans : $bureau"
    }
    return
}

$espion = Join-Path $PSScriptRoot 'npPommeEspion.dll'
if (-not (Test-Path $espion)) {
    throw "npPommeEspion.dll introuvable à côté du script ($PSScriptRoot)."
}
if (@(Get-ChildItem -Path $rangement -Filter 'NPSWF64_*.dll' -File -ErrorAction SilentlyContinue).Count -gt 0) {
    throw "L'espion est déjà installé (le vrai module est dans $rangement). Retirez-le d'abord avec -Retirer."
}
$modules = @(Get-ChildItem -Path $Dossier -Filter 'NPSWF64_*.dll' -File)
if ($modules.Count -ne 1) {
    throw "Un seul module Flash (NPSWF64_*.dll) attendu dans $Dossier, $($modules.Count) trouvé(s)."
}

$module = $modules[0]
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
Write-Host "Journaux : $journaux"
