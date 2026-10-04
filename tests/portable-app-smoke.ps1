# Verify single-executable deployment on a local Windows disk, with no sidecars.
[CmdletBinding()]
param([string] $AppPath)
$ErrorActionPreference = 'Stop'
if (-not $AppPath) { $AppPath = Join-Path $PSScriptRoot '..\artifacts\app\win-x64\PDWSLC.exe' }
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('pdwslc-portable-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temporary | Out-Null
try {
    $copied = Join-Path $temporary 'PDWSLC.exe'
    Copy-Item $AppPath $copied
    if (@(Get-ChildItem $temporary -File).Count -ne 1) { throw 'Test copy contains companion files.' }
    & (Join-Path $PSScriptRoot 'native-app-smoke.ps1') -AppPath $copied
    if (@(Get-ChildItem $temporary -File).Count -ne 1) { throw 'App required files beside the executable.' }
    Write-Output 'PASS: only PDWSLC.exe copied; no installed .NET or companion scripts/DLLs required'
} finally {
    Remove-Item $temporary -Recurse -Force
}
