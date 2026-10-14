param([string]$Version)
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
if (-not $Version) {
    $vf = Join-Path $here '..\inventor-addin\VERSION'
    if (Test-Path $vf) { $Version = (Get-Content $vf -Raw).Trim() } else { $Version = '1.0.0' }
}
$stage = Join-Path ([IO.Path]::GetTempPath()) ("sfvb-mcpb-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage | Out-Null
try {
    Copy-Item (Join-Path $here 'server') (Join-Path $stage 'server') -Recurse
    $m = Get-Content (Join-Path $here 'manifest.json') -Raw | ConvertFrom-Json
    $m.version = $Version
    $json = $m | ConvertTo-Json -Depth 10
    [IO.File]::WriteAllText((Join-Path $stage 'manifest.json'), $json, (New-Object Text.UTF8Encoding($false)))
    $dist = Join-Path $here 'dist'
    New-Item -ItemType Directory -Force -Path $dist | Out-Null
    $out = Join-Path $dist 'SF Vault Bridge.mcpb'
    if (Test-Path $out) { Remove-Item $out -Force }
    $packed = $false
    if (Get-Command npx -ErrorAction SilentlyContinue) {
        try {
            & npx --no-install @anthropic-ai/mcpb pack $stage $out 2>&1 | Out-Host
            if ($LASTEXITCODE -eq 0 -and (Test-Path $out)) { $packed = $true }
        } catch { }
    }
    if (-not $packed) {
        Write-Host 'mcpb CLI not available; zipping with .NET'
        Add-Type -AssemblyName System.IO.Compression
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $zip = [IO.Compression.ZipFile]::Open($out, 'Create')
        try {
            Get-ChildItem $stage -Recurse -File | ForEach-Object {
                $rel = $_.FullName.Substring($stage.Length + 1).Replace('\', '/')
                [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $_.FullName, $rel, 'Optimal')
            }
        } finally { $zip.Dispose() }
    }
    Write-Host "Built $out (version $Version)"
} finally {
    Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
}
