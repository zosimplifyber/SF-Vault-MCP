<#
Stages SFVaultBridge.bundle (PackageContents.xml + Contents) under OutDir from a built add-in.
Shared by Install-VaultBridge.ps1 (dev loop) and build-release.ps1 (release). Does not build anything.
The .addin in Contents keeps the {{ASSEMBLY}} placeholder; Install-Package.ps1 fills in the installed path.
#>
param(
    [Parameter(Mandatory)][string]$OutDir,
    [Parameter(Mandatory)][string]$Tool,
    [string]$Configuration = 'Release'
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$version = (Get-Content (Join-Path $root 'VERSION') -Raw).Trim()
$bin = Join-Path $root "SFVaultBridge.Addin\bin\$Configuration\net8.0-windows"
if (-not (Test-Path (Join-Path $bin 'SFVaultBridge.Addin.dll'))) { throw "Not built: $bin" }
if (-not (Test-Path $Tool)) { throw "Missing $Tool" }

$bundle = Join-Path $OutDir 'SFVaultBridge.bundle'
if (Test-Path $bundle) { Remove-Item $bundle -Recurse -Force }
$contents = Join-Path $bundle 'Contents'
New-Item -ItemType Directory -Force -Path (Join-Path $contents 'tools') | Out-Null
Get-ChildItem $bin -Filter 'SFVaultBridge.*' -File | Where-Object { $_.Extension -ne '.pdb' } |
    ForEach-Object { Copy-Item $_.FullName $contents -Force }
Copy-Item $Tool (Join-Path $contents 'tools\SFVaultTools.exe') -Force
Copy-Item (Join-Path $root 'manifest\SFVaultBridge.addin') (Join-Path $contents 'SFVaultBridge.addin') -Force
$pc = (Get-Content (Join-Path $root 'manifest\PackageContents.xml') -Raw).Replace('{{VERSION}}', $version)
[IO.File]::WriteAllText((Join-Path $bundle 'PackageContents.xml'), $pc, (New-Object Text.UTF8Encoding($false)))
Write-Host "Staged $bundle (version $version)"
