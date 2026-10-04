# Built-in Windows/WSL prerequisite check and narrowly scoped elevated setup.
[CmdletBinding()]
param(
    [ValidateSet('Check', 'Install', 'Update')][string] $Mode = 'Check',
    [string] $ResultPath
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
$OutputEncoding = [Console]::OutputEncoding
# Refresh discovery after an installation without changing the parent's PATH.
$machinePath = [Environment]::GetEnvironmentVariable('Path', 'Machine')
$userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
$env:Path = "$env:Path;$machinePath;$userPath;$env:ProgramFiles\WSL"
function Find-Tool([string] $name) {
    $command = Get-Command $name -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($command) { return $command.Source }
    return $null
}
function Get-WslVersion {
    $executable = Find-Tool 'wsl.exe'
    if (-not $executable) { return $null }
    try { $output = & $executable --version 2>&1 } catch { return $null }
    if ($LASTEXITCODE -ne 0) { return $null }
    $text = ($output -join "`n").Replace("`0", '')
    $match = [regex]::Match($text, '(?m)^\s*WSL\s+[^\r\n\d]*(\d+\.\d+\.\d+(?:\.\d+)?)')
    if ($match.Success) { return $match.Groups[1].Value }
    return $null
}
function Get-FeatureEnabled {
    try {
        $feature = Get-CimInstance Win32_OptionalFeature -Filter "Name='VirtualMachinePlatform'" -ErrorAction Stop
        if ($feature) { return ($feature.InstallState -eq 1) }
    } catch { }
    return $null
}
if ($Mode -eq 'Check') {
    $firmware = $null
    try {
        $computer = Get-CimInstance Win32_ComputerSystem -ErrorAction Stop
        $processors = @(Get-CimInstance Win32_Processor -ErrorAction Stop)
        if ($computer.HypervisorPresent) { $firmware = $true }
        elseif ($processors.Count) { $firmware = @($processors | Where-Object { $_.VirtualizationFirmwareEnabled }).Count -gt 0 }
    } catch { }
    $bootIdentity = $null
    try { $bootIdentity = (Get-CimInstance Win32_OperatingSystem -ErrorAction Stop).LastBootUpTime.ToUniversalTime().ToString('o') } catch { }
    $wslc = Find-Tool 'wslc.exe'
    $wslcAvailable = $false
    if ($wslc) {
        try {
            $null = & $wslc --version 2>&1
            $wslcAvailable = $LASTEXITCODE -eq 0
        } catch { $wslcAvailable = $false }
    }
    [pscustomobject]@{
        BootIdentity = $bootIdentity
        WindowsSupported = [Environment]::OSVersion.Version.Build -ge 19041
        PowerShellSupported = $PSVersionTable.PSVersion -ge [version]'5.1'
        CurlAvailable = [bool](Find-Tool 'curl.exe')
        WslVersion = Get-WslVersion
        WslcAvailable = $wslcAvailable
        VirtualMachinePlatformEnabled = Get-FeatureEnabled
        FirmwareVirtualizationAvailable = $firmware
    } | ConvertTo-Json -Compress
    exit 0
}
if (-not $ResultPath) { throw 'Setup requires a result file path.' }
$result = @{ Succeeded = $false; RestartRequired = $false; Error = $null }
try {
    $administrator = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    if (-not $administrator.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Windows administrator consent is required for setup.' }
    # No shutdown, distribution installation, container/session removal, or restart.
    $feature = Get-WindowsOptionalFeature -Online -FeatureName VirtualMachinePlatform
    if ($feature.State -eq 'EnablePending' -or $feature.State -eq 'DisablePending') {
        $result.Succeeded = $true; $result.RestartRequired = $true
    } elseif ($feature.State -ne 'Enabled') {
        Write-Host 'Enabling Virtual Machine Platform...'
        $enabled = Enable-WindowsOptionalFeature -Online -FeatureName VirtualMachinePlatform -All -NoRestart
        $result.RestartRequired = [bool]$enabled.RestartNeeded
        if ($result.RestartRequired) { $result.Succeeded = $true }
    }
    if (-not $result.RestartRequired) {
        $wsl = Find-Tool 'wsl.exe'
        if (-not $wsl) {
            Write-Host 'Enabling the Windows WSL component...'
            $enabled = Enable-WindowsOptionalFeature -Online -FeatureName Microsoft-Windows-Subsystem-Linux -All -NoRestart
            if ($enabled.RestartNeeded) { $result.Succeeded = $true; $result.RestartRequired = $true }
            $wsl = Find-Tool 'wsl.exe'
        }
        if (-not $result.RestartRequired) {
            if (-not $wsl) { throw 'wsl.exe is unavailable after enabling Windows components. Restart Windows and retry.' }
            if ($Mode -eq 'Install') { & $wsl --install --no-distribution --web-download }
            else { & $wsl --update --web-download }
            $code = $LASTEXITCODE
            if ($code -in @(3010, 1641)) { $result.RestartRequired = $true }
            elseif ($code -ne 0) { throw "WSL setup failed (exit code $code)." }
            $result.Succeeded = $true
            foreach ($featureName in @('VirtualMachinePlatform', 'Microsoft-Windows-Subsystem-Linux')) {
                $state = (Get-WindowsOptionalFeature -Online -FeatureName $featureName).State
                if ($state -in @('EnablePending', 'DisablePending')) { $result.RestartRequired = $true }
            }
        }
    }
} catch { $result.Error = $_.ToString(); Write-Host $result.Error }
$result | ConvertTo-Json -Compress | Set-Content -LiteralPath $ResultPath -Encoding UTF8
if ($result.Succeeded) { exit 0 } else { exit 1 }
