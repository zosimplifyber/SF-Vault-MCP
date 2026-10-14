<#
Builds the SF Vault Bridge release: release\SF Vault Bridge <version>\ and release\SF Vault Bridge <version>.zip.
Version comes from inventor-addin\VERSION. Needs the .NET 8 SDK and py -3.14 (PyInstaller, reportlab) on this PC only.
  -ExtensionDir  folder with the Claude Desktop extension (build.ps1 inside); default .\claude-extension
  -ForceTools    always rebuild SFVaultTools.exe (default: rebuild when the Python sources are newer)
Publish afterwards with: py -3.14 scripts\vault_bridge.py publish-release "release\SF Vault Bridge <version>.zip"
#>
param(
    [string]$ExtensionDir = (Join-Path $PSScriptRoot 'claude-extension'),
    [switch]$ForceTools,
    [string]$Configuration = 'Release'
)
$ErrorActionPreference = 'Stop'
$repo = $PSScriptRoot
$addin = Join-Path $repo 'inventor-addin'
$version = (Get-Content (Join-Path $addin 'VERSION') -Raw).Trim()
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "inventor-addin\VERSION must be x.y.z, got '$version'" }
$name = "SF Vault Bridge $version"
$releaseRoot = Join-Path $repo 'release'
$out = Join-Path $releaseRoot $name
$zip = Join-Path $releaseRoot "$name.zip"

Write-Host "== Build $name"
dotnet build (Join-Path $addin 'SFVaultBridge.Addin\SFVaultBridge.Addin.csproj') -c $Configuration --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

Write-Host '== SFVaultTools.exe'
$tool = Join-Path $addin 'tools\SFVaultTools.exe'
$sources = 'vault_tools_cli.py','review_package.py','manufacturing_package.py','bom_purchasing.py','purchasing_reference.py','pdf_watermark.py','vault_state.py' |
    ForEach-Object { Join-Path $repo $_ } | Where-Object { Test-Path $_ }
$stale = (Test-Path $tool) -and (@($sources | Where-Object { (Get-Item $_).LastWriteTime -gt (Get-Item $tool).LastWriteTime }).Count -gt 0)
if ($ForceTools -or -not (Test-Path $tool) -or $stale) {
    & cmd /c (Join-Path $repo 'build_vault_tools_exe.bat')
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $tool)) { throw 'Building SFVaultTools.exe failed.' }
} else { Write-Host 'SFVaultTools.exe is up to date; reusing it.' }

Write-Host '== Assemble'
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
if (Test-Path $zip) { Remove-Item $zip -Force }
New-Item -ItemType Directory -Force -Path $out | Out-Null
& (Join-Path $addin 'New-Bundle.ps1') -OutDir $out -Tool $tool -Configuration $Configuration
Copy-Item (Join-Path $addin 'Install.cmd') $out
Copy-Item (Join-Path $addin 'Install-Package.ps1') $out
Copy-Item (Join-Path $addin 'manifest\team-settings.json') $out

Write-Host '== Claude Desktop extension'
$extBuild = Join-Path $ExtensionDir 'build.ps1'
if (Test-Path $extBuild) {
    & $extBuild -Version $version
    $mcpb = Join-Path $ExtensionDir 'dist\SF Vault Bridge.mcpb'
    if (-not (Test-Path $mcpb)) { throw "Extension build.ps1 ran but $mcpb is missing." }
    Copy-Item $mcpb (Join-Path $out 'SF Vault Bridge.mcpb')
} else {
    Write-Host "EXTENSION NOT BUILT: $extBuild not found. The package has no SF Vault Bridge.mcpb."
}

Write-Host '== Install guide'
& py -3.14 (Join-Path $repo 'scripts\build_install_guide.py') (Join-Path $out 'Install guide.pdf') $version
if ($LASTEXITCODE -ne 0) { throw 'Building the install guide failed.' }

Write-Host '== Zip'
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($out, $zip, [IO.Compression.CompressionLevel]::Optimal, $true)
Write-Host "Built $zip"
