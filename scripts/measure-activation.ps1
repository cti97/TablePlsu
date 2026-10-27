# Measures resident mode: "second launch → window visible" and "second process exit" times.
# Usage: & .\scripts\measure-activation.ps1 [-Exe artifacts/publish/TablePlsu.App.exe] [-Runs 7]
# Starts the exe with --background (keepRunningInTray on, throwaway TABLEPLSU_ROOT), then launches it -Runs times with
# --measure-activation: each second process forwards its args over the instance pipe and exits; the running instance
# shows its hidden window, logs "activation: N ms from second launch to window visible" (from the second process's
# creation time to after the render pass that follows Show) to logs\startup.log and hides again. Ends with --quit.
param(
    [string]$Exe = "artifacts/publish/TablePlsu.App.exe",
    [int]$Runs = 7
)
$ErrorActionPreference = "Stop"
$path = (Resolve-Path $Exe).Path
$root = Join-Path ([IO.Path]::GetTempPath()) ("tableplsu-activation-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force (Join-Path $root "roaming") | Out-Null
Set-Content -Encoding utf8 (Join-Path $root "roaming\settings.json") '{ "general": { "keepRunningInTray": true, "reopenWorkspacesOnStartup": false } }'
$log = Join-Path $root "local\logs\startup.log"
$oldRoot = $env:TABLEPLSU_ROOT
$env:TABLEPLSU_ROOT = $root

function Get-Median([double[]]$values) {
    $s = @($values | Sort-Object); $n = $s.Count
    if ($n -eq 0) { return [double]::NaN }
    if ($n % 2 -eq 1) { return $s[[int][Math]::Floor($n / 2)] }
    return ($s[$n / 2 - 1] + $s[$n / 2]) / 2
}

function Get-ActivationLines { if (Test-Path $log) { @(Get-Content $log | Where-Object { $_ -match "activation: " }) } else { @() } }

function Invoke-Second([string]$arg) {
    $before = @(Get-ActivationLines).Count
    $p = [Diagnostics.Process]::Start((New-Object Diagnostics.ProcessStartInfo $path, $arg -Property @{ UseShellExecute = $false }))
    if (-not $p.WaitForExit(15000)) { $p.Kill(); throw "second launch did not exit" }
    $exitMs = ($p.ExitTime - $p.StartTime).TotalMilliseconds
    $deadline = [DateTime]::Now.AddSeconds(15)
    while (@(Get-ActivationLines).Count -le $before) {
        if ([DateTime]::Now -gt $deadline) { throw "no activation line logged" }
        Start-Sleep -Milliseconds 50
    }
    $line = @(Get-ActivationLines)[-1]
    $null = $line -match "activation: (\d+) ms"
    return [pscustomobject]@{ ExitMs = $exitMs; ShowMs = [double]$Matches[1] }
}

$primary = $null
try {
    $primary = [Diagnostics.Process]::Start((New-Object Diagnostics.ProcessStartInfo $path, "--background" -Property @{ UseShellExecute = $false }))
    $null = Invoke-Second "--measure-activation" # warm-up (also waits until the primary listens)
    $exits = @(); $shows = @()
    for ($i = 1; $i -le $Runs; $i++) {
        $r = Invoke-Second "--measure-activation"
        Write-Output ("run {0}: second process exited after {1:N0} ms; window visible {2:N0} ms after launch" -f $i, $r.ExitMs, $r.ShowMs)
        $exits += $r.ExitMs; $shows += $r.ShowMs
    }
    Write-Output ("median of {0} runs: second process exit {1:N0} ms; second launch to window visible {2:N0} ms" -f $Runs, (Get-Median $exits), (Get-Median $shows))
}
finally {
    $q = [Diagnostics.Process]::Start((New-Object Diagnostics.ProcessStartInfo $path, "--quit" -Property @{ UseShellExecute = $false }))
    $null = $q.WaitForExit(10000)
    if ($primary -and -not $primary.WaitForExit(10000)) { Write-Warning "primary did not quit; killing it"; $primary.Kill() }
    $env:TABLEPLSU_ROOT = $oldRoot
    Remove-Item -Recurse -Force $root -ErrorAction SilentlyContinue
}
