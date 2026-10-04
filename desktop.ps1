# Copyright (c) 2026 Sebastian Sejzer.
# Licensed under the MIT License.
#Requires -Version 5.1

[CmdletBinding()]
param(
    [ValidateSet('Start', 'Stop', 'Status', 'Logs')]
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
    [switch] $NoBrowser
)

$ErrorActionPreference = 'Stop'
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

function Get-Desktop {
    $containers = @(Invoke-Wslc -Arguments @('ps', '-a', '--format', 'json') | ConvertFrom-Json)
    $found = $containers | Where-Object { $_.Names.TrimStart('/') -eq $Name }
    if ($found) {
        $details = @(Invoke-Wslc -Arguments @('inspect', '--type', 'container', $Name) | ConvertFrom-Json)
        return $details[0]
    }
    return $null
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

$desktop = Get-Desktop
if ($Action -ne 'Start') {
    if (-not $desktop) { throw "Container '$Name' does not exist in the selected WSLC session." }
    switch ($Action) {
        'Stop' {
            if ($desktop.State.Running) {
                Invoke-Wslc -Arguments @('stop', $Name)
            }
            Write-Host "Desktop '$Name' stopped. Its container and home volume are retained."
        }
        'Status' {
            [pscustomobject]@{
                Name = $Name
                State = $desktop.State.Status
                Image = $desktop.Config.Image
                Url = Get-DesktopUrl -Desktop $desktop
            }
        }
        'Logs' { Invoke-Wslc -Arguments @('logs', '--tail', '100', $Name) }
    }
    return
}

# Check the readiness probe dependency before creating or starting anything.
$curl = (Get-Command curl.exe -ErrorAction Stop).Source
if (-not $desktop) {
    $volume = "$Name-home"
    Write-Host "Creating persistent home volume '$volume'..."
    Invoke-Wslc -Arguments @('volume', 'create', $volume) | Out-Host
    Write-Host "Creating '$Name' from '$Image' (the first download can take several minutes)..."
    Invoke-Wslc -Arguments @(
        'run', '-d', '--name', $Name,
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
if (-not $NoBrowser) { Start-Process $url }
