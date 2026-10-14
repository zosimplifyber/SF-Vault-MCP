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

function Merge-TeamSettings([string]$teamFile, [string]$settingsFile) {
    # Same rules as SettingsMerge in SFVaultBridge.Core: team keys overwrite, every other key (recentPartners) stays.
    $team = Get-Content $teamFile -Raw | ConvertFrom-Json
    $merged = $null
    if (Test-Path $settingsFile) {
        try { $merged = Get-Content $settingsFile -Raw | ConvertFrom-Json } catch { $merged = $null }
        if ($merged -isnot [pscustomobject]) { $merged = $null }
    }
    if (-not $merged) { $merged = [pscustomobject]@{} }
    foreach ($key in $TeamKeys) {
        if ($team.PSObject.Properties.Name -contains $key) {
            $merged | Add-Member -NotePropertyName $key -NotePropertyValue $team.$key -Force
        }
    }
    New-Item -ItemType Directory -Force -Path (Split-Path $settingsFile) | Out-Null
    [IO.File]::WriteAllText($settingsFile, (ConvertTo-Json -InputObject $merged -Depth 10), (New-Object Text.UTF8Encoding($false)))
}

if (-not $SkipInventorCheck) {
    $inventorExe = 'C:\Program Files\Autodesk\Inventor 2026\Bin\Inventor.exe'
    if (-not (Test-Path $inventorExe)) { throw 'Autodesk Inventor 2026 was not found on this PC. The SF Vault Bridge needs it.' }
    while ($true) {
        $running = @(Get-Process Inventor -ErrorAction SilentlyContinue)
        if ($running.Count -eq 0) { break }
        Write-Host ('Inventor is running (process ID ' + (($running | ForEach-Object { $_.Id }) -join ', ') + '). Save your work and close it.')
        if (-not [Environment]::UserInteractive) { throw 'Close Inventor and run the installer again.' }
        Read-Host 'Press Enter after Inventor is closed' | Out-Null
    }
}

$source = Join-Path $PackageDir 'SFVaultBridge.bundle'
if (-not (Test-Path (Join-Path $source 'PackageContents.xml'))) { throw "No SFVaultBridge.bundle in $PackageDir" }
$dest = Join-Path $Target 'SFVaultBridge.bundle'
New-Item -ItemType Directory -Force -Path $Target | Out-Null

# Remove the older layout (ApplicationPlugins\SFVaultBridge) only when it holds nothing but our files.
$old = Join-Path $Target 'SFVaultBridge'
if (Test-Path $old) {
    $foreign = @(Get-ChildItem $old -Recurse -File | Where-Object { $_.Name -notlike 'SFVaultBridge.*' -and $_.Name -ne 'SFVaultTools.exe' })
    if ($foreign.Count -eq 0) { Remove-Item $old -Recurse -Force; Write-Host "Removed the old install folder $old" }
    else { Write-Host "Left $old alone: it holds files that are not ours ($($foreign[0].Name))." }
}
Remove-Item -LiteralPath (Join-Path $OldAddinsDir 'SFVaultBridge.addin') -Force -ErrorAction SilentlyContinue
Remove-Item -Path (Join-Path $DataDir 'SFVaultBridge.*') -Force -ErrorAction SilentlyContinue

if (Test-Path $dest) { Remove-Item $dest -Recurse -Force }
Copy-Item $source $dest -Recurse -Force
Get-ChildItem $dest -Recurse -File | Unblock-File

$assembly = Join-Path $dest 'Contents\SFVaultBridge.Addin.dll'
$addin = Join-Path $dest 'Contents\SFVaultBridge.addin'
$text = (Get-Content $addin -Raw).Replace('{{ASSEMBLY}}', $assembly)
[IO.File]::WriteAllText($addin, $text, (New-Object Text.UTF8Encoding($true)))

$team = Join-Path $PackageDir 'team-settings.json'
if (Test-Path $team) { Merge-TeamSettings $team (Join-Path $DataDir 'settings.json'); Write-Host 'Team settings merged.' }

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
