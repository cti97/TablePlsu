# Measures the result-streaming hot path: 'select * from sales.orders' (seed: 200k rows, No limit) in the real SQL editor
# view, plus the DbSession-only fetch and the row-limit cut-off cost.
# Usage: & .\scripts\perf-streaming.ps1 [-Runs 5] [-IntervalMs 50] [-Trace path.nettrace] [-NoBuild]
# Needs the test DB (scripts/start-testdb.sh); TABLEPLSU_TEST_PG defaults to the local seed DB on port 54329.
# In one process the test alternates fetch-only runs, the old per-batch UI path and the coalesced path (A/B interleaved,
# so machine load hits both alike) and prints, per mode, medians of: time to all rows displayed (and its ratio to the
# fetch-only time), UI-thread CPU, UI-thread allocations, collection notifications, dispatcher operations, RowCount
# updates and the max UI-thread stall (Input-priority heartbeat), with the slowest dispatcher operations per run.
# The CPU/allocation/count proxies are load-independent; wall times are only comparable within one run.
# -Trace records a CPU sampling profile of the measured runs (needs the dotnet-trace global tool) plus a .speedscope.json.
param(
    [int]$Runs = 5,
    [int]$IntervalMs = 0,
    [string]$Trace = "",
    [switch]$NoBuild
)
$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
if (-not $env:TABLEPLSU_TEST_PG) { $env:TABLEPLSU_TEST_PG = "Host=localhost;Port=54329;Username=postgres;Database=tableplsu_test" }
$env:TABLEPLSU_PERF_RUNS = "$Runs"
$env:TABLEPLSU_PERF_INTERVAL_MS = if ($IntervalMs -gt 0) { "$IntervalMs" } else { "" }
$env:TABLEPLSU_PERF_TRACE = if ($Trace) { [IO.Path]::GetFullPath($Trace) } else { "" }
$build = if ($NoBuild) { @("--no-build") } else { @() }
try {
    dotnet test (Join-Path $repo "tests/TablePlsu.Tests") -c Release @build --filter "FullyQualifiedName~DbSessionStreamingPerfTests" --logger "console;verbosity=detailed" |
        Select-String -Pattern "200k rows|row limit|Passed |Failed |error" | ForEach-Object { $_.Line }
    dotnet test (Join-Path $repo "tests/TablePlsu.App.Tests") -c Release @build --filter "FullyQualifiedName~StreamingPerfTests" --logger "console;verbosity=detailed" |
        Select-String -Pattern "run \d|fetch only|per-batch :|coalesced :|UIA|Passed |Failed |error" | ForEach-Object { $_.Line }
}
finally {
    Remove-Item Env:TABLEPLSU_PERF_RUNS, Env:TABLEPLSU_PERF_INTERVAL_MS, Env:TABLEPLSU_PERF_TRACE -ErrorAction SilentlyContinue
}
