# Build one portable Windows executable with .NET and scripts bundled inside.
[CmdletBinding()]
param([ValidateSet('win-x64', 'win-arm64')] [string] $Runtime = 'win-x64')
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'src\PDWSLC.App\PDWSLC.App.csproj'
$destination = Join-Path $PSScriptRoot "artifacts\app\$Runtime"
$staging = Join-Path $PSScriptRoot ('artifacts\publish-' + [Guid]::NewGuid().ToString('N'))
try {
    & dotnet publish $project -c Release -r $Runtime --self-contained true -o $staging
    if ($LASTEXITCODE -ne 0) { throw "Application build failed (exit code $LASTEXITCODE). Install the .NET 10 SDK and retry." }
    $executable = Join-Path $staging 'PDWSLC.exe'
    if (-not (Test-Path $executable -PathType Leaf)) { throw 'Publish did not produce PDWSLC.exe.' }
    # Optional debugger symbols are not runtime dependencies and aren't distributed.
    $extra = @(Get-ChildItem $staging -Recurse -File | Where-Object { $_.FullName -ne $executable -and $_.Extension -ne '.pdb' })
    if ($extra.Count) { throw 'Publish produced companion files. Single-file packaging needs attention.' }
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    Copy-Item $executable (Join-Path $destination 'PDWSLC.exe') -Force
    Write-Host "Built portable executable: $destination\PDWSLC.exe"
    Write-Host 'Only PDWSLC.exe needs to be copied. WSL/WSLC prerequisites still apply.'
} finally {
    if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
}
