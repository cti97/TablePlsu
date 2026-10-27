<#
.SYNOPSIS
  Sidebar / History / palette search hot path: types "customers" char by char on a 2000-table scratch schema and
  reports UI-thread CPU, list rebuilds, dispatcher ops, layouts, allocations, realized rows and the longest UI op.

.DESCRIPTION
  Runs tests/TablePlsu.App.Tests SidebarSearchPerfTests ([Trait Category=Perf]). With -Baseline and -After (folders
  holding a built TablePlsu.App.Tests.dll each, e.g. from
  `dotnet build tests/TablePlsu.App.Tests -c Release -o <dir>` on two commits) the runs alternate
  baseline/after -Runs times and the per-metric medians are printed side by side. Without them it runs the current
  checkout. The scratch schema is created once, kept across runs and dropped by the last run.
  Needs TABLEPLSU_TEST_PG (default: the local test DB on port 54329).

.EXAMPLE
  scripts/perf-sidebarsearch.ps1 -Baseline C:\tmp\base -After C:\tmp\after -Runs 5
#>
param(
    [string]$Baseline,
    [string]$After,
    [int]$Runs = 5,
    [int]$Rounds = 3
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (-not $env:TABLEPLSU_TEST_PG) { $env:TABLEPLSU_TEST_PG = 'Host=localhost;Port=54329;Username=postgres;Database=tableplsu_test' }
$env:TABLEPLSU_PERF_SCHEMA = 'perf_sbsearch_' + [guid]::NewGuid().ToString('N').Substring(0, 6)
$env:TABLEPLSU_PERF_ROUNDS = "$Rounds"

$targets = @()
if ($Baseline -and $After) {
    $targets = @(@{ Name = 'baseline'; Dll = Join-Path $Baseline 'TablePlsu.App.Tests.dll' }, @{ Name = 'after'; Dll = Join-Path $After 'TablePlsu.App.Tests.dll' })
} else {
    $out = Join-Path ([IO.Path]::GetTempPath()) ('tableplsu-perf-sbsearch-' + [guid]::NewGuid().ToString('N').Substring(0, 6))
    dotnet build (Join-Path $root 'tests/TablePlsu.App.Tests/TablePlsu.App.Tests.csproj') -c Release -o $out -v q -nologo | Out-Null
    $targets = @(@{ Name = 'current'; Dll = Join-Path $out 'TablePlsu.App.Tests.dll' })
}

$results = @{}
$total = $Runs * $targets.Count
$n = 0
for ($i = 0; $i -lt $Runs; $i++) {
    foreach ($t in $targets) {
        $n++
        $env:TABLEPLSU_PERF_DROP = if ($n -eq $total) { '1' } else { '0' }
        $file = [IO.Path]::GetTempFileName()
        $env:TABLEPLSU_PERF_OUT = $file
        Write-Host "run $n/$total $($t.Name)"
        dotnet test $t.Dll --filter 'FullyQualifiedName~SidebarSearchPerfTests' -nologo | Out-Null
        foreach ($line in Get-Content $file) {
            if ($line -notmatch '^PERF sidebarsearch (\S+) (.*)$') { continue }
            $case = $Matches[1]
            foreach ($kv in $Matches[2] -split ' ') {
                $k, $v = $kv -split '=', 2
                $d = 0.0
                if (-not [double]::TryParse($v, [Globalization.NumberStyles]::Float, [Globalization.CultureInfo]::InvariantCulture, [ref]$d)) { continue }
                $key = "$($t.Name)|$case|$k"
                if (-not $results.ContainsKey($key)) { $results[$key] = [Collections.Generic.List[double]]::new() }
                $results[$key].Add($d)
            }
        }
        Remove-Item $file
    }
}

function Median($xs) { $s = $xs | Sort-Object; $s[[int][math]::Floor($s.Count / 2)] }
$rows = $results.Keys | ForEach-Object { $p = $_ -split '\|'; [pscustomobject]@{ Target = $p[0]; Case = $p[1]; Metric = $p[2]; Median = Median $results[$_]; N = $results[$_].Count } }
$rows | Sort-Object Case, Metric, Target | Format-Table -AutoSize
