# Read-only WSLC smoke check. Folder edits use a disposable metadata file.
[CmdletBinding()]
param([string] $AppPath)
$ErrorActionPreference = 'Stop'
if (-not $AppPath) { $AppPath = Join-Path $PSScriptRoot '..\artifacts\app\win-x64\PDWSLC.exe' }
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class PDWSLCWindowFocus {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr window);
}
'@
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('pdwslc-ui-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temporary | Out-Null
$previous = $env:PDWSLC_FOLDER_DATA_PATH
$env:PDWSLC_FOLDER_DATA_PATH = Join-Path $temporary 'folders.json'
try { $process = Start-Process -FilePath $AppPath -PassThru }
finally { $env:PDWSLC_FOLDER_DATA_PATH = $previous }
try {
    $deadline = [DateTime]::UtcNow.AddSeconds(45)
    $window = $null
    do {
        Start-Sleep -Milliseconds 200
        $process.Refresh()
        if ($process.HasExited) { throw "App exited unexpectedly: $($process.ExitCode)" }
        if ($process.MainWindowHandle -ne [IntPtr]::Zero) { $window = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle) }
    } until ($window -or [DateTime]::UtcNow -gt $deadline)
    if (-not $window) { throw 'No native window appeared.' }
    $scope = [System.Windows.Automation.TreeScope]::Descendants
    function Find-Control([string] $id, $root = $window) {
        $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
        return $root.FindFirst($scope, $condition)
    }
    function Elements($type) {
        $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, $type)
        $processCondition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $process.Id)
        $roots = [System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, $processCondition)
        for ($i=0; $i -lt $roots.Count; $i++) {
            if ($roots[$i].Current.ControlType -eq $type) { $roots[$i] }
            $items = $roots[$i].FindAll($scope, $condition)
            for ($j=0; $j -lt $items.Count; $j++) { $items[$j] }
        }
    }
    function Named($type, [string] $name) { @(Elements $type | Where-Object { $_.Current.Name -like $name }) | Select-Object -First 1 }
    function Invoke-Element($item) {
        if (-not $item) { throw 'Expected UI action was missing.' }
        $item.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    }
    function Context($item) {
        [PDWSLCWindowFocus]::SetForegroundWindow($process.MainWindowHandle) | Out-Null
        $item.SetFocus()
        [System.Windows.Forms.SendKeys]::SendWait('+{F10}')
        Start-Sleep -Milliseconds 250
    }
    function Close-Menu { [System.Windows.Forms.SendKeys]::SendWait('{ESC}') }
    $status = Find-Control 'StatusText'
    do { Start-Sleep -Milliseconds 200; $state = $status.Current.Name }
    until ($state -like 'Completed.*' -or $state -like 'Operation failed*' -or [DateTime]::UtcNow -gt $deadline)
    if ($state -notlike 'Completed.*') { throw "Discovery failed: $state" }
    $operationOutput = (Find-Control 'OutputBox').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
    if ($operationOutput -notlike '*Pre-flight passed.*') { throw 'Startup did not pass prerequisite checks before discovery.' }
    if ($operationOutput -like '*Installing WSL*' -or $operationOutput -like '*Updating WSL*') { throw 'Healthy host unexpectedly requested prerequisite installation.' }
    if (-not (Find-Control 'DesktopTree')) { throw 'Explorer tree missing.' }
    Write-Output 'Checking Host context menu'
    $hostItem = Named ([System.Windows.Automation.ControlType]::TreeItem) 'Host'
    if (-not $hostItem) { throw 'Host root missing.' }
    Context $hostItem
    $menu = @(Elements ([System.Windows.Automation.ControlType]::MenuItem))
    if (@($menu | Where-Object { $_.Current.Name -like '*Delete*' }).Count) { throw 'Host exposed a delete action.' }
    $newFolder = $menu | Where-Object { $_.Current.Name -like 'New folder*' } | Select-Object -First 1
    Invoke-Element $newFolder
    Start-Sleep -Milliseconds 250
    $dialog = Named ([System.Windows.Automation.ControlType]::Window) 'New folder'
    $field = Find-Control 'FolderName' $dialog
    $field.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('Smoke folder')
    Invoke-Element (Named ([System.Windows.Automation.ControlType]::Button) 'Create folder')
    Start-Sleep -Milliseconds 250
    if (-not (Named ([System.Windows.Automation.ControlType]::TreeItem) 'Smoke folder')) { throw 'Folder creation did not update tree.' }
    $saved = Get-Content (Join-Path $temporary 'folders.json') -Raw
    if ($saved -notmatch '"Name":\s*"Smoke folder"') { throw 'Folder was not saved.' }
    $hostItem = Named ([System.Windows.Automation.ControlType]::TreeItem) 'Host'
    Write-Output 'Checking new desktop form'
    Context $hostItem
    Invoke-Element (Named ([System.Windows.Automation.ControlType]::MenuItem) 'New desktop*')
    Start-Sleep -Milliseconds 250
    $dialog = Named ([System.Windows.Automation.ControlType]::Window) 'New desktop'
    foreach ($id in @('NameBox','ImageBox','PortBox','TimeZoneBox')) { if (-not (Find-Control $id $dialog)) { throw "Missing creation field: $id" } }
    $dialog.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
    $desktopItem = @(Elements ([System.Windows.Automation.ControlType]::TreeItem) | Where-Object { $_.Current.Name -like '*  *' -and $_.Current.Name -notlike '* storage*' }) | Select-Object -First 1
    if ($desktopItem) {
        Write-Output 'Checking desktop Properties'
        Context $desktopItem
        $menu = @(Elements ([System.Windows.Automation.ControlType]::MenuItem))
        if ($menu[-1].Current.Name -ne 'Properties') { throw 'Properties is not the last desktop action.' }
        Invoke-Element $menu[-1]
        Start-Sleep -Milliseconds 250
        $dialog = Named ([System.Windows.Automation.ControlType]::Window) '* Properties'
        if (-not (Find-Control 'DesktopProperties' $dialog) -or -not (Find-Control 'DesktopLogs' $dialog)) { throw 'Properties fields/logs missing.' }
        $deadline = [DateTime]::UtcNow.AddSeconds(45)
        do { Start-Sleep -Milliseconds 200; $state = $status.Current.Name }
        until ($state -like 'Completed.*' -or $state -like 'Operation failed*' -or [DateTime]::UtcNow -gt $deadline)
        if ($state -notlike 'Completed.*') { throw 'Properties lookup failed.' }
        $dialog.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
    }
    $attachedItems = @(Elements ([System.Windows.Automation.ControlType]::TreeItem) | Where-Object { $_.Current.Name -like '*attached storage' })
    foreach ($storageItem in $attachedItems) {
        $parent = [System.Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($storageItem)
        if ($parent.Current.ControlType -ne [System.Windows.Automation.ControlType]::TreeItem -or $parent.Current.Name -notlike '*  *' -or $parent.Current.Name -like '* storage*') {
            throw 'Attached storage is not nested beneath a desktop.'
        }
    }
    if ($attachedItems.Count) {
        Context $attachedItems[0]
        $menu = @(Elements ([System.Windows.Automation.ControlType]::MenuItem))
        if ($menu.Count -ne 1 -or $menu[0].Current.Name -ne 'Properties') { throw 'Attached storage exposed independent move/delete actions.' }
        Close-Menu
    }
    Write-Output 'PASS: Host tree/protection, folder persistence, creation form, Properties, attached storage hierarchy/protection and read-only discovery' 
} finally {
    if (-not $process.HasExited) { $process.CloseMainWindow() | Out-Null; if (-not $process.WaitForExit(5000)) { $process.Kill() } }
    Remove-Item $temporary -Recurse -Force
}
