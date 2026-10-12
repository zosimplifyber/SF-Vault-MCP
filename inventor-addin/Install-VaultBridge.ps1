<#
Builds the SF Vault Bridge and installs it for the current Windows user. No admin rights.
Close Inventor first; start it again afterwards and sign in to Vault.
#>
param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'

if (Get-Process Inventor -ErrorAction SilentlyContinue) {
    throw 'Close Inventor before installing the SF Vault Bridge.'
}
$root = $PSScriptRoot
# Per-user ApplicationPlugins folder, like Vault's own add-in. Token and bridge.log stay in the data folder.
$dest = Join-Path $env:APPDATA 'Autodesk\ApplicationPlugins\SFVaultBridge'
$data = Join-Path $env:APPDATA 'Simplifyber\VaultBridge'
$oldManifest = Join-Path $env:APPDATA 'Autodesk\Inventor 2026\Addins\SFVaultBridge.addin'

dotnet build (Join-Path $root 'SFVaultBridge.Addin\SFVaultBridge.Addin.csproj') -c $Configuration --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

$out = Join-Path $root "SFVaultBridge.Addin\bin\$Configuration\net8.0-windows"
New-Item -ItemType Directory -Force -Path $dest | Out-Null
Copy-Item (Join-Path $out 'SFVaultBridge.*') $dest -Force

$assembly = Join-Path $dest 'SFVaultBridge.Addin.dll'
$manifest = (Get-Content (Join-Path $root 'manifest\SFVaultBridge.addin') -Raw).Replace('{{ASSEMBLY}}', $assembly)
Set-Content -Path (Join-Path $dest 'SFVaultBridge.addin') -Value $manifest -Encoding UTF8

# Clean up an older install (manifest in Inventor's Addins folder, DLLs in the data folder).
Remove-Item -LiteralPath $oldManifest -Force -ErrorAction SilentlyContinue
Remove-Item -Path (Join-Path $data 'SFVaultBridge.*') -Force -ErrorAction SilentlyContinue

Write-Host "Installed SF Vault Bridge to $dest"
Write-Host 'Start Inventor and sign in to Vault (Vault tab, Log In).'
