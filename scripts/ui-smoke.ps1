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
        # The first Enter can be lost when the window has not taken the foreground yet; resend once.
        if ($i -eq 20 -and $proc.MainWindowTitle -eq "TablePlsu") {
            $window.SetFocus(); try { $listItem.SetFocus() } catch { }
            [System.Windows.Forms.SendKeys]::SendWait("{ENTER}")
        }
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

    function Wait-For([scriptblock]$probe, [int]$tries = 50) {
        for ($i = 0; $i -lt $tries; $i++) { $r = & $probe; if ($r) { return $r }; Start-Sleep -Milliseconds 200 }
        return $null
    }
    function Id($id) { New-Object System.Windows.Automation.PropertyCondition($ae::AutomationIdProperty, $id) }
    function Named($name) { New-Object System.Windows.Automation.PropertyCondition($ae::NameProperty, $name) }

    # Ctrl+T opens a SQL editor tab.
    $before = $window.FindAll($tree::Descendants, (Id "RunButton")).Count
    $window.SetFocus()
    [System.Windows.Forms.SendKeys]::SendWait("^t")
    $runButton = Wait-For { $all = $window.FindAll($tree::Descendants, (Id "RunButton")); if ($all.Count -gt $before -or ($before -eq 0 -and $all.Count -gt 0)) { $all[$all.Count - 1] } }
    if (-not $runButton) { throw "Ctrl+T did not open a SQL editor tab." }
    Write-Output "Ctrl+T opened a SQL editor tab"

    # Type a query and run it with Ctrl+Enter; the result bar must report rows.
    $textArea = $window.FindFirst($tree::Descendants, (New-Object System.Windows.Automation.PropertyCondition($ae::ClassNameProperty, "TextArea")))
    if ($textArea) { try { $textArea.SetFocus() } catch { } }
    Start-Sleep -Milliseconds 300
    [System.Windows.Forms.SendKeys]::SendWait("select {*} from sales.orders")
    Start-Sleep -Milliseconds 500
    # Close the autocomplete popup if it is open (it is a separate top-level window of the process).
    $popupCond = New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition($ae::ProcessIdProperty, $proc.Id)),
        (New-Object System.Windows.Automation.NotCondition((Id "MainWindow"))))
    if ($ae::RootElement.FindFirst($tree::Children, $popupCond)) { [System.Windows.Forms.SendKeys]::SendWait("{ESC}"); Start-Sleep -Milliseconds 200 }
    [System.Windows.Forms.SendKeys]::SendWait("^{ENTER}")
    $summary = Wait-For { $s = $window.FindFirst($tree::Descendants, (Id "ResultSummary")); if ($s -and $s.Current.Name -match '^[1-9][\d,.]* rows?') { $s } } 75
    if (-not $summary) {
        $s = $window.FindFirst($tree::Descendants, (Id "ResultSummary"))
        throw "Ctrl+Enter did not show rows for sales.orders (result bar: '$(if ($s) { $s.Current.Name })')."
    }
    Write-Output "query result: $($summary.Current.Name)"

    # Open public.customers from the sidebar: search, select the exact row, Enter.
    $search = Wait-For { $window.FindFirst($tree::Descendants, (Id "ItemsSearch")) }
    if (-not $search) { throw "Sidebar search box not found." }
    $search.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue("customers")
    $row = Wait-For {
        $texts = $window.FindAll($tree::Descendants, (Named "customers"))
        foreach ($t in $texts) {
            if ($t.Current.AutomationId -ne "ItemText") { continue }
            $p = [System.Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($t)
            while ($p -and $p.Current.ControlType -ne [System.Windows.Automation.ControlType]::ListItem) { $p = [System.Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($p) }
            if ($p) { return $p }
        }
    }
    if (-not $row) { throw "Sidebar did not list 'customers'." }
    $row.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    $search.SetFocus()
    [System.Windows.Forms.SendKeys]::SendWait("{ENTER}")
    $count = Wait-For { $c = $window.FindFirst($tree::Descendants, (Id "RowCountButton")); if ($c -and $c.Current.Name -match 'rows') { $c } } 75
    if (-not $count) { throw "Opening public.customers from the sidebar did not show a table tab with a row count." }
    Write-Output "sidebar opened customers: $($count.Current.Name)"
    Write-Output "SMOKE OK"
}
finally {
    if (-not $proc.HasExited) { $proc.Kill() }
    Remove-Item -Recurse -Force $root -ErrorAction SilentlyContinue
}
