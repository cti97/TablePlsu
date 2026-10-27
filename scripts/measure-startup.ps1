# Measures startup of published TablePlsu.App.exe builds with --measure-startup.
# Usage: & .\scripts\measure-startup.ps1 [-Exe artifacts/publish/TablePlsu.App.exe[,other.exe]] [-Runs 5] [-Warmup 1] [-Quiet]
# Each run prints "startup: N ms to first ContentRendered (...; jit J methods M ms; assemblies A; OnStartup a, …, rendered f)".
# The script prints every run, then per exe the median total, the median of each phase (ms since process start) and the
# median JIT counts. Several -Exe values are run interleaved (A, B, A, B, …) so machine load affects them alike: use that
# for before/after comparisons. A throwaway TABLEPLSU_ROOT keeps the user's settings and workspaces out of the measurement.
param(
    [string[]]$Exe = @("artifacts/publish/TablePlsu.App.exe"),
    [int]$Runs = 5,
    [int]$Warmup = 1,
    [switch]$Quiet
)
$ErrorActionPreference = "Stop"
$exes = @($Exe | ForEach-Object { (Resolve-Path $_).Path })
$root = Join-Path ([IO.Path]::GetTempPath()) ("tableplsu-startup-" + [Guid]::NewGuid().ToString("N"))
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
    $text = $task.Result
    return ($text -split "`r?`n" | Where-Object { $_ -like "startup:*" } | Select-Object -First 1)
}

function Get-Median([double[]]$values) {
    $sorted = @($values | Sort-Object)
    $n = $sorted.Count
    if ($n -eq 0) { return [double]::NaN }
    if ($n % 2 -eq 1) { return $sorted[[int][Math]::Floor($n / 2)] }
    return ($sorted[$n / 2 - 1] + $sorted[$n / 2]) / 2
}

function Add-Sample($table, [string]$key, [double]$value) {
    if (-not $table.Contains($key)) { $table[$key] = New-Object System.Collections.Generic.List[double] }
    $table[$key].Add($value)
}

try {
    foreach ($path in $exes) { for ($i = 0; $i -lt $Warmup; $i++) { Invoke-Once $path | Out-Null } }
    $results = @{}
    foreach ($path in $exes) { $results[$path] = [ordered]@{} }
    for ($i = 1; $i -le $Runs; $i++) {
        foreach ($path in $exes) {
            $line = Invoke-Once $path
            if (-not $line) { Write-Warning "run ${i}: no startup line from $path"; continue }
            if (-not $Quiet) { Write-Output ("run {0} [{1}]: {2}" -f $i, (Split-Path -Leaf (Split-Path -Parent $path)), $line) }
            $t = $results[$path]
            if ($line -match "startup: (\d+) ms") { Add-Sample $t "total" ([double]$Matches[1]) }
            if ($line -match "jit (\d+) methods (\d+) ms") {
                Add-Sample $t "jitMethods" ([double]$Matches[1])
                Add-Sample $t "jitMs" ([double]$Matches[2])
            }
            if ($line -match "cpu ui (\d+) ms process (\d+) ms") {
                Add-Sample $t "cpuUi" ([double]$Matches[1])
                Add-Sample $t "cpuProcess" ([double]$Matches[2])
            }
            if ($line -match ";\s*([^;]+)\)$") {
                $prev = 0.0
                foreach ($part in ($Matches[1] -split ",\s*")) {
                    if ($part -match "^(\S+) (\d+)$") {
                        $v = [double]$Matches[2]
                        Add-Sample $t $Matches[1] $v
                        Add-Sample $t ("d." + $Matches[1]) ($v - $prev)
                        $prev = $v
                    }
                }
            }
        }
    }
    foreach ($path in $exes) {
        $t = $results[$path]
        if (-not $t.Contains("total")) { continue }
        $phases = ($t.Keys | Where-Object { $_ -notlike "d.*" -and $_ -notin @("total", "jitMethods", "jitMs", "cpuUi", "cpuProcess") } |
            ForEach-Object { "{0} {1:N0} (+{2:N0})" -f $_, (Get-Median $t[$_]), (Get-Median $t["d." + $_]) }) -join ", "
        $cpu = if ($t.Contains("cpuUi")) { "cpu ui {0:N0} ms process {1:N0} ms; " -f (Get-Median $t["cpuUi"]), (Get-Median $t["cpuProcess"]) } else { "" }
        Write-Output ("median of {0} runs [{1}]: {2:N0} ms to first render; jit {3:N0} methods {4:N0} ms; {5}{6}" -f `
            $t["total"].Count, $path, (Get-Median $t["total"]), (Get-Median $t["jitMethods"]), (Get-Median $t["jitMs"]), $cpu, $phases)
    }
}
finally {
    $env:TABLEPLSU_ROOT = $oldRoot
    Remove-Item -Recurse -Force $root -ErrorAction SilentlyContinue
}
