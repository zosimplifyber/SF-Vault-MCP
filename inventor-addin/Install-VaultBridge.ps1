<#
Dev loop: builds the SF Vault Bridge and installs it for the current Windows user as an app bundle
(%APPDATA%\Autodesk\ApplicationPlugins\SFVaultBridge.bundle). No admin rights.
Close Inventor first; start it again afterwards and sign in to Vault.
Engineers use the release zip (build-release.ps1) instead.
#>
param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'

if (Get-Process Inventor -ErrorAction SilentlyContinue) {
    throw 'Close Inventor before installing the SF Vault Bridge.'
}
$root = $PSScriptRoot
$repo = Split-Path $root -Parent

dotnet build (Join-Path $root 'SFVaultBridge.Addin\SFVaultBridge.Addin.csproj') -c $Configuration --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

$tool = Join-Path $root 'tools\SFVaultTools.exe'
$sources = 'vault_tools_cli.py','review_package.py','manufacturing_package.py','bom_purchasing.py','purchasing_reference.py','pdf_watermark.py','vault_state.py' |
    ForEach-Object { Join-Path $repo $_ } | Where-Object { Test-Path $_ }
$stale = (Test-Path $tool) -and (@($sources | Where-Object { (Get-Item $_).LastWriteTime -gt (Get-Item $tool).LastWriteTime }).Count -gt 0)
if (-not (Test-Path $tool) -or $stale) {
    & cmd /c (Join-Path $repo 'build_vault_tools_exe.bat')
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $tool)) { throw 'Building SFVaultTools.exe failed.' }
}

# Stage the bundle, then install it exactly like an engineer's install (no team-settings.json here, so settings stay as they are).
$stage = Join-Path $repo 'build\dev-bundle'
New-Item -ItemType Directory -Force -Path $stage | Out-Null
& (Join-Path $root 'New-Bundle.ps1') -OutDir $stage -Tool $tool -Configuration $Configuration
& (Join-Path $root 'Install-Package.ps1') -PackageDir $stage
