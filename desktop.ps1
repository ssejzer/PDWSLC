# Copyright (c) 2026 Sebastian Sejzer.
# Licensed under the MIT License.
#Requires -Version 5.1

[CmdletBinding()]
param(
    [ValidateSet('List', 'Storage', 'Start', 'Open', 'Stop', 'Delete', 'Status', 'Logs')]
    [string] $Action = 'Start',
    [ValidatePattern('^[a-zA-Z0-9][a-zA-Z0-9_.-]*$')]
    [string] $Name = 'desktop',
    [string] $Session,
    [string] $Image = 'lscr.io/linuxserver/webtop:ubuntu-kde',
    [ValidateRange(1, 65535)]
    [int] $Port = 3001,
    [string] $TimeZone = 'Asia/Jerusalem',
    [ValidateRange(1, 3600)]
    [int] $TimeoutSeconds = 300,
    [switch] $NoBrowser,
    [switch] $Running,
    [switch] $IncludeStorage,
    [switch] $Adopt,
    [switch] $Json
)

$ErrorActionPreference = 'Stop'
if ($Json -and $Action -notin @('List', 'Storage', 'Status')) { throw '-Json requires List, Storage or Status.' }
if ($Running -and $Action -ne 'List') { throw '-Running requires -Action List.' }
if ($IncludeStorage -and $Action -ne 'Delete') { throw '-IncludeStorage requires -Action Delete.' }
if ($Adopt -and -not ($Action -eq 'Delete' -and $IncludeStorage)) {
    throw '-Adopt requires -Action Delete -IncludeStorage; it authorizes deleting an unlabelled legacy home volume.'
}
if ($Action -eq 'Delete' -and -not $PSBoundParameters.ContainsKey('Name')) {
    throw 'Deletion requires an explicit -Name.'
}
$wsl = (Get-Command wsl.exe -ErrorAction Stop).Source
$versionOutput = & $wsl --version
if ($LASTEXITCODE -ne 0) {
    throw 'PDWSLC requires WSL 3.0.1 or later. Run wsl --update, then retry.'
}
# Some WSL versions emit UTF-16 output when invoked from a redirected process.
$versionText = ($versionOutput -join "`n").Replace("`0", '')
$versionMatch = [regex]::Match($versionText, '(?m)^\s*WSL\s+[^\r\n\d]*(\d+\.\d+\.\d+(?:\.\d+)?)')
if (-not $versionMatch.Success) {
    throw 'Could not determine the WSL version. PDWSLC requires WSL 3.0.1 or later; check wsl --version.'
}
if ([version]$versionMatch.Groups[1].Value -lt [version]'3.0.1') {
    throw "PDWSLC requires WSL 3.0.1 or later (installed: $($versionMatch.Groups[1].Value)). Run wsl --update, then retry."
}
$wslc = (Get-Command wslc.exe -ErrorAction Stop).Source
$sessionArguments = @()
if ($Session) { $sessionArguments = @('--session', $Session) }

function Invoke-Wslc {
    param([string[]] $Arguments)
    $result = & $wslc @sessionArguments @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "wslc $($Arguments -join ' ') failed (exit code $LASTEXITCODE)."
    }
    return $result
}

function Get-Containers {
    $items = @(Invoke-Wslc -Arguments @('ps', '-a', '--format', 'json') | ConvertFrom-Json | ForEach-Object { $_ })
    foreach ($item in $items) {
        if (-not $item) { continue }
        $details = @(Invoke-Wslc -Arguments @('inspect', '--type', 'container', $item.Names.TrimStart('/')) | ConvertFrom-Json | ForEach-Object { $_ })
        $details[0]
    }
}

function Get-Desktop {
    Get-Containers | Where-Object { $_.Name.TrimStart('/') -ceq $Name } | Select-Object -First 1
}

function Get-Volumes {
    $items = @(Invoke-Wslc -Arguments @('volume', 'list', '--format', 'json') | ConvertFrom-Json | ForEach-Object { $_ })
    foreach ($item in $items) {
        if (-not $item) { continue }
        $details = @(Invoke-Wslc -Arguments @('volume', 'inspect', $item.Name) | ConvertFrom-Json | ForEach-Object { $_ })
        $details[0]
    }
}

function Test-Owned {
    param($Labels, [string] $DesktopName)
    return ($Labels -and $Labels.'io.pdwslc.managed' -eq 'true' -and
        $Labels.'io.pdwslc.desktop' -ceq $DesktopName)
}

function Get-HomeMount {
    param($Desktop)
    @($Desktop.Mounts | Where-Object { $_.Destination -eq '/config' -and $_.Type -eq 'volume' })
}

function Get-Management {
    param($Desktop)
    $desktopName = $Desktop.Name.TrimStart('/')
    if (Test-Owned $Desktop.Config.Labels $desktopName) { return 'Managed' }
    $homeMounts = @(Get-HomeMount $Desktop)
    if ($Desktop.Config.Image -match '(^|/)linuxserver/webtop(?=[:@]|$)' -and
        $homeMounts.Count -eq 1 -and $homeMounts[0].Name -ceq "$desktopName-home") { return 'Legacy' }
    return 'Unmanaged'
}

function Get-DesktopSummary {
    param($Desktop)
    $url = $null
    try { $url = Get-DesktopUrl $Desktop } catch { }
    [pscustomobject]@{
        Name = $Desktop.Name.TrimStart('/')
        State = $Desktop.State.Status
        Image = $Desktop.Config.Image
        Url = $url
        Storage = (@(Get-HomeMount $Desktop) | ForEach-Object { $_.Name }) -join ', '
        Management = Get-Management $Desktop
        CreatedAt = $Desktop.Created
        StartedAt = $Desktop.State.StartedAt
        FinishedAt = $Desktop.State.FinishedAt
    }
}

function Get-DesktopUrl {
    param($Desktop)
    $portProperty = $Desktop.Ports.PSObject.Properties['3001/tcp']
    if (-not $portProperty -or -not $portProperty.Value) {
        throw "Container '$Name' has no published HTTPS port (3001/tcp). Its configuration was preserved."
    }
    $bindings = @($portProperty.Value)
    $binding = $bindings | Where-Object { $_.HostIp -in @('127.0.0.1', '0.0.0.0', '') } | Select-Object -First 1
    if (-not $binding) {
        throw "Container '$Name' has no IPv4 localhost-accessible HTTPS binding."
    }
    return "https://localhost:$($binding.HostPort)"
}

if ($Action -eq 'List') {
    $rows = @(Get-Containers | Where-Object {
        (Get-Management $_) -ne 'Unmanaged' -and (-not $Running -or $_.State.Running)
    } | ForEach-Object { Get-DesktopSummary $_ })
    if ($Json) { ConvertTo-Json -InputObject $rows -Depth 8 -Compress } else { $rows }
    return
}

if ($Action -eq 'Storage') {
    $containers = @(Get-Containers)
    $rows = @(foreach ($volume in @(Get-Volumes)) {
        $owner = $volume.Labels.'io.pdwslc.desktop'
        $managed = Test-Owned $volume.Labels $owner
        # Legacy names are candidates, not proof of ownership.
        if (-not $managed -and $volume.Name -notmatch '-home$') { continue }
        $references = @($containers | Where-Object {
            @($_.Mounts | Where-Object { $_.Type -eq 'volume' -and $_.Name -ceq $volume.Name }).Count -gt 0
        } | ForEach-Object { $_.Name.TrimStart('/') })
        [pscustomobject]@{
            Name = $volume.Name
            Desktop = $owner
            Management = if ($managed) { 'Managed' } else { 'Legacy candidate' }
            Containers = $references -join ', '
            Orphaned = $references.Count -eq 0
        }
    })
    if ($Json) { ConvertTo-Json -InputObject $rows -Depth 8 -Compress } else { $rows }
    return
}

$desktop = Get-Desktop
if ($Action -eq 'Delete') {
    if ($desktop -and (Get-Management $desktop) -eq 'Unmanaged') {
        throw "Container '$Name' is not a managed or recognizable legacy desktop; it was retained."
    }
    $volumeName = "$Name-home"
    $volume = $null
    if ($IncludeStorage) {
        if ($desktop) {
            $homes = @(Get-HomeMount $desktop)
            if ($homes.Count -ne 1 -or $homes[0].Name -cne $volumeName) {
                throw 'Storage deletion requires exactly one expected named home volume at /config. Nothing was deleted.'
            }
        }
        $volume = Get-Volumes | Where-Object { $_.Name -ceq $volumeName } | Select-Object -First 1
        if ($volume) {
            if (-not (Test-Owned $volume.Labels $Name)) {
                $labels = @($volume.Labels.PSObject.Properties | Where-Object { $_ })
                if (-not $Adopt -or $labels.Count -gt 0) {
                    throw "Volume '$volumeName' is not owned by this desktop. Unlabelled legacy storage requires -Adopt. Nothing was deleted."
                }
            }
            $others = @(Get-Containers | Where-Object {
                $_.Name.TrimStart('/') -cne $Name -and
                @($_.Mounts | Where-Object { $_.Type -eq 'volume' -and $_.Name -ceq $volumeName }).Count -gt 0
            })
            if ($others.Count) { throw "Volume '$volumeName' is referenced by another container. Nothing was deleted." }
        }
    }
    if (-not $desktop -and -not $volume) { throw "No desktop or requested home storage found for '$Name'." }
    if ($desktop) {
        if ($desktop.State.Running) { Invoke-Wslc -Arguments @('stop', $Name) | Out-Host }
        Invoke-Wslc -Arguments @('remove', $Name) | Out-Host
        Write-Host "Container '$Name' removed. Software installed in its writable layer was deleted."
    }
    if ($volume) {
        try { Invoke-Wslc -Arguments @('volume', 'remove', $volumeName) | Out-Host }
        catch { throw "Container removal completed, but storage '$volumeName' was retained: $_" }
        Write-Host "Home storage '$volumeName' deleted."
    } else { Write-Host 'Home storage retained (if present).' }
    return
}

if ($Action -in @('Stop', 'Status', 'Logs')) {
    if (-not $desktop) { throw "Container '$Name' does not exist in the selected WSLC session." }
    switch ($Action) {
        'Stop' {
            if ($desktop.State.Running) { Invoke-Wslc -Arguments @('stop', $Name) | Out-Host }
            Write-Host "Desktop '$Name' stopped. Its container and home volume are retained."
        }
        'Status' {
            $row = Get-DesktopSummary $desktop
            if ($Json) { ConvertTo-Json -InputObject $row -Depth 8 -Compress } else { $row }
        }
        'Logs' { Invoke-Wslc -Arguments @('logs', '--tail', '100', $Name) }
    }
    return
}

if ($desktop -and (Get-Management $desktop) -eq 'Unmanaged') {
    throw "Container '$Name' is not a managed or recognizable legacy desktop; it was retained."
}

# Check the readiness probe dependency before creating or starting anything.
$curl = (Get-Command curl.exe -ErrorAction Stop).Source
if (-not $desktop) {
    $volume = "$Name-home"
    $volumes = @(Invoke-Wslc -Arguments @('volume', 'list', '--format', 'json') | ConvertFrom-Json | ForEach-Object { $_ })
    $existingVolume = $volumes | Where-Object { $_.Name -ceq $volume }
    if ($existingVolume) {
        $volumeDetails = Get-Volumes | Where-Object { $_.Name -ceq $volume } | Select-Object -First 1
        if ($volumeDetails.Labels.'io.pdwslc.managed' -and -not (Test-Owned $volumeDetails.Labels $Name)) {
            throw "Volume '$volume' belongs to another desktop; it was retained."
        }
        $references = @(Get-Containers | Where-Object {
            @($_.Mounts | Where-Object { $_.Type -eq 'volume' -and $_.Name -ceq $volume }).Count -gt 0
        })
        if ($references.Count) { throw "Volume '$volume' is already attached to another container; it was retained." }
        Write-Host "Reusing persistent home volume '$volume'..."
    } else {
        Write-Host "Creating persistent home volume '$volume'..."
        Invoke-Wslc -Arguments @('volume', 'create', '--label', 'io.pdwslc.managed=true', '--label', "io.pdwslc.desktop=$Name", $volume) | Out-Host
    }
    Write-Host "Creating '$Name' from '$Image' (the first download can take several minutes)..."
    Invoke-Wslc -Arguments @(
        'run', '-d', '--name', $Name,
        '--label', 'io.pdwslc.managed=true', '--label', "io.pdwslc.desktop=$Name",
        '-e', 'PUID=1000', '-e', 'PGID=1000', '-e', "TZ=$TimeZone",
        '-p', "127.0.0.1:${Port}:3001",
        '-v', "${volume}:/config",
        '--shm-size', '1073741824', $Image
    ) | Out-Host
    $desktop = Get-Desktop
}

# Existing containers retain their image, mounts and published port.
$url = Get-DesktopUrl -Desktop $desktop
if (-not $desktop.State.Running) {
    Write-Host "Starting '$Name'..."
    Invoke-Wslc -Arguments @('start', $Name) | Out-Host
}

Write-Host "Waiting for the desktop at $url ..."
$deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
$ready = $false
do {
    # Accept Webtop's self-signed certificate only for this local readiness probe.
    & $curl --insecure --silent --fail --output NUL --max-time 3 $url
    if ($LASTEXITCODE -eq 0) {
        $ready = $true
        break
    }
    $desktop = Get-Desktop
    if (-not $desktop.State.Running) {
        throw "Desktop '$Name' exited during startup. Run this script with -Action Logs to diagnose it."
    }
    Start-Sleep -Seconds 2
} while ([DateTime]::UtcNow -lt $deadline)

if (-not $ready) {
    throw "Desktop did not respond within $TimeoutSeconds seconds. Run this script with -Action Logs. The container is retained."
}

Write-Host "Desktop ready: $url"
Write-Host 'Webtop uses a self-signed HTTPS certificate; your browser may ask you to accept it for localhost.'
if (-not $NoBrowser -and ($Action -eq 'Open' -or -not $PSBoundParameters.ContainsKey('Action'))) {
    Start-Process $url
}
