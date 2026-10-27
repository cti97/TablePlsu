# Publishes the app (Release, win-x64) to artifacts/publish and prints startup timing.
# Default: self-contained with PublishReadyToRunComposite: the .NET runtime, WPF and the app are precompiled into one
# composite R2R image (TablePlsu.App.r2r.dll), so startup jits ~560 methods instead of ~1,700. Larger on disk (~205 MB
# vs ~23 MB), needs no installed runtime. -FrameworkDependent publishes the old small build (per-assembly R2R, needs the
# .NET 10 Desktop Runtime).
# Usage: & .\scripts\publish.ps1 [-Output artifacts/publish] [-Runs 7] [-NoMeasure] [-FrameworkDependent]
# Timing: runs "TablePlsu.App.exe --measure-startup" -Runs times (after one warm-up run) against a throwaway data root,
# prints every run plus the median of each phase. Exit code 1 when the publish fails.
param(
    [string]$Output = "artifacts/publish",
    [int]$Runs = 7,
    [switch]$NoMeasure,
    [switch]$FrameworkDependent
)
$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$out = if ([IO.Path]::IsPathRooted($Output)) { $Output } else { Join-Path $repo $Output }
$project = Join-Path $repo "src/TablePlsu.App/TablePlsu.App.csproj"

$mode = if ($FrameworkDependent) {
    @("--self-contained", "false", "-p:PublishReadyToRun=true")
} else {
    @("--self-contained", "true", "-p:PublishReadyToRun=true", "-p:PublishReadyToRunComposite=true")
}

Write-Output "Publishing to $out ($(if ($FrameworkDependent) { 'framework-dependent R2R' } else { 'self-contained composite R2R' })) ..."
$sw = [Diagnostics.Stopwatch]::StartNew()
# The R2R step is incremental per file: after a composite publish, a framework-dependent one would copy the composite's
# IL-only component stubs (and vice versa). Always start the R2R step clean.
$r2r = Join-Path $repo "src/TablePlsu.App/obj/Release/net10.0-windows/win-x64/R2R"
if (Test-Path $r2r) { Remove-Item -Recurse -Force $r2r }
if (Test-Path $out) { Remove-Item -Recurse -Force $out }
& dotnet publish $project -c Release -r win-x64 @mode -o $out --nologo -v quiet
if ($LASTEXITCODE -ne 0) { Write-Error "dotnet publish failed ($LASTEXITCODE)"; exit 1 }
$size = (Get-ChildItem -Recurse -File $out | Measure-Object -Sum Length).Sum
Write-Output ("Published in {0:N1} s, {1:N0} MB" -f $sw.Elapsed.TotalSeconds, ($size / 1MB))
if ($NoMeasure) { exit 0 }

& (Join-Path $PSScriptRoot "measure-startup.ps1") -Exe (Join-Path $out "TablePlsu.App.exe") -Runs $Runs
