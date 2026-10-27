# UI smoke test for the app shell (Windows PowerShell 5.1+).
# Starts the published app against a throwaway data root (TABLEPLSU_ROOT) holding one connection to the test
# Postgres, connects from the welcome list with Enter, and checks that the workspace shows "connected".
# Usage: scripts/ui-smoke.ps1 [-Exe artifacts/publish/TablePlsu.App.exe] [-Port 54329] [-Database tableplsu_test]
param(
    [string]$Exe = "artifacts/publish/TablePlsu.App.exe",
    [int]$Port = 54329,
    [string]$Database = "tableplsu_test",
    [string]$User = "postgres"
)
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms

$root = Join-Path ([IO.Path]::GetTempPath()) ("tableplsu-smoke-" + [Guid]::NewGuid().ToString("N"))
$roaming = Join-Path $root "roaming"
New-Item -ItemType Directory -Force $roaming | Out-Null
$json = @"
{
  "version": 1,
  "connections": [
    { "id": "smoke1", "name": "smoke-pg", "color": "#34C759", "tag": "local", "host": "localhost", "port": $Port,
      "user": "$User", "database": "$Database", "passwordMode": "none", "sslMode": "disable" }
  ]
}
"@
[IO.File]::WriteAllText((Join-Path $roaming "connections.json"), $json)

$env:TABLEPLSU_ROOT = $root
$proc = Start-Process -FilePath (Resolve-Path $Exe) -PassThru
try {
    $ae = [System.Windows.Automation.AutomationElement]
    $tree = [System.Windows.Automation.TreeScope]
    $window = $null
    for ($i = 0; $i -lt 50 -and -not $window; $i++) {
        Start-Sleep -Milliseconds 200
        $cond = New-Object System.Windows.Automation.AndCondition(
            (New-Object System.Windows.Automation.PropertyCondition($ae::ProcessIdProperty, $proc.Id)),
            (New-Object System.Windows.Automation.PropertyCondition($ae::ControlTypeProperty, [System.Windows.Automation.ControlType]::Window)),
            (New-Object System.Windows.Automation.PropertyCondition($ae::AutomationIdProperty, "MainWindow")))
        $window = $ae::RootElement.FindFirst($tree::Children, $cond)
    }
    if (-not $window) { throw "Main window did not appear." }
    Write-Output "window: $($proc.MainWindowTitle)"

    $nameCond = New-Object System.Windows.Automation.PropertyCondition($ae::NameProperty, "smoke-pg")
    $item = $null
    for ($i = 0; $i -lt 25 -and -not $item; $i++) { Start-Sleep -Milliseconds 200; $item = $window.FindFirst($tree::Descendants, $nameCond) }
    if (-not $item) { throw "Connection 'smoke-pg' not in the welcome list." }
    Write-Output "welcome list shows: smoke-pg"

    $window.SetFocus()
    $listItem = [System.Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($item)
    try { $listItem.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select() } catch { }
    try { $listItem.SetFocus() } catch { $item.SetFocus() }
    [System.Windows.Forms.SendKeys]::SendWait("{ENTER}")

    $ok = $false
    $title = ""
    for ($i = 0; $i -lt 50 -and -not $ok; $i++) {
        Start-Sleep -Milliseconds 200
        $proc.Refresh()
        $title = $proc.MainWindowTitle
        $status = $window.FindFirst($tree::Descendants, (New-Object System.Windows.Automation.PropertyCondition($ae::AutomationIdProperty, "ConnectionStatus")))
        $ok = ($title -like "smoke-pg / $Database*") -and ($null -ne $status) -and ($status.Current.Name -eq "connected")
    }
    if (-not $ok) {
        $names = $window.FindAll($tree::Descendants, [System.Windows.Automation.Condition]::TrueCondition) | ForEach-Object { $_.Current.AutomationId + "=" + $_.Current.Name } | Where-Object { $_ -ne "=" }
        throw "Workspace did not reach 'connected' (title: $title). Elements: $($names -join '; ')"
    }
    Write-Output "workspace: $title - status $($status.Current.Name)"

    # Ctrl+T opens a tab (an empty tab until the SQL editor feature registers its factory).
    [System.Windows.Forms.SendKeys]::SendWait("^t")
    Start-Sleep -Milliseconds 500
    $tab = $window.FindFirst($tree::Descendants, (New-Object System.Windows.Automation.PropertyCondition($ae::NameProperty, "New Tab")))
    if (-not $tab) { throw "Ctrl+T did not open a tab." }
    Write-Output "Ctrl+T opened: New Tab"
    Write-Output "SMOKE OK"
}
finally {
    if (-not $proc.HasExited) { $proc.Kill() }
    Remove-Item -Recurse -Force $root -ErrorAction SilentlyContinue
}
