# Window.Show / first-frame comparison of published builds (interleaved A, B, A, B, ... so machine load hits all alike).
# Usage: & .\scripts\perf-windowshow.ps1 -Exe artifacts/base/TablePlsu.App.exe,artifacts/publish/TablePlsu.App.exe [-Runs 9]
# Publish each build to its own folder first (scripts/publish.ps1 -Output artifacts/<name> -NoMeasure).
# Per exe it prints medians of:
#   wall   total = ms from process start to first ContentRendered; show = Window.Show (marks "window" -> "shown");
#          hw = time to switch the first window back to hardware rendering after the first frame (new builds only)
#   cpu    UI-thread CPU ms (GetThreadTimes, 15.6 ms granularity) and process CPU ms at exit
#   jit    methods jitted (all threads) and on the UI thread (new builds only)
#   mcyc   UI-thread CPU cycles (millions, QueryThreadCycleTime) to "shown" and to "rendered" (new builds only):
#          the least load-sensitive proxy for UI-thread work.
# A fresh TABLEPLSU_ROOT is used for the whole comparison; the warm-up run per exe records its multi-core JIT profile,
# as normal use would.
param(
    [string[]]$Exe = @("artifacts/publish/TablePlsu.App.exe"),
    [int]$Runs = 9,
    [int]$Warmup = 1
)
$ErrorActionPreference = "Stop"
$exes = @($Exe | ForEach-Object { (Resolve-Path $_).Path })
$root = Join-Path ([IO.Path]::GetTempPath()) ("tableplsu-show-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force $root | Out-Null
$oldRoot = $env:TABLEPLSU_ROOT
$env:TABLEPLSU_ROOT = $root

function Invoke-Once([string]$path) {
    $psi = New-Object Diagnostics.ProcessStartInfo $path, "--measure-startup"
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $p = [Diagnostics.Process]::Start($psi)
    $task = $p.StandardOutput.ReadToEndAsync()
    if (-not $p.WaitForExit(60000)) { $p.Kill(); throw "App did not exit within 60 s" }
    return ($task.Result -split "`r?`n" | Where-Object { $_ -like "startup:*" } | Select-Object -First 1)
}

function Get-Steps([string]$segment) {
    $h = @{}
    foreach ($part in ($segment -split ",\s*")) { if ($part -match "^(\S+) (\d+)$") { $h[$Matches[1]] = [double]$Matches[2] } }
    return $h
}

function Get-Median($values) {
    $s = @($values | Where-Object { $_ -ne $null } | Sort-Object)
    if ($s.Count -eq 0) { return "-" }
    if ($s.Count % 2 -eq 1) { return $s[[int][Math]::Floor($s.Count / 2)] }
    return ($s[$s.Count / 2 - 1] + $s[$s.Count / 2]) / 2
}

try {
    foreach ($path in $exes) { for ($i = 0; $i -lt $Warmup; $i++) { Invoke-Once $path | Out-Null } }
    $results = @{}
    foreach ($path in $exes) { $results[$path] = New-Object System.Collections.Generic.List[object] }
    for ($i = 1; $i -le $Runs; $i++) {
        foreach ($path in $exes) {
            $line = Invoke-Once $path
            if (-not $line) { Write-Warning "run ${i}: no startup line from $path"; continue }
            $wall = Get-Steps ($line -replace '.*;\s*([^;]+)\)$', '$1')
            $r = [ordered]@{ total = $null; show = $null; hw = $null; cpuUi = $null; cpuProc = $null; jit = $null; uiJit = $null; mcShown = $null; mcRendered = $null }
            if ($line -match "startup: (\d+) ms") { $r.total = [double]$Matches[1] }
            if ($wall.ContainsKey("window") -and $wall.ContainsKey("shown")) { $r.show = $wall.shown - $wall.window }
            if ($wall.ContainsKey("hw") -and $wall.ContainsKey("rendered")) { $r.hw = $wall.hw - $wall.rendered }
            if ($line -match "cpu ui (\d+) ms process (\d+) ms") { $r.cpuUi = [double]$Matches[1]; $r.cpuProc = [double]$Matches[2] }
            if ($line -match "; jit (\d+) methods") { $r.jit = [double]$Matches[1] }
            if ($line -match "ui jit (\d+) methods") { $r.uiJit = [double]$Matches[1] }
            if ($line -match "ui mcycles ([^;]+);") {
                $c = Get-Steps $Matches[1]
                $r.mcShown = $c.shown; $r.mcRendered = $c.rendered
            }
            $results[$path].Add([pscustomobject]$r)
            Write-Output ("run {0} [{1}]: total {2} show {3} hw {4} cpuUi {5} mcyc {6}/{7}" -f $i, (Split-Path -Leaf (Split-Path -Parent $path)), $r.total, $r.show, $r.hw, $r.cpuUi, $r.mcShown, $r.mcRendered)
        }
    }
    foreach ($path in $exes) {
        $t = $results[$path]
        Write-Output ("median of {0} [{1}]: wall total {2} ms, show {3} ms, hw {4} ms | cpu ui {5} ms, process {6} ms | jit {7} (ui {8}) | ui mcycles to shown {9}, to rendered {10}" -f `
            $t.Count, $path, (Get-Median $t.total), (Get-Median $t.show), (Get-Median $t.hw), (Get-Median $t.cpuUi), (Get-Median $t.cpuProc),
            (Get-Median $t.jit), (Get-Median $t.uiJit), (Get-Median $t.mcShown), (Get-Median $t.mcRendered))
    }
}
finally {
    $env:TABLEPLSU_ROOT = $oldRoot
    Remove-Item -Recurse -Force $root -ErrorAction SilentlyContinue
}
