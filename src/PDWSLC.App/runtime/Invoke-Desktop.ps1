# UTF-8 process boundary used by the native Windows application.
# All policy and lifecycle operations are implemented by desktop.ps1.
[CmdletBinding()]
param(
    [string] $Action,
    [string] $Name,
    [string] $Session,
    [string] $Image,
    [int] $Port,
    [string] $TimeZone,
    [switch] $IncludeStorage,
    [switch] $Adopt,
    [switch] $Json
)
$ErrorActionPreference = 'Stop'
$machinePath = [Environment]::GetEnvironmentVariable('Path', 'Machine')
$userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
$env:Path = "$env:Path;$machinePath;$userPath;$env:ProgramFiles\WSL"
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
$OutputEncoding = [Console]::OutputEncoding
try {
    & (Join-Path $PSScriptRoot 'desktop.ps1') @PSBoundParameters
    exit 0
} catch {
    [Console]::Error.WriteLine($_.ToString())
    exit 1
}
