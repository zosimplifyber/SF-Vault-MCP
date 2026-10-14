param([string]$Version)
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
if (-not $Version) {
    $vf = Join-Path $here '..\inventor-addin\VERSION'
    if (Test-Path $vf) { $Version = (Get-Content $vf -Raw).Trim() } else { $Version = '1.0.0' }
}
# Never leave an old build behind: drop everything in dist first so a failed build cannot be mistaken for a fresh one.
$dist = Join-Path $here 'dist'
if (Test-Path $dist) { Remove-Item (Join-Path $dist '*') -Recurse -Force }
$stage = Join-Path ([IO.Path]::GetTempPath()) ("sfvb-mcpb-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage | Out-Null
try {
    # The relay is a framework-dependent single-file win-x64 exe (needs the .NET 8 runtime Inventor 2026 installs).
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue) -and (Test-Path 'C:\Program Files\dotnet\dotnet.exe')) {
        $env:PATH = 'C:\Program Files\dotnet;' + $env:PATH
    }
    $serverDir = Join-Path $stage 'server'
    & dotnet publish (Join-Path $here 'relay\SFVaultBridgeRelay.csproj') -c Release -r win-x64 --self-contained false `
        -p:PublishSingleFile=true -p:Version=$Version -o $serverDir | Out-Host
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path (Join-Path $serverDir 'SFVaultBridgeRelay.exe'))) { throw 'dotnet publish of the relay failed.' }
    Get-ChildItem $serverDir -File | Where-Object { $_.Name -ne 'SFVaultBridgeRelay.exe' } | Remove-Item -Force
    $m = Get-Content (Join-Path $here 'manifest.json') -Raw | ConvertFrom-Json
    $m.version = $Version
    $json = $m | ConvertTo-Json -Depth 10
    [IO.File]::WriteAllText((Join-Path $stage 'manifest.json'), $json, (New-Object Text.UTF8Encoding($false)))
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
    # Check what was packed: the manifest (stamped with this version) and the relay exe must both be inside.
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $z = [IO.Compression.ZipFile]::OpenRead($out)
    try {
        $names = @($z.Entries | ForEach-Object { $_.FullName.Replace([string][char]92, '/') })
        foreach ($need in 'manifest.json', 'server/SFVaultBridgeRelay.exe') {
            if ($names -notcontains $need) { throw "Packed $out lacks $need (has: $($names -join ', '))." }
        }
        $entry = $z.GetEntry('manifest.json'); $rd = New-Object IO.StreamReader($entry.Open())
        try { $packedVersion = ($rd.ReadToEnd() | ConvertFrom-Json).version } finally { $rd.Dispose() }
        if ($packedVersion -ne $Version) { throw "Packed manifest version is $packedVersion, expected $Version." }
    } finally { $z.Dispose() }
    Write-Host "Built $out (version $Version)"
} finally {
    Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
}
