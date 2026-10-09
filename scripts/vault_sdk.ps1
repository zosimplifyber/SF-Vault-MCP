# Vault SDK bridge -- parameterized PowerShell wrapper around the Vault .NET
# SDK. The Python side (scripts/vault_sdk.py) shells out to this script
# once per operation: arguments come in as a JSON string, results go out as
# a JSON line on stdout. Errors are written to stderr and the script exits
# with code 1.
#
# Operations exposed:
#   GetLifecycleStates              -> all lifecycle defs + states
#   GetItemPropertyDefinitions      -> all ITEM property defs
#   GetItemCategories               -> all ITEM categories
#   LookupItem                      -> single item by part number
#   LookupFile                      -> single file by master id
#   UpdateItemProperties            -> set properties on items
#   UpdateItemLifeCycleStates       -> promote items to a state
#   UpdateItemCategories            -> change items' Category
#   UpdateFileLifeCycleStates       -> promote files to a state
#   CheckoutFile                    -> reserve a file (no download)
#   UndoCheckoutFile                -> release a file's checkout
#   UpdateFileProperties            -> set Vault properties on files
#   GetCadBom                       -> Inventor's stored multi-level BOM
#
# Read operations sign in read-only and take no license seat.
#
# Examples:
#   pwsh -File vault_sdk.ps1 -Operation GetLifecycleStates
#   pwsh -File vault_sdk.ps1 -Operation LookupItem -ArgsJson '{"number":"SF-001702"}'

param(
    [Parameter(Mandatory = $true)] [string]$Operation,
    [string]$ArgsJson = '{}'
)

$ErrorActionPreference = 'Stop'

# Force UTF-8 stdout so the Python side can decode our JSON cleanly. Default
# PowerShell output encoding on Windows is UTF-16 LE which breaks subprocess
# JSON parsing.
$OutputEncoding = [System.Text.Encoding]::UTF8
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

# ---------------------------------------------------------------------------
# SDK location (override via $env:VAULT_SDK_BIN)
# ---------------------------------------------------------------------------
# The SDK must match the server's major version: the 2025 SDK against the
# 2026 server (31.x) fails sign-in with "The license obtained is incompatible
# with the server". The Vault Client 2026 install ships the same assemblies,
# built for .NET 8, so this script must run under PowerShell 7 (vault_sdk.py
# prefers pwsh); Windows PowerShell 5.1 cannot load them.
$SdkBin = if ($env:VAULT_SDK_BIN) {
    $env:VAULT_SDK_BIN
} elseif (Test-Path 'C:\Program Files\Autodesk\Vault Client 2026\Explorer\Autodesk.Connectivity.WebServices.dll') {
    'C:\Program Files\Autodesk\Vault Client 2026\Explorer'
} else {
    'C:\Program Files\Autodesk\Autodesk Vault 2025 SDK\bin\x64'
}

# Per AU SD321955 page 14: PowerShell needs the Autodesk Licensing native
# DLL on its DLL search path before any license-acquiring SDK call. The PDF
# names AdskLicensingSDK_2.dll; on Vault 2025 it is AdskLicensingSDK_8.dll.
# Without this, every writable LicensingAgent / AuthenticationFlags combo
# fails with "Failed to acquire a license" or VaultLicenseException, even
# when the user actually has a seat available. Confirmed via test_license.ps1.
$licDllNames = @('AdskLicensingSDK_9.dll','AdskLicensingSDK_8.dll','AdskLicensingSDK_2.dll')
$licSearchDirs = @(
    $SdkBin,
    'C:\Program Files\Autodesk\Vault Client 2025\Explorer',
    'C:\Program Files\Autodesk\Inventor 2025\Bin'
)
foreach ($d in $licSearchDirs) {
    foreach ($n in $licDllNames) {
        if (Test-Path (Join-Path $d $n)) {
            if ($env:PATH -notlike "*$d*") { $env:PATH = "$d;$env:PATH" }
            break
        }
    }
}

$projectRoot = Split-Path -Parent $PSScriptRoot
$configPath  = Join-Path $projectRoot 'config.json'

# ---------------------------------------------------------------------------
# Failure helper -- prints an error JSON to stderr and exits 1. Python side
# inspects the exit code first and surfaces stderr verbatim.
# ---------------------------------------------------------------------------
function Die([string]$msg) {
    [Console]::Error.WriteLine($msg)
    exit 1
}

# ---------------------------------------------------------------------------
# Sign-in -- once per script invocation. Returns a WebServiceManager.
#
# Uses the PDF-recommended pattern (AU class SD321955, section 2.4.6):
# VdfForms.Library.Login with AutoLoginMode = RestoreAndExecute. Reuses
# the encrypted credentials Vault Explorer stored after an Autodesk
# Account sign-in, so subsequent runs are silent. The first run shows
# the standard Vault login dialog (requires STA -- the Python wrapper
# launches PowerShell with -STA).
#
# Falls back to legacy ConnectionManager.LogIn with config.json
# username/password when the VDF Forms assemblies are unavailable
# (e.g. on a server with no Vault Client install).
# ---------------------------------------------------------------------------

# Per-name AssemblyResolve cache + handler. Critical: the cache prevents
# StackOverflow when a transitive dep is missing. Without it the handler
# returns $null, .NET re-fires AssemblyResolve, recurses infinitely.
$script:resolveDirs  = New-Object System.Collections.ArrayList
$script:resolveCache = @{}
$script:resolveHandlerRegistered = $false
$script:resolveHandler = {
    param($sender, $eventArgs)
    $name = ($eventArgs.Name -split ',')[0].Trim()
    if ($script:resolveCache.ContainsKey($name)) {
        return $script:resolveCache[$name]
    }
    $script:resolveCache[$name] = $null   # mark in-progress to block re-entry
    foreach ($dir in $script:resolveDirs) {
        $cand = Join-Path $dir ($name + '.dll')
        if (Test-Path $cand) {
            try {
                $asm = [System.Reflection.Assembly]::LoadFrom($cand)
                $script:resolveCache[$name] = $asm
                return $asm
            } catch { }
        }
    }
    return $null
}
function Add-ResolveDir([string]$dir) {
    if ($dir -and (Test-Path $dir) -and (-not $script:resolveDirs.Contains($dir))) {
        [void]$script:resolveDirs.Add($dir)
    }
    if (-not $script:resolveHandlerRegistered) {
        [System.AppDomain]::CurrentDomain.add_AssemblyResolve($script:resolveHandler)
        $script:resolveHandlerRegistered = $true
    }
}

function Find-VaultClientFormsDll {
    # Pin to the Vault Client whose version matches the SDK we loaded.
    # Mixing 2025 SDK + 2027 Forms causes a .NET 10 loader cascade on PS 5.1.
    $sdkVersion = $null
    if ($SdkBin -match 'Autodesk Vault (\d{4}) SDK') { $sdkVersion = $matches[1] }

    $clientRoots = Get-ChildItem 'C:\Program Files\Autodesk' -Directory -Filter 'Vault Client*' -ErrorAction SilentlyContinue
    $candidates = @()
    if ($sdkVersion) {
        $matched = $clientRoots | Where-Object { $_.Name -match ('Vault Client\s*' + [regex]::Escape($sdkVersion) + '\b') }
        foreach ($r in $matched) {
            $p = Join-Path $r.FullName 'Explorer\Autodesk.DataManagement.Client.Framework.Vault.Forms.dll'
            if (Test-Path $p) { $candidates += $p }
        }
    }
    if ($candidates.Count -eq 0) {
        foreach ($r in ($clientRoots | Sort-Object Name)) {
            $p = Join-Path $r.FullName 'Explorer\Autodesk.DataManagement.Client.Framework.Vault.Forms.dll'
            if (Test-Path $p) { $candidates += $p }
        }
    }
    return $candidates
}

function Connect-Vault([switch]$ReadOnly) {
    if (-not (Test-Path $configPath)) { Die ('config.json not found at ' + $configPath) }
    $cfg = Get-Content $configPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $server = $cfg.vault.servername
    $db     = $cfg.vault.database
    $user   = $cfg.vault.username
    $pass   = $cfg.vault.password
    if (-not $server -or -not $db) {
        Die 'config.json is missing vault.servername / vault.database'
    }
    $serverHost = $server -replace '^https?://', '' -replace '/$', ''

    # Core SDK assemblies always come from the SDK bin.
    $needed = @(
        'Autodesk.Connectivity.WebServices.dll',
        'Autodesk.DataManagement.Client.Framework.dll',
        'Autodesk.DataManagement.Client.Framework.Vault.dll'
    )
    foreach ($n in $needed) {
        $p = Join-Path $SdkBin $n
        if (-not (Test-Path $p)) { Die ('SDK assembly missing: ' + $p) }
        Add-Type -Path $p
    }
    Add-ResolveDir $SdkBin

    # NOTE: We deliberately skip the VDF Forms login path here. Although the
    # PDF (AU SD321955 §2.4.6) recommends VdfForms.Library.Login with
    # RestoreAndExecute for Autodesk Account sign-in, that call reliably
    # crashes the host process with StackOverflowException when invoked from
    # Windows PowerShell 5.1 -- a known incompat between the Vault Forms
    # assemblies and the PS 5.1 .NET Framework runspace. Recursion guards,
    # fresh STA worker threads, and explicit WinForms init all fail to fix
    # it. The diagnostic in scripts/test_license.ps1 confirms the crash.
    #
    # We use the legacy ConnectionManager.LogIn path with config.json
    # credentials -- it's stable, and license issues surface as clean error
    # 319 / VaultLicenseException rather than process termination.
    if (-not $user -or -not $pass) {
        Die 'config.json is missing vault.username / vault.password (required for ConnectionManager login).'
    }
    $afType = [Autodesk.DataManagement.Client.Framework.Vault.Currency.Connections.AuthenticationFlags]
    $cm = [Autodesk.DataManagement.Client.Framework.Vault.Library]::ConnectionManager
    # Read-only sessions take no license seat, so reads keep working while
    # Inventor / Vault Explorer hold it (or when no seat can be had at all).
    if ($ReadOnly) {
        $r = $cm.LogIn($serverHost, $db, $user, $pass, $afType::ReadOnly, $null)
        if ($r -and $r.Success) { return $r.Connection.WebServiceManager }
        $msgs = @()
        if ($r -and $r.ErrorMessages) {
            foreach ($kv in $r.ErrorMessages.GetEnumerator()) { $msgs += ('{0}: {1}' -f $kv.Key, $kv.Value) }
        }
        Die ('Read-only LogIn failed: ' + ($msgs -join ' | '))
    }
    $combos = @(
        @{ name = 'Standard';                            flags = $afType::Standard },
        @{ name = 'Standard|ServerLicense';              flags = ($afType::Standard -bor $afType::ServerLicense) },
        @{ name = 'WindowsAuthenticationWithCredentials';flags = $afType::WindowsAuthenticationWithCredentials },
        @{ name = 'WindowsAuthentication';               flags = $afType::WindowsAuthentication }
    )
    $attempts = @()
    $licenseBlocked = $false
    foreach ($c in $combos) {
        $comboMsgs = @()
        try {
            $r = $cm.LogIn($serverHost, $db, $user, $pass, $c.flags, $null)
            if ($r -and $r.Success) {
                [Console]::Error.WriteLine("VAULT_LOGIN_OK via=ConnectionManager combo=$($c.name) flags=$($r.Connection.AuthenticationFlags)")
                return $r.Connection.WebServiceManager
            }
            $comboMsgs += ('{0} did not Success' -f $c.name)
            if ($r -and $r.ErrorMessages) {
                foreach ($kv in $r.ErrorMessages.GetEnumerator()) {
                    $comboMsgs += ('  {0}: {1}' -f $kv.Key, $kv.Value)
                }
            }
        } catch {
            $comboMsgs += ('{0} threw: {1}' -f $c.name, $_.Exception.Message)
        }
        $attempts += $comboMsgs
        # Circuit-breaker: a license refusal applies to the seat, not the
        # auth flags. Trying the remaining combos cannot succeed and just
        # hammers the license server. Bail immediately with a tagged error.
        if ($comboMsgs -match 'Failed to acquire a license') {
            $licenseBlocked = $true
            break
        }
    }
    if ($licenseBlocked) {
        Die ('Failed to acquire a license. ConnectionManager.LogIn aborted after first license refusal (skipped remaining flag combos to avoid hammering license server). Detail: ' + ($attempts -join ' | '))
    }
    Die ('ConnectionManager.LogIn failed for every flag combo: ' + ($attempts -join ' | '))
}

# ---------------------------------------------------------------------------
# Argument parsing -- turn the JSON string into a PSObject we can index.
# ---------------------------------------------------------------------------
$argsObj = if ([string]::IsNullOrWhiteSpace($ArgsJson)) {
    [PSCustomObject]@{}
} else {
    try { $ArgsJson | ConvertFrom-Json } catch { Die ('Invalid -ArgsJson: ' + $_.Exception.Message) }
}

# Helper: pull an arg with a default value
function Get-Arg([string]$name, $default = $null) {
    if ($argsObj -and ($argsObj.PSObject.Properties.Name -contains $name)) {
        return $argsObj.$name
    }
    return $default
}

# ---------------------------------------------------------------------------
# Operation handlers -- each returns a hashtable / array that ConvertTo-Json
# turns into the response body.
# ---------------------------------------------------------------------------

function Invoke-GetLifecycleStates($mgr) {
    $defs = $mgr.LifeCycleService.GetAllLifeCycleDefinitions()
    $out = @()
    foreach ($d in $defs) {
        $states = @()
        foreach ($s in $d.StateArray) {
            $name = if ($s.DispName) { $s.DispName } else { $s.Name }
            $states += [ordered]@{
                id   = [int64]$s.Id
                name = [string]$name
            }
        }
        $out += [ordered]@{
            id     = [int64]$d.Id
            name   = [string]$d.DispName
            states = $states
        }
    }
    return @{ definitions = $out }
}

function Invoke-GetItemPropertyDefinitions($mgr) {
    # Try the modern signature first, fall back to the older one.
    $infos = $null
    try {
        $infos = $mgr.PropertyService.GetPropertyDefinitionInfosByEntityClassId('ITEM', $null)
    } catch {
        $infos = $mgr.PropertyService.GetPropertyDefinitionsByEntityClassId('ITEM')
    }
    $out = @()
    foreach ($p in $infos) {
        $pdef = if ($p.PropDef) { $p.PropDef } else { $p }
        $out += [ordered]@{
            id       = [int64]$pdef.Id
            sysName  = [string]$pdef.SysName
            dispName = [string]$pdef.DispName
            dataType = [string]$pdef.Typ
            isSystem = [bool]$pdef.IsSys
            isActive = [bool]$pdef.Active
        }
    }
    return @{ properties = $out }
}

function Invoke-GetItemCategories($mgr) {
    $cats = $mgr.CategoryService.GetCategoriesByEntityClassId('ITEM', $true)
    $out = @()
    foreach ($c in $cats) {
        $out += [ordered]@{
            id   = [int64]$c.Id
            name = [string]$c.Name
        }
    }
    return @{ categories = $out }
}

function Invoke-LookupItem($mgr) {
    $number = Get-Arg 'number'
    if ([string]::IsNullOrWhiteSpace($number)) { Die 'LookupItem requires {"number":"..."}' }

    # Build a SrchCond filtering by part number. The system name on this
    # vault is 'Number'; older / customised vaults use 'ItemNum' or
    # 'ItemNumber' or 'PartNumber'. Try them in order.
    $propDefs = $mgr.PropertyService.GetPropertyDefinitionInfosByEntityClassId('ITEM', $null)
    $numberDef = $null
    foreach ($candidate in @('Number', 'ItemNum', 'ItemNumber', 'PartNumber')) {
        foreach ($p in $propDefs) {
            $pdef = if ($p.PropDef) { $p.PropDef } else { $p }
            if ($pdef.SysName -eq $candidate) { $numberDef = $pdef; break }
        }
        if ($numberDef) { break }
    }
    if ($null -eq $numberDef) { Die 'Could not find Number / ItemNum property def for ITEM class' }

    $cond = New-Object Autodesk.Connectivity.WebServices.SrchCond
    $cond.PropDefId  = $numberDef.Id
    $cond.PropTyp    = [Autodesk.Connectivity.WebServices.PropertySearchType]::SingleProperty
    $cond.SrchOper   = 3        # 3 = "Is exactly"
    $cond.SrchTxt    = [string]$number
    $cond.SrchRule   = [Autodesk.Connectivity.WebServices.SearchRuleType]::Must

    $bookmark = ''
    $status   = $null
    $items = $mgr.ItemService.FindItemRevisionsBySearchConditions(
        @($cond), $null, $true, [ref]$bookmark, [ref]$status
    )
    if (-not $items -or $items.Count -eq 0) { return @{ found = $false; number = $number } }

    $item = $items[0]

    # Pull all properties for the matched item version
    $propDefIds = @()
    foreach ($p in $propDefs) {
        $pdef = if ($p.PropDef) { $p.PropDef } else { $p }
        $propDefIds += [int64]$pdef.Id
    }
    $propVals = $mgr.PropertyService.GetProperties('ITEM', @([int64]$item.Id), [int64[]]$propDefIds)
    $props = [ordered]@{}
    foreach ($pv in $propVals) {
        $defForId = $propDefs | Where-Object {
            $d = if ($_.PropDef) { $_.PropDef } else { $_ }
            $d.Id -eq $pv.PropDefId
        } | Select-Object -First 1
        if ($defForId) {
            $d = if ($defForId.PropDef) { $defForId.PropDef } else { $defForId }
            $key = if ($d.DispName) { [string]$d.DispName } else { [string]$d.SysName }
            $props[$key] = $pv.Val
        }
    }

    return [ordered]@{
        found            = $true
        id               = [int64]$item.Id          # item-version id
        masterId         = [int64]$item.MasterId    # master item id (for lifecycle calls)
        number           = [string]$item.ItemNum
        title            = [string]$item.Title
        revision         = [string]$item.RevNum
        lifecycleDefId   = [int64]$item.LfCyc.LfCycDefId
        lifecycleStateId = [int64]$item.LfCycStateId
        properties       = $props
    }
}

function Invoke-LookupFile($mgr) {
    $masterId = Get-Arg 'masterId'
    if ($null -eq $masterId) { Die 'LookupFile requires masterId' }
    $files = $mgr.DocumentService.GetLatestFilesByMasterIds([int64[]]@([int64]$masterId))
    if (-not $files -or $files.Count -eq 0) {
        return [ordered]@{ found = $false; masterId = [int64]$masterId }
    }
    $f = $files[0]
    return [ordered]@{
        found            = $true
        id               = [int64]$f.Id
        masterId         = [int64]$f.MasterId
        name             = [string]$f.Name
        revision         = if ($f.FileRev) { [string]$f.FileRev.Label } else { '' }
        lifecycleDefId   = if ($f.FileLfCyc) { [int64]$f.FileLfCyc.LfCycDefId } else { 0 }
        lifecycleStateId = if ($f.FileLfCyc) { [int64]$f.FileLfCyc.LfCycStateId } else { 0 }
    }
}

function Invoke-UpdateItemProperties($mgr) {
    $itemIds  = Get-Arg 'itemIds'
    $propMap  = Get-Arg 'properties'    # { sysName: value, ... }
    if (-not $itemIds -or $itemIds.Count -eq 0) { Die 'UpdateItemProperties requires itemIds' }
    if (-not $propMap) { Die 'UpdateItemProperties requires properties' }

    # ----- System fields vs UDPs ---------------------------------------------
    # A few "properties" in Vault terminology are actually attributes on the
    # SOAP `Item` structure (Title, Detail, Comm), NOT user-defined properties.
    # ItemService.UpdateItemProperties only accepts UDP IDs and rejects system
    # fields with error 3933 ("Cannot update system property"). Vault Explorer
    # changes them by mutating the Item object returned by EditItems and then
    # calling UpdateAndCommitItems. Confirmed via SOAP trace 2026-05-02 (see
    # scripts/probes/setup_explorer_trace.ps1).
    $systemPropToItemAttr = @{
        'Title (Item,CO)'       = 'Title'
        'Title(Item,CO)'        = 'Title'
        'Description (Item,CO)' = 'Detail'
        'Description(Item,CO)'  = 'Detail'
    }
    $systemUpdates = @{}            # { ItemAttr -> value }
    $udpProps      = @{}            # { propname -> value } passed through to UDP path

    $propDefs = $mgr.PropertyService.GetPropertyDefinitionInfosByEntityClassId('ITEM', $null)
    $sysToDef = @{}
    foreach ($p in $propDefs) {
        $pdef = if ($p.PropDef) { $p.PropDef } else { $p }
        $sysToDef[[string]$pdef.SysName] = $pdef
        $sysToDef[[string]$pdef.DispName] = $pdef   # accept either key form
    }

    foreach ($key in $propMap.PSObject.Properties.Name) {
        if ($systemPropToItemAttr.ContainsKey($key)) {
            $systemUpdates[$systemPropToItemAttr[$key]] = $propMap.$key
            continue
        }
        $udpProps[$key] = $propMap.$key
    }

    $updates = @()
    foreach ($key in $udpProps.Keys) {
        $def = $sysToDef[$key]
        if ($null -eq $def) { Die ('Unknown property: ' + $key) }
        $value = $udpProps[$key]
        $param = New-Object Autodesk.Connectivity.WebServices.PropInstParam
        $param.PropDefId = [int64]$def.Id
        $param.Val       = $value
        $updates += $param
    }

    # PropInstParamArray wraps a per-entity batch (only built if we have UDP changes)
    $perEntity = $null
    if ($updates.Count -gt 0) {
        $perEntity = New-Object Autodesk.Connectivity.WebServices.PropInstParamArray
        $perEntity.Items = [Autodesk.Connectivity.WebServices.PropInstParam[]]$updates
    }

    # Callers pass iteration IDs (what REST and our LookupItem return as `id`).
    # But ItemService.EditItems / UpdateItemProperties want REVISION IDs --
    # confirmed via Vault Explorer SOAP trace 2026-05-02. Passing the
    # iteration ID makes the server-side EditItemRevisions SP look up
    # Locks.EntityID with a non-existent key, get NULL back, and fail with
    # error 1321 (EditItemRevisionFailed) wrapping a SQL constraint violation.
    # Convert iter IDs -> rev IDs via GetItemsByIds (returns Item.RevId).
    $iterIds = [int64[]]($itemIds | ForEach-Object { [int64]$_ })
    $items = $mgr.ItemService.GetItemsByIds($iterIds)
    if (-not $items -or $items.Count -ne $iterIds.Count) {
        Die ("GetItemsByIds returned $($items.Count) items for $($iterIds.Count) iter IDs " + ($iterIds -join ','))
    }
    $revIds = [int64[]]($items | ForEach-Object { [int64]$_.RevId })

    $editable = $mgr.ItemService.EditItems($revIds)
    if (-not $editable -or $editable.Count -eq 0) {
        Die ("EditItems returned no editable items for rev IDs " + ($revIds -join ','))
    }

    try {
        # Apply system-field updates by mutating the editable Item objects in
        # place. UpdateAndCommitItems below picks up these mutations.
        if ($systemUpdates.Count -gt 0) {
            foreach ($it in $editable) {
                foreach ($attr in $systemUpdates.Keys) {
                    $it.$attr = [string]$systemUpdates[$attr]
                }
            }
        }

        # UpdateItemProperties also takes RevIds (per Explorer trace), not the
        # new iteration IDs returned by EditItems. Skip if we only have system
        # updates (no UDPs) -- calling UpdateItemProperties with empty params
        # is an error.
        if ($null -ne $perEntity) {
            $editableRevIds = [int64[]]($editable | ForEach-Object { [int64]$_.RevId })
            $arrays = @()
            for ($i = 0; $i -lt $editableRevIds.Count; $i++) { $arrays += $perEntity }
            $arrayParam = [Autodesk.Connectivity.WebServices.PropInstParamArray[]]$arrays
            $mgr.ItemService.UpdateItemProperties($editableRevIds, $arrayParam)
        }

        $mgr.ItemService.UpdateAndCommitItems($editable)
    } catch {
        try { $mgr.ItemService.UndoEditItems($revIds) | Out-Null } catch { }
        throw
    }

    return @{ updated = $itemIds.Count }
}

function Invoke-UpdateItemLifeCycleStates($mgr) {
    $masters  = Get-Arg 'masterIds'
    $stateId  = Get-Arg 'stateId'
    $comment  = Get-Arg 'comment' ''
    if (-not $masters -or $masters.Count -eq 0) { Die 'UpdateItemLifeCycleStates requires masterIds' }
    if ($null -eq $stateId) { Die 'UpdateItemLifeCycleStates requires stateId' }

    $longMasters = [int64[]]($masters | ForEach-Object { [int64]$_ })
    $longStates  = [int64[]](@([int64]$stateId) * $masters.Count)

    $updated = $mgr.ItemService.UpdateItemLifeCycleStates($longMasters, $longStates, [string]$comment)
    return @{ updated = if ($updated) { $updated.Count } else { 0 } }
}

function Invoke-UpdateItemCategories($mgr) {
    $masters = Get-Arg 'masterIds'
    $catId   = Get-Arg 'categoryId'
    $comment = Get-Arg 'comment' ''
    if (-not $masters -or $masters.Count -eq 0) { Die 'UpdateItemCategories requires masterIds' }
    if ($null -eq $catId) { Die 'UpdateItemCategories requires categoryId' }

    $longMasters = [int64[]]($masters | ForEach-Object { [int64]$_ })
    $longCats    = [int64[]](@([int64]$catId) * $masters.Count)

    $updated = $mgr.ItemService.UpdateItemCategories($longMasters, $longCats, [string]$comment)
    return @{ updated = if ($updated) { $updated.Count } else { 0 } }
}

function Invoke-UpdateFileLifeCycleStates($mgr) {
    $masters  = Get-Arg 'masterIds'
    $stateId  = Get-Arg 'stateId'
    $comment  = Get-Arg 'comment' ''
    if (-not $masters -or $masters.Count -eq 0) { Die 'UpdateFileLifeCycleStates requires masterIds' }
    if ($null -eq $stateId) { Die 'UpdateFileLifeCycleStates requires stateId' }

    $longMasters = [int64[]]($masters | ForEach-Object { [int64]$_ })
    $longStates  = [int64[]](@([int64]$stateId) * $masters.Count)

    $updated = $mgr.DocumentServiceExtensions.UpdateFileLifeCycleStates(
        $longMasters, $longStates, [string]$comment
    )
    return @{ updated = if ($updated) { $updated.Count } else { 0 } }
}

function Get-FileOut($f) {
    return [ordered]@{
        id         = [int64]$f.Id
        masterId   = [int64]$f.MasterId
        name       = [string]$f.Name
        checkedOut = [bool]$f.CheckedOut
        ckOutUserId = if ($f.CheckedOut) { [int64]$f.CkOutUserId } else { $null }
    }
}

function Invoke-CheckoutFile($mgr) {
    # Reserves the latest version in Vault without downloading it; the
    # checkout is recorded against this machine with no local path.
    $masterId = Get-Arg 'masterId'
    $comment  = Get-Arg 'comment' ''
    if ($null -eq $masterId) { Die 'CheckoutFile requires masterId' }
    $file = $mgr.DocumentService.GetLatestFileByMasterId([int64]$masterId)
    $ticket = $null
    $out = $mgr.DocumentService.CheckoutFile(
        [int64]$file.Id,
        [Autodesk.Connectivity.WebServices.CheckoutFileOptions]::Master,
        [string]$env:COMPUTERNAME,
        '',
        [string]$comment,
        [ref]$ticket
    )
    return Get-FileOut $out
}

function Invoke-UndoCheckoutFile($mgr) {
    $masterId = Get-Arg 'masterId'
    if ($null -eq $masterId) { Die 'UndoCheckoutFile requires masterId' }
    $ticket = $null
    $out = $mgr.DocumentService.UndoCheckoutFile([int64]$masterId, [ref]$ticket)
    return Get-FileOut $out
}

function Invoke-UpdateFileProperties($mgr) {
    # Writes Vault property values on the latest version of each file and
    # creates a new property-only version. CAD-mapped properties reach the
    # file itself only after an Autodesk.Vault.SyncProperties job runs.
    $masters = Get-Arg 'masterIds'
    $propMap = Get-Arg 'properties'    # { name: value, ... } same for every file
    if (-not $masters -or $masters.Count -eq 0) { Die 'UpdateFileProperties requires masterIds' }
    if (-not $propMap) { Die 'UpdateFileProperties requires properties' }

    $propDefs = $mgr.PropertyService.GetPropertyDefinitionInfosByEntityClassId('FILE', $null)
    $byName = @{}
    foreach ($p in $propDefs) {
        $pdef = if ($p.PropDef) { $p.PropDef } else { $p }
        $byName[[string]$pdef.SysName] = $pdef
        $byName[[string]$pdef.DispName] = $pdef
    }
    $params = @()
    foreach ($key in $propMap.PSObject.Properties.Name) {
        $def = $byName[$key]
        if ($null -eq $def) { Die ('Unknown file property: ' + $key) }
        if ($def.IsSys) { Die ('System property cannot be written: ' + $key) }
        $param = New-Object Autodesk.Connectivity.WebServices.PropInstParam
        $param.PropDefId = [int64]$def.Id
        $param.Val       = $propMap.$key
        $params += $param
    }
    $perFile = New-Object Autodesk.Connectivity.WebServices.PropInstParamArray
    $perFile.Items = [Autodesk.Connectivity.WebServices.PropInstParam[]]$params

    $longMasters = [int64[]]($masters | ForEach-Object { [int64]$_ })
    $arrays = @()
    for ($i = 0; $i -lt $longMasters.Count; $i++) { $arrays += $perFile }
    $files = $mgr.DocumentServiceExtensions.UpdateFileProperties(
        $longMasters, [Autodesk.Connectivity.WebServices.PropInstParamArray[]]$arrays
    )
    $out = @()
    foreach ($f in $files) { $out += Get-FileOut $f }
    return @{ updated = $out.Count; files = $out }
}

function Invoke-GetCadBom($mgr) {
    # Reads the BOM Inventor stored in Vault when the assembly was checked in:
    # every level, per-parent quantities and BOM structure, no items needed.
    # Part properties come from each component file's own stored BOM (its
    # component 0 carries that file's iProperties).
    $masterId = Get-Arg 'masterId'
    if ($null -eq $masterId) { Die 'GetCadBom requires masterId' }
    $file = $mgr.DocumentService.GetLatestFileByMasterId([int64]$masterId)
    $root = [ordered]@{
        name          = [string]$file.Name
        fileVersionId = [int64]$file.Id
        masterId      = [int64]$file.MasterId
        checkedOut    = [bool]$file.CheckedOut
        checkedIn     = [string]$file.CkInDate.ToString('o')
    }
    $bom = $mgr.DocumentService.GetBOMByFileId([int64]$file.Id)
    if (-not $bom -or -not $bom.CompArray) { return [ordered]@{ found = $false; root = $root } }

    $want = @('Part Number','Title','Description','Source','Vendor','Material',
              'Stock Number','Revision','Cost','Content Center File')
    function Get-Attrs($b, [int64]$compId) {
        $names = @{}
        foreach ($p in $b.PropArray) { $names[[int64]$p.Id] = [string]$p.DispName }
        $out = [ordered]@{}
        foreach ($a in $b.CompAttrArray) {
            if ([int64]$a.CompId -ne $compId) { continue }
            $n = $names[[int64]$a.PropId]
            $v = [string]$a.Val
            if ($n -and ($want -contains $n) -and $v -and -not $v.StartsWith('01/01/1601')) { $out[$n] = $v }
        }
        return $out
    }

    $xrefs = @($bom.CompArray | Where-Object { $_.XRefTyp -eq 'External' -and $_.XRefId -gt 0 } |
               ForEach-Object { [int64]$_.XRefId } | Select-Object -Unique)
    $files = @{}
    $props = @{}
    if ($xrefs.Count -gt 0) {
        foreach ($f in $mgr.DocumentService.GetFilesByIds([int64[]]$xrefs)) { $files[[int64]$f.Id] = $f }
        foreach ($x in $xrefs) {
            try {
                $cb = $mgr.DocumentService.GetBOMByFileId($x)
                $props[$x] = if ($cb) { Get-Attrs $cb 0 } else { [ordered]@{} }
            } catch { $props[$x] = [ordered]@{} }
        }
    }

    $comps = @()
    foreach ($c in $bom.CompArray) {
        $ext = ($c.XRefTyp -eq 'External' -and $c.XRefId -gt 0)
        $f = if ($ext) { $files[[int64]$c.XRefId] } else { $null }
        $comps += [ordered]@{
            id            = [int64]$c.Id
            name          = if ($f) { [string]$f.Name } else { [string]$c.Name }
            fileVersionId = if ($ext) { [int64]$c.XRefId } else { $null }
            fileMasterId  = if ($f) { [int64]$f.MasterId } else { $null }
            type          = [string]$c.CompTyp
            bomStructure  = [string]$c.BOMStruct
            uom           = [string]$c.BaseUOM
            props         = if ($ext) { $props[[int64]$c.XRefId] } else { Get-Attrs $bom ([int64]$c.Id) }
        }
    }
    $insts = @()
    foreach ($i in $bom.InstArray) {
        # BOMStructOverde is the per-occurrence override (Default / Reference /
        # Phantom / Purchased ...); Reference occurrences are not in the BOM.
        $insts += [ordered]@{
            parent    = [int64]$i.ParId
            child     = [int64]$i.CldId
            qty       = [double]$i.Quant
            structure = [string]$i.BOMStructOverde
        }
    }
    return [ordered]@{ found = $true; root = $root; comps = $comps; insts = $insts }
}

# ---------------------------------------------------------------------------
# Dispatch
# ---------------------------------------------------------------------------

# Operations that only read run on a read-only session (no license seat).
$readOnlyOps = @('GetLifecycleStates','GetItemPropertyDefinitions','GetItemCategories',
                 'LookupItem','LookupFile','GetCadBom')

try {
    $mgr = if ($readOnlyOps -contains $Operation) { Connect-Vault -ReadOnly } else { Connect-Vault }
    $result = switch ($Operation) {
        'GetCadBom'                  { Invoke-GetCadBom                  $mgr }
        'GetLifecycleStates'         { Invoke-GetLifecycleStates         $mgr }
        'GetItemPropertyDefinitions' { Invoke-GetItemPropertyDefinitions $mgr }
        'GetItemCategories'          { Invoke-GetItemCategories          $mgr }
        'LookupItem'                 { Invoke-LookupItem                 $mgr }
        'LookupFile'                 { Invoke-LookupFile                 $mgr }
        'UpdateItemProperties'       { Invoke-UpdateItemProperties       $mgr }
        'UpdateItemLifeCycleStates'  { Invoke-UpdateItemLifeCycleStates  $mgr }
        'UpdateItemCategories'       { Invoke-UpdateItemCategories       $mgr }
        'UpdateFileLifeCycleStates'  { Invoke-UpdateFileLifeCycleStates  $mgr }
        'CheckoutFile'               { Invoke-CheckoutFile               $mgr }
        'UndoCheckoutFile'           { Invoke-UndoCheckoutFile           $mgr }
        'UpdateFileProperties'       { Invoke-UpdateFileProperties       $mgr }
        default { Die ('Unknown -Operation: ' + $Operation) }
    }
} catch {
    Die ($Operation + ' failed: ' + $_.Exception.Message)
}

# Single JSON line on stdout -- Python parses with json.loads
$result | ConvertTo-Json -Depth 12 -Compress
