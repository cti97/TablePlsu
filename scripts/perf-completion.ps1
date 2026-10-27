# Autocomplete hot-path benchmark (CompletionPerfTests: 5000 tables, keystroke -> popup updated, first popup open).
#
#   scripts/perf-completion.ps1                       # this checkout, 5 runs
#   scripts/perf-completion.ps1 -Baseline C:\other    # interleaves runs of another checkout (A/B), same conditions
#
# Builds Release, runs the [Trait Category=Perf] test once per run in a fresh test host, parses its "PERF name=value"
# lines and prints the median of each metric per checkout. cpuMs is UI-thread CPU (QueryThreadCycleTime), which load on
# the machine distorts far less than wallMs; allocKB / posted / layouts are deterministic.
param(
    [string]$Repo = (Split-Path -Parent $PSScriptRoot),
    [string]$Baseline,
    [int]$Runs = 5,
    [switch]$NoBuild
)
$ErrorActionPreference = 'Stop'

$targets = [ordered]@{ after = $Repo }
if ($Baseline) { $targets = [ordered]@{ baseline = $Baseline; after = $Repo } }

function Dll($root) { Join-Path $root 'tests\TablePlsu.App.Tests\bin\Release\net10.0-windows\TablePlsu.App.Tests.dll' }

if (-not $NoBuild) {
    foreach ($t in $targets.Values) {
        dotnet build (Join-Path $t 'tests\TablePlsu.App.Tests') -c Release -v q -nologo | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "build failed: $t" }
    }
}

$results = @{}
foreach ($k in $targets.Keys) { $results[$k] = @{} }
for ($i = 1; $i -le $Runs; $i++) {
    foreach ($k in $targets.Keys) {
        $out = dotnet test (Dll $targets[$k]) --filter 'FullyQualifiedName~CompletionPerfTests' --logger 'console;verbosity=detailed' 2>&1 | Out-String
        $lines = $out -split "`n" | Where-Object { $_ -match 'PERF ' }
        if (-not $lines) { Write-Warning "run $i ${k}: no PERF output`n$out"; continue }
        foreach ($line in $lines) {
            foreach ($m in [regex]::Matches($line, '([\w\.]+)=([\d\.]+)')) {
                $name = $m.Groups[1].Value
                if (-not $results[$k].ContainsKey($name)) { $results[$k][$name] = New-Object System.Collections.Generic.List[double] }
                $results[$k][$name].Add([double]::Parse($m.Groups[2].Value, [Globalization.CultureInfo]::InvariantCulture))
            }
        }
        Write-Host "run $i $k done"
    }
}

function Median($xs) { $s = $xs | Sort-Object; $s[[int][math]::Floor($s.Count / 2)] }
$names = $results.Values | ForEach-Object { $_.Keys } | Sort-Object -Unique
$names | ForEach-Object {
    $row = [ordered]@{ metric = $_ }
    foreach ($k in $targets.Keys) { $row[$k] = if ($results[$k].ContainsKey($_)) { '{0:N2}' -f (Median $results[$k][$_]) } else { '' } }
    [pscustomobject]$row
} | Format-Table -AutoSize
