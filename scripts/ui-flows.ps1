# End-to-end UI flows (every P0 flow in docs/spec.md) against the published app, with real keystrokes.
# Starts TablePlsu.App.exe on a throwaway TABLEPLSU_ROOT with one saved connection to the test Postgres, drives it with
# SendInput + UI Automation (tests/TablePlsu.UiFlows), and prints a pass/fail matrix. Needs an interactive, unlocked
# desktop; do not touch the mouse/keyboard while it runs. Creates (and drops) a scratch table and database tableplsu_test_2.
# The saved connections are the test DB and "maintdb" (same server, database postgres, which must stay empty: the sidebar
# empty-database hint flow). Flows live in tests/TablePlsu.UiFlows/FlowsList.cs (RunAll).
# Usage: scripts/ui-flows.ps1 [-Exe artifacts/publish/TablePlsu.App.exe] [-Only "flow name,flow name"] [-Publish]
param(
    [string]$Exe = "artifacts/publish/TablePlsu.App.exe",
    [string]$Only = "",
    [switch]$Publish
)
$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
if ($Publish) { & (Join-Path $PSScriptRoot "publish.ps1") -NoMeasure; if ($LASTEXITCODE -ne 0) { exit 1 } }
$exePath = if ([IO.Path]::IsPathRooted($Exe)) { $Exe } else { Join-Path $repo $Exe }
$project = Join-Path $repo "tests/TablePlsu.UiFlows/TablePlsu.UiFlows.csproj"
& dotnet build $project -v q --nologo | Out-Null
if ($LASTEXITCODE -ne 0) { Write-Error "harness build failed"; exit 1 }
$argsList = @("--exe", $exePath)
if ($Only) { $argsList += @("--only", $Only) }
& dotnet run --project $project --no-build -- @argsList
exit $LASTEXITCODE
