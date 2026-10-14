<#
Installs the SF Vault Bridge bundle for the current Windows user. No admin rights, nothing but Inventor 2026 needed.
Run through Install.cmd. Parameters exist for testing and for the dev installer:
  -PackageDir  folder that holds SFVaultBridge.bundle (and team-settings.json); default: next to this script
  -Target      ApplicationPlugins folder to install into; default %APPDATA%\Autodesk\ApplicationPlugins
  -DataDir     settings/token folder; default %APPDATA%\Simplifyber\VaultBridge
  -SkipInventorCheck  do not require Inventor 2026 and do not look for a running Inventor (tests)
#>
param(
    [string]$PackageDir = $PSScriptRoot,
    [string]$Target = (Join-Path $env:APPDATA 'Autodesk\ApplicationPlugins'),
    [string]$DataDir = (Join-Path $env:APPDATA 'Simplifyber\VaultBridge'),
    [string]$OldAddinsDir = (Join-Path $env:APPDATA 'Autodesk\Inventor 2026\Addins'),
    [switch]$SkipInventorCheck
)
$ErrorActionPreference = 'Stop'
$TeamKeys = 'processValues', 'printMaterials', 'dropboxAppKey', 'salesTaxRate'

function Test-Empty($v) {
    # Empty team value: null, blank text or an empty list. Never replaces a non-empty user value.
    if ($null -eq $v) { return $true }
    if ($v -is [string]) { return [string]::IsNullOrWhiteSpace($v) }
    if ($v -is [System.Collections.IEnumerable]) { return (@($v)).Count -eq 0 }
    return $false
}

function Merge-TeamSettings([string]$teamFile, [string]$settingsFile) {
    # Same rules as SettingsMerge in SFVaultBridge.Core: team keys overwrite (unless empty over non-empty),
    # a team key listed in the file userOverrides is skipped, every other key (recentPartners) stays, a malformed file is backed up and never discarded.
    $team = Get-Content -LiteralPath $teamFile -Raw | ConvertFrom-Json
    $merged = $null
    if (Test-Path -LiteralPath $settingsFile) {
        $raw = Get-Content -LiteralPath $settingsFile -Raw
        if (-not [string]::IsNullOrWhiteSpace($raw)) {
            try { $merged = $raw | ConvertFrom-Json } catch { $merged = $null }
            if ($merged -isnot [pscustomobject]) {
                $backup = "$settingsFile.bad-" + (Get-Date -Format 'yyyyMMdd-HHmmss')
                Copy-Item -LiteralPath $settingsFile $backup
                Write-Host "WARNING: $settingsFile was not valid settings JSON. Kept a copy at $backup and wrote a fresh file."
                $merged = $null
            }
        }
    }
    if (-not $merged) { $merged = [pscustomobject]@{} }
    $overridden = @()
    if ($merged.PSObject.Properties.Name -contains 'userOverrides' -and $merged.userOverrides -is [System.Collections.IEnumerable] -and $merged.userOverrides -isnot [string]) { $overridden = @($merged.userOverrides) }
    foreach ($key in $TeamKeys) {
        if ($overridden -ccontains $key) { continue }  # the user changed it in the Settings window
        if ($team.PSObject.Properties.Name -notcontains $key) { continue }
        $value = $team.$key
        if ((Test-Empty $value) -and ($merged.PSObject.Properties.Name -contains $key) -and -not (Test-Empty $merged.$key)) { continue }
        $merged | Add-Member -NotePropertyName $key -NotePropertyValue $value -Force
    }
    New-Item -ItemType Directory -Force -Path (Split-Path $settingsFile) | Out-Null
    $tmp = "$settingsFile.tmp"
    [IO.File]::WriteAllText($tmp, (ConvertTo-Json -InputObject $merged -Depth 10), (New-Object Text.UTF8Encoding($false)))
    Move-Item -LiteralPath $tmp -Destination $settingsFile -Force  # swap in whole: a crash never leaves a half-written file
}

function Find-Inventor2026 {
    $candidates = @()
    foreach ($key in 'HKLM:\SOFTWARE\Autodesk\Inventor\RegistryVersion30.0', 'HKLM:\SOFTWARE\WOW6432Node\Autodesk\Inventor\RegistryVersion30.0') {
        try {
            $props = Get-ItemProperty -LiteralPath $key -ErrorAction Stop
            foreach ($name in 'InstallPath', 'InstallLocation', 'InventorLocation') { if ($props.$name) { $candidates += $props.$name } }
        } catch { }
    }
    foreach ($root in 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall', 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall') {
        try {
            Get-ChildItem -LiteralPath $root -ErrorAction Stop | ForEach-Object {
                $p = Get-ItemProperty -LiteralPath $_.PSPath
                if ($p.DisplayName -like 'Autodesk Inventor*2026*' -and $p.InstallLocation) { $candidates += $p.InstallLocation }
            }
        } catch { }
    }
    $candidates += 'C:\Program Files\Autodesk\Inventor 2026\Bin\Inventor.exe'
    foreach ($c in $candidates) {
        foreach ($exe in @($c, (Join-Path $c 'Bin\Inventor.exe'), (Join-Path $c 'Inventor.exe'))) {
            if ($exe -like '*.exe' -and (Test-Path -LiteralPath $exe)) { return $exe }
        }
    }
    return $null
}

if (-not $SkipInventorCheck) {
    if (-not (Find-Inventor2026)) { throw 'Autodesk Inventor 2026 was not found on this PC. The SF Vault Bridge needs it.' }
    while ($true) {
        $running = @(Get-Process Inventor -ErrorAction SilentlyContinue)
        if ($running.Count -eq 0) { break }
        Write-Host ('Inventor is running (process ID ' + (($running | ForEach-Object { $_.Id }) -join ', ') + '). Save your work and close it.')
        if (-not [Environment]::UserInteractive) { throw 'Close Inventor and run the installer again.' }
        Read-Host 'Press Enter after Inventor is closed' | Out-Null
    }
}

$source = Join-Path $PackageDir 'SFVaultBridge.bundle'
if (-not (Test-Path -LiteralPath (Join-Path $source 'PackageContents.xml'))) { throw "No SFVaultBridge.bundle in $PackageDir" }
$dest = Join-Path $Target 'SFVaultBridge.bundle'
$staging = Join-Path $Target 'SFVaultBridge.bundle.new'
$backupDir = Join-Path $Target 'SFVaultBridge.bundle.old'
New-Item -ItemType Directory -Force -Path $Target | Out-Null

# 1. Copy the new bundle beside the old one first: a failed copy leaves the working install untouched.
foreach ($leftover in $staging, $backupDir) { if (Test-Path -LiteralPath $leftover) { Remove-Item -LiteralPath $leftover -Recurse -Force } }
try {
    Copy-Item -LiteralPath $source $staging -Recurse -Force
    Get-ChildItem -LiteralPath $staging -Recurse -File | Unblock-File
} catch {
    Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
    throw "Could not copy the add-in, nothing was changed: $($_.Exception.Message)"
}

# 2. Older layout (ApplicationPlugins\SFVaultBridge): remove it when it is only ours, otherwise remove just our files so Inventor never sees two copies.
$old = Join-Path $Target 'SFVaultBridge'
if (Test-Path -LiteralPath $old) {
    $ours = { $_.Name -like 'SFVaultBridge.*' -or $_.Name -eq 'SFVaultTools.exe' }
    $foreign = @(Get-ChildItem -LiteralPath $old -Recurse -File | Where-Object { -not (& $ours) })
    if ($foreign.Count -eq 0) { Remove-Item -LiteralPath $old -Recurse -Force; Write-Host "Removed the old install folder $old" }
    else {
        Get-ChildItem -LiteralPath $old -Recurse -File | Where-Object { & $ours } | Remove-Item -Force
        Write-Host "Removed our old files from $old but left the folder: it holds files that are not ours ($($foreign[0].Name))."
    }
}
Remove-Item -LiteralPath (Join-Path $OldAddinsDir 'SFVaultBridge.addin') -Force -ErrorAction SilentlyContinue
if (Test-Path -LiteralPath $DataDir) {
    Get-ChildItem -LiteralPath $DataDir -File | Where-Object { $_.Name -like 'SFVaultBridge.Addin.*' -or $_.Name -like 'SFVaultBridge.Core.*' -or $_.Name -eq 'SFVaultBridge.addin' } |
        Remove-Item -Force
}

# 3. Swap.
if (Test-Path -LiteralPath $dest) {
    try { Move-Item -LiteralPath $dest $backupDir -ErrorAction Stop }
    catch {
        Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
        throw "Could not replace the installed add-in (is a file in use?). The old version is still installed: $($_.Exception.Message)"
    }
}
try { Move-Item -LiteralPath $staging $dest -ErrorAction Stop }
catch {
    if (Test-Path -LiteralPath $backupDir) { Move-Item -LiteralPath $backupDir $dest -ErrorAction SilentlyContinue }
    throw "Could not put the new add-in in place; the old version was restored: $($_.Exception.Message)"
}
if (Test-Path -LiteralPath $backupDir) { Remove-Item -LiteralPath $backupDir -Recurse -Force -ErrorAction SilentlyContinue }

$assembly = Join-Path $dest 'Contents\SFVaultBridge.Addin.dll'
$addin = Join-Path $dest 'Contents\SFVaultBridge.addin'
$text = (Get-Content -LiteralPath $addin -Raw).Replace('{{ASSEMBLY}}', $assembly)
[IO.File]::WriteAllText($addin, $text, (New-Object Text.UTF8Encoding($true)))

$team = Join-Path $PackageDir 'team-settings.json'
if (Test-Path -LiteralPath $team) { Merge-TeamSettings $team (Join-Path $DataDir 'settings.json'); Write-Host 'Team settings merged.' }

$uninstall = Join-Path $Target 'Uninstall SF Vault Bridge.cmd'
$lines = @(
    '@echo off',
    'tasklist /fi "imagename eq Inventor.exe" | find /i "Inventor.exe" >nul && (echo Close Inventor first. & pause & exit /b 1)',
    'rmdir /s /q "%~dp0SFVaultBridge.bundle"',
    'echo SF Vault Bridge removed. Settings and the token in %APPDATA%\Simplifyber\VaultBridge were kept.',
    'pause',
    '(goto) 2>nul & del "%~f0"'
)
Set-Content -Path $uninstall -Value $lines -Encoding ASCII

$version = ([xml](Get-Content (Join-Path $dest 'PackageContents.xml') -Raw)).ApplicationPackage.AppVersion
Write-Host "Installed SF Vault Bridge $version to $dest"
Write-Host ''
Write-Host 'Next:'
Write-Host '  1. Start Inventor. If it asks about the SF Vault Bridge add-in, open Add-In Manager (Tools tab) once and allow it (the package is not signed yet).'
Write-Host '  2. Sign in to Vault (Vault tab, Log In). The SF Vault Bridge tab shows a green dot when ready.'
Write-Host '  3. For Claude Desktop, double-click "SF Vault Bridge.mcpb" from this package.'
Write-Host "To remove it later, run: $uninstall"
