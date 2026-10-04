# Uses an in-memory WSLC runtime; never invokes the installed runtime.
$ErrorActionPreference = 'Stop'
$path = Join-Path $PSScriptRoot '..\desktop.ps1'
$tokens = $null
$errors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($path, [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw ($errors | Out-String) }
$functions = ($ast.EndBlock.Statements | Where-Object {
    $_ -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $_.Name -ne 'Invoke-Wslc'
} | ForEach-Object { $_.Extent.Text }) -join "`n"
$source = Get-Content $path -Raw
$main = $source.Substring($source.IndexOf("if (`$Action -eq 'List')"))
$main = $main.Replace("`$curl = (Get-Command curl.exe -ErrorAction Stop).Source", "`$curl = 'Test-Curl'")
function Test-Curl { $global:LASTEXITCODE = 0 }
function Start-Process { param($FilePath) $script:opened = $FilePath }
Invoke-Expression $functions
function Invoke-Wslc {
    param([string[]] $Arguments)
    $script:calls += ,$Arguments
    switch ($Arguments[0]) {
        ps { return (ConvertTo-Json -InputObject @($script:containers | ForEach-Object { @{Names=$_.Name.TrimStart('/')} }) -Depth 12 -Compress) }
        inspect { return (ConvertTo-Json -InputObject @($script:containers | Where-Object { $_.Name.TrimStart('/') -ceq $Arguments[3] }) -Depth 12 -Compress) }
        volume {
            switch ($Arguments[1]) {
                list { return (ConvertTo-Json -InputObject $script:volumes -Depth 12 -Compress) }
                inspect { return (ConvertTo-Json -InputObject @($script:volumes | Where-Object { $_.Name -ceq $Arguments[2] }) -Depth 12 -Compress) }
                remove { if ($script:failVolumeRemoval) { throw 'simulated volume removal failure' }; $script:volumes = @($script:volumes | Where-Object { $_.Name -cne $Arguments[2] }); return }
                create { $script:volumes=@([pscustomobject]@{Name='desktop-home'; Labels=@{'io.pdwslc.managed'='true'; 'io.pdwslc.desktop'='desktop'}}); return }
                default { throw 'Unexpected volume operation' }
            }
        }
        stop { return }
        start { $script:containers[0].State.Running=$true; return }
        run { $script:containers=@($script:template); return 'test-id' }
        remove { $script:containers = @($script:containers | Where-Object { $_.Name.TrimStart('/') -cne $Arguments[1] }); return }
        logs { return 'test logs' }
        default { throw "Unexpected runtime call: $Arguments" }
    }
}
function Reset-Fixture {
    $script:Name = 'desktop'
    $script:Running = $false
    $script:Json = $false
    $script:NoBrowser = $true
    $script:TimeoutSeconds = 1
    $script:Port = 3001
    $script:Image = 'lscr.io/linuxserver/webtop:ubuntu-kde'
    $script:TimeZone = 'Asia/Jerusalem'
    $script:opened = $null
    $script:failVolumeRemoval = $false
    $script:IncludeStorage = $false
    $script:Adopt = $false
    $script:calls = @()
    $labels = @{ 'io.pdwslc.managed'='true'; 'io.pdwslc.desktop'='desktop' }
    $script:containers = @([pscustomobject]@{
        Name='/desktop'; Config=@{Image='lscr.io/linuxserver/webtop:ubuntu-kde'; Labels=$labels}
        State=@{Running=$true; Status='running'}
        Ports=@{'3001/tcp'=@(@{HostIp='127.0.0.1'; HostPort='3001'})}
        Mounts=@(@{Type='volume'; Name='desktop-home'; Destination='/config'})
    })
    $script:volumes = @([pscustomobject]@{Name='desktop-home'; Labels=$labels})
    $script:template = $script:containers[0]
}
function Assert-True($condition, $message) { if (-not $condition) { throw $message } }
function Assert-Blocked {
    $blocked = $false
    try { & ([scriptblock]::Create($main)) | Out-Null } catch { $blocked = $true }
    Assert-True $blocked 'Expected refusal'
    $mutations = @($script:calls | Where-Object { $_[0] -in @('stop','remove') -or ($_[0] -eq 'volume' -and $_[1] -eq 'remove') })
    Assert-True ($mutations.Count -eq 0) 'Refusal mutated runtime'
}
Reset-Fixture
$Action='List'
$rows = @(& ([scriptblock]::Create($main)))
Assert-True ($rows.Count -eq 1 -and $rows[0].Storage -eq 'desktop-home') 'Managed listing failed'
$Running=$true; $containers[0].State.Running=$false
Assert-True (@(& ([scriptblock]::Create($main))).Count -eq 0) 'Running filter failed'
Reset-Fixture
$containers[0].Config.Labels=$null
$Action='List'
Assert-True ((& ([scriptblock]::Create($main))).Management -eq 'Legacy') 'Legacy listing failed'
$containers[0].Config.Image='unrelated/image'
Assert-True (@(& ([scriptblock]::Create($main))).Count -eq 0) 'Unmanaged container leaked into list'
Reset-Fixture
$containers=@(); $Action='Storage'
Assert-True ((& ([scriptblock]::Create($main))).Orphaned) 'Orphan storage listing failed'
Reset-Fixture
$Action='Delete'
& ([scriptblock]::Create($main)) | Out-Null
Assert-True ($containers.Count -eq 0 -and $volumes.Count -eq 1) 'Container deletion did not preserve home'
Reset-Fixture
$Action='Delete'; $IncludeStorage=$true
& ([scriptblock]::Create($main)) | Out-Null
Assert-True ($containers.Count -eq 0 -and $volumes.Count -eq 0) 'Owned storage deletion failed'
Reset-Fixture
$Action='Delete'; $IncludeStorage=$true; $volumes[0].Labels=$null
Assert-Blocked
$Adopt=$true
& ([scriptblock]::Create($main)) | Out-Null
Assert-True ($volumes.Count -eq 0) 'Legacy storage adoption failed'
Reset-Fixture
$Action='Delete'; $IncludeStorage=$true
$other = [pscustomobject]@{Name='/other'; Mounts=@(@{Type='volume'; Name='desktop-home'; Destination='/data'})}
$containers += $other
Assert-Blocked
Reset-Fixture
$Action='Delete'; $IncludeStorage=$true; $volumes[0].Labels=@{'io.pdwslc.managed'='true'; 'io.pdwslc.desktop'='other'}
$Adopt=$true
Assert-Blocked
Reset-Fixture
$Action='Delete'; $containers[0].Config.Image='unrelated/image'; $containers[0].Config.Labels=$null
Assert-Blocked
Reset-Fixture
$Action='Delete'; $IncludeStorage=$true; $containers=@()
& ([scriptblock]::Create($main)) | Out-Null
Assert-True ($volumes.Count -eq 0) 'Owned orphan storage deletion failed'
Reset-Fixture
$Action='Stop'
& ([scriptblock]::Create($main)) | Out-Null
Assert-True ($containers.Count -eq 1 -and $volumes.Count -eq 1) 'Stop changed resources'
$Action='Status'
Assert-True ((& ([scriptblock]::Create($main))).Url -eq 'https://localhost:3001') 'Status URL failed'
$Action='Logs'
Assert-True ((& ([scriptblock]::Create($main))) -eq 'test logs') 'Logs failed'
Write-Output 'PASS: parsing, list, running filter, legacy discovery, storage, stop, status, logs, deletion and storage ownership guards'

Reset-Fixture
$Action='Start'; $containers[0].State.Running=$false
& ([scriptblock]::Create($main)) | Out-Null
Assert-True ($containers[0].State.Running) 'Resume failed'
Assert-True (@($calls | Where-Object { $_[0] -eq 'run' }).Count -eq 0) 'Resume recreated desktop'
Reset-Fixture
$Action='Open'; $NoBrowser=$false
& ([scriptblock]::Create($main)) | Out-Null
Assert-True ($opened -eq 'https://localhost:3001') 'Open did not open browser'
Reset-Fixture
$Action='Start'; $containers=@(); $volumes=@()
& ([scriptblock]::Create($main)) | Out-Null
$runCall = $calls | Where-Object { $_[0] -eq 'run' }
Assert-True ($runCall -contains 'io.pdwslc.managed=true') 'Creation omitted ownership label'
Reset-Fixture
$Action='Start'; $containers=@()
& ([scriptblock]::Create($main)) | Out-Null
Assert-True (@($calls | Where-Object { $_[0] -eq 'volume' -and $_[1] -eq 'create' }).Count -eq 0) 'Existing storage recreated'
Write-Output 'PASS: start, resume, open, ownership labels and volume reuse'

Reset-Fixture
$Action='Delete'; $IncludeStorage=$true; $containers[0].Mounts[0].Type='bind'
Assert-Blocked
Reset-Fixture
$Action='Delete'; $IncludeStorage=$true; $failVolumeRemoval=$true
$failure = $null
try { & ([scriptblock]::Create($main)) | Out-Null } catch { $failure = $_.ToString() }
Assert-True ($failure -like '*storage*retained*') 'Partial deletion did not report retained storage'
Assert-True ($containers.Count -eq 0 -and $volumes.Count -eq 1) 'Partial deletion lost storage'
foreach ($arguments in @(@{Action='Delete'}, @{Action='List'; IncludeStorage=$true}, @{Action='Stop'; Running=$true}, @{Action='Start'; Adopt=$true})) {
    $blocked=$false
    try { & $path @arguments | Out-Null } catch { $blocked=$true }
    Assert-True $blocked 'Invalid options were accepted'
}
Write-Output 'PASS: bind-mount protection, partial deletion reporting and argument validation'

Reset-Fixture
$Action='List'; $Json=$true
$jsonOutput = & ([scriptblock]::Create($main))
Assert-True ($jsonOutput.StartsWith('[') -and ($jsonOutput | ConvertFrom-Json)[0].Name -eq 'desktop') 'JSON desktop list failed'
$containers=@()
Assert-True ((& ([scriptblock]::Create($main))) -eq '[]') 'Empty JSON list must be an array'
$Action='Storage'
$jsonOutput = & ([scriptblock]::Create($main))
Assert-True (($jsonOutput | ConvertFrom-Json)[0].Orphaned) 'JSON storage list failed'
Write-Output 'PASS: JSON desktop and storage interface'

Reset-Fixture
$containers[0] | Add-Member -NotePropertyName Created -NotePropertyValue '2026-10-04T10:00:00Z'
$containers[0].State.StartedAt = '2026-10-04T10:01:00Z'
$containers[0].State.FinishedAt = '0001-01-01T00:00:00Z'
$Action='Status'; $Json=$true
$details = (& ([scriptblock]::Create($main))) | ConvertFrom-Json
Assert-True ($details.CreatedAt -eq '2026-10-04T10:00:00Z' -and $details.StartedAt -eq '2026-10-04T10:01:00Z') 'Runtime timestamps were lost'
Assert-True ($details.FinishedAt -eq '0001-01-01T00:00:00Z') 'Unset stop timestamp was invented'
Write-Output 'PASS: runtime creation/start/stop properties'
