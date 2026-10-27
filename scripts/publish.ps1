# Publishes the app (Release, win-x64, framework-dependent, ReadyToRun) to artifacts/publish and prints startup timing.
# Usage: & .\scripts\publish.ps1 [-Output artifacts/publish] [-Runs 5] [-NoMeasure]
# Timing: runs "TablePlsu.App.exe --measure-startup" -Runs times (after one warm-up run) against a throwaway data root,
# prints every run plus the median of each phase. Exit code 1 when the publish fails.
param(
    [string]$Output = "artifacts/publish",
    [int]$Runs = 5,
    [switch]$NoMeasure
)
$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$out = if ([IO.Path]::IsPathRooted($Output)) { $Output } else { Join-Path $repo $Output }
$project = Join-Path $repo "src/TablePlsu.App/TablePlsu.App.csproj"

Write-Output "Publishing to $out ..."
$sw = [Diagnostics.Stopwatch]::StartNew()
& dotnet publish $project -c Release -r win-x64 --self-contained false -p:PublishReadyToRun=true -o $out --nologo -v quiet
if ($LASTEXITCODE -ne 0) { Write-Error "dotnet publish failed ($LASTEXITCODE)"; exit 1 }
Write-Output ("Published in {0:N1} s" -f $sw.Elapsed.TotalSeconds)
if ($NoMeasure) { exit 0 }

& (Join-Path $PSScriptRoot "measure-startup.ps1") -Exe (Join-Path $out "TablePlsu.App.exe") -Runs $Runs
