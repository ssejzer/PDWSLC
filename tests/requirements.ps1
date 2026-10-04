# Installer helper tests. All feature changes and WSL commands are mocked.
$ErrorActionPreference = 'Stop'
$path = Join-Path $PSScriptRoot '..\src\PDWSLC.App\runtime\Requirements.ps1'
$tokens=$null; $errors=$null
[System.Management.Automation.Language.Parser]::ParseFile($path,[ref]$tokens,[ref]$errors) | Out-Null
if ($errors.Count) { throw ($errors | Out-String) }
$source = Get-Content $path -Raw
$main = $source.Substring($source.IndexOf('if (-not $ResultPath)'))
# Exercise setup logic without elevation. The real helper retains this gate.
$gate = "if (-not `$administrator.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Windows administrator consent is required for setup.' }"
if (-not $main.Contains($gate)) { throw 'Expected administrator guard is missing.' }
$main = $main.Replace($gate, '').Replace('if ($result.Succeeded) { exit 0 } else { exit 1 }', '')
function Find-Tool([string] $name) { if ($name -eq 'wsl.exe') { return 'Test-Wsl' }; return $null }
function Test-Wsl {
    $script:commands += ,@($args)
    $global:LASTEXITCODE = $script:nativeExitCode
}
function Get-WindowsOptionalFeature {
    param([switch] $Online, [string] $FeatureName)
    if ($FeatureName -eq 'VirtualMachinePlatform') { return @{State=$script:featureState} }
    return @{State='Disabled'}
}
function Enable-WindowsOptionalFeature {
    param([switch] $Online, [string] $FeatureName, [switch] $All, [switch] $NoRestart)
    if (-not $NoRestart) { throw 'Installer tried to restart Windows.' }
    $script:enabled += $FeatureName
    return @{RestartNeeded=$script:enableNeedsRestart}
}
function Reset-Fixture {
    $script:Mode='Install'
    $script:featureState='Enabled'
    $script:nativeExitCode=0
    $script:enableNeedsRestart=$false
    $script:commands=@()
    $script:enabled=@()
}
function Assert-True($condition, $message) { if (-not $condition) { throw $message } }
$ResultPath = Join-Path ([IO.Path]::GetTempPath()) ('pdwslc-requirements-' + [Guid]::NewGuid().ToString('N') + '.json')
function Execute-Fixture {
    & ([scriptblock]::Create($main)) | Out-Null
    Get-Content $ResultPath -Raw | ConvertFrom-Json
}
try {
    Reset-Fixture
    $result = Execute-Fixture
    Assert-True ($result.Succeeded -and $commands.Count -eq 1) 'Install did not run exactly once'
    Assert-True (($commands[0] -join ' ') -eq '--install --no-distribution --web-download') 'Install created an unwanted distribution or used wrong source'
    Assert-True ($enabled.Count -eq 0) 'Enabled features were changed unnecessarily'
    Reset-Fixture
    $Mode='Update'
    $result = Execute-Fixture
    Assert-True (($commands[0] -join ' ') -eq '--update --web-download') 'Update used unexpected commands'
    Reset-Fixture
    $featureState='Disabled'; $enableNeedsRestart=$true
    $result = Execute-Fixture
    Assert-True ($result.Succeeded -and $result.RestartRequired -and $commands.Count -eq 0) 'Installer continued before feature reboot'
    Assert-True ($enabled.Count -eq 1 -and $enabled[0] -eq 'VirtualMachinePlatform') 'Wrong feature was enabled'
    Reset-Fixture
    $featureState='EnablePending'
    $result=Execute-Fixture
    Assert-True ($result.RestartRequired -and $commands.Count -eq 0 -and $enabled.Count -eq 0) 'Pending feature caused repeated installation'
    Reset-Fixture
    $nativeExitCode=3010
    $result=Execute-Fixture
    Assert-True ($result.Succeeded -and $result.RestartRequired) 'Native reboot-required result was lost'
    Reset-Fixture
    $nativeExitCode=5
    $result=Execute-Fixture
    Assert-True (-not $result.Succeeded -and $result.Error -like '*exit code 5*') 'Native installer failure was hidden'
    Write-Output 'PASS: installer feature guards, no-distribution install, update, no automatic restart, reboot handling and failure reporting (all mutations mocked)'
} finally { if (Test-Path $ResultPath) { Remove-Item $ResultPath -Force } }
