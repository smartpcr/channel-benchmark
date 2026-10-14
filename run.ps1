<#
.SYNOPSIS
Runs the Go, C# and Rust channel benchmarks over one parameter matrix and
writes each case's median time, CPU time and heap allocation per message to
out/results.csv.

.DESCRIPTION
Each language first runs its correctness checks. The benchmarks then run
with their own libraries: Go testing, BenchmarkDotNet and Criterion. One
benchmark operation sends -Messages messages to each topic, so each case's
per-message time is the median operation time divided by Messages * Topics.
CPU time is the process's user plus kernel time. Go measures CPU and
allocation over its timed operations; Rust and C# measure them in a separate
pass of at least 3 operations and 1 second after timing each case.
#>
param(
    [int]$Messages = 100000,
    [int[]]$Payloads = @(64, 1024),
    [int[]]$Capacities = @(64),
    [int[]]$Topics = @(1, 4),
    [int[]]$Producers = @(4),
    [int[]]$Consumers = @(1, 4),
    [int[]]$Work = @(0, 1000),
    [int]$GoCount = 5
)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

function Invoke-Checked([string]$Name, [scriptblock]$Command) {
    & $Command | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "$Name failed" }
}

function Get-Median([double[]]$Values) {
    $sorted = $Values | Sort-Object
    $mid = [int][math]::Floor($sorted.Count / 2)
    if ($sorted.Count % 2) { $sorted[$mid] } else { ($sorted[$mid - 1] + $sorted[$mid]) / 2 }
}

function Get-CaseKey([hashtable]$Case) {
    '{0}/{1}/{2}/{3}/{4}/{5}' -f $Case.payload, $Case.capacity, $Case.topics, $Case.producers, $Case.consumers, $Case.work
}

# Reads a resource-pass CSV (id,ops,cpu_ns,allocated_bytes) into per-message
# CPU nanoseconds and allocated bytes, keyed by case.
function Read-Resources([string]$Path) {
    $byCase = @{}
    foreach ($line in Get-Content $Path) {
        $id, $ops, $cpuNs, $bytes = $line -split ','
        $case = ConvertFrom-Pairs $id '_'
        $total = [double]$ops * $Messages * $case.topics
        $byCase[(Get-CaseKey $case)] = @{ cpu = [double]$cpuNs / $total; alloc = [double]$bytes / $total }
    }
    $byCase
}

function New-Row([string]$Language, [hashtable]$Case, [double]$MedianNsPerOp, [hashtable]$Resources) {
    $perMessage = $MedianNsPerOp / ($Messages * $Case.topics)
    [pscustomobject]@{
        language            = $Language
        payload_bytes       = $Case.payload
        capacity            = $Case.capacity
        topics              = $Case.topics
        producers_per_topic = $Case.producers
        consumers_per_topic = $Case.consumers
        work_rounds         = $Case.work
        messages_per_topic  = $Messages
        median_ns_per_msg   = [math]::Round($perMessage, 2)
        msg_per_second      = [math]::Round(1e9 / $perMessage)
        cpu_ns_per_msg      = [math]::Round($Resources.cpu, 2)
        alloc_bytes_per_msg = [math]::Round($Resources.alloc, 3)
    }
}

function ConvertFrom-Pairs([string]$Text, [string]$Separator) {
    $case = @{}
    foreach ($pair in $Text -split $Separator) {
        $key, $value = $pair -split '='
        $case[$key.ToLowerInvariant()] = [int]$value
    }
    $case
}

$env:BENCH_MESSAGES = "$Messages"
$env:BENCH_PAYLOADS = $Payloads -join ','
$env:BENCH_CAPACITIES = $Capacities -join ','
$env:BENCH_TOPICS = $Topics -join ','
$env:BENCH_PRODUCERS = $Producers -join ','
$env:BENCH_CONSUMERS = $Consumers -join ','
$env:BENCH_WORK = $Work -join ','
New-Item -ItemType Directory -Force -Path out | Out-Null

Write-Host '== Correctness checks'
Invoke-Checked 'Go tests' { go -C go test -run . -count 1 }
Invoke-Checked 'Rust tests' { cargo test --release --manifest-path rust/Cargo.toml }
Invoke-Checked 'C# build' { dotnet build csharp/ChannelBench.csproj -c Release }
Invoke-Checked 'C# checks' { dotnet run -c Release --project csharp --no-build -- --verify }

$rows = [System.Collections.Generic.List[object]]::new()

Write-Host '== Go (testing)'
$goOut = go -C go test -run '^$' -bench BenchmarkChannel -benchtime 2s -count $GoCount
if ($LASTEXITCODE -ne 0) { $goOut | Out-Host; throw 'Go benchmarks failed' }
$goOut | Out-Host
$goRuns = @{}
foreach ($line in $goOut) {
    if ($line -match '^BenchmarkChannel/(\S+)-\d+\s+\d+\s+([\d.]+) ns/op') {
        $name = $Matches[1]
        if (-not $goRuns.ContainsKey($name)) { $goRuns[$name] = @{ ns = @(); cpu = @(); bytes = @() } }
        $goRuns[$name].ns += [double]$Matches[2]
        if ($line -match '([\d.]+) cpu-ns/msg') { $goRuns[$name].cpu += [double]$Matches[1] }
        if ($line -match '(\d+) B/op') { $goRuns[$name].bytes += [double]$Matches[1] }
    }
}
foreach ($name in $goRuns.Keys) {
    $case = ConvertFrom-Pairs $name '/'
    $resources = @{
        cpu   = Get-Median $goRuns[$name].cpu
        alloc = (Get-Median $goRuns[$name].bytes) / ($Messages * $case.topics)
    }
    $rows.Add((New-Row 'go' $case (Get-Median $goRuns[$name].ns) $resources))
}

Write-Host '== Rust (Criterion)'
$criterionDir = 'rust/target/criterion/channel'
if (Test-Path $criterionDir) { Remove-Item -Recurse -Force $criterionDir }
$env:BENCH_RESOURCES_OUT = Join-Path $PSScriptRoot 'out/rust-resources.csv'
Remove-Item $env:BENCH_RESOURCES_OUT -ErrorAction SilentlyContinue
Invoke-Checked 'Rust benchmarks' { cargo bench --manifest-path rust/Cargo.toml --bench channel }
$rustResources = Read-Resources $env:BENCH_RESOURCES_OUT
foreach ($dir in Get-ChildItem $criterionDir -Directory | Where-Object Name -ne 'report') {
    $id = (Get-Content "$($dir.FullName)/new/benchmark.json" -Raw | ConvertFrom-Json).value_str
    $median = (Get-Content "$($dir.FullName)/new/estimates.json" -Raw | ConvertFrom-Json).median.point_estimate
    $case = ConvertFrom-Pairs $id '_'
    $rows.Add((New-Row 'rust' $case $median $rustResources[(Get-CaseKey $case)]))
}

Write-Host '== C# (BenchmarkDotNet)'
$bdnDir = 'out/bdn'
if (Test-Path $bdnDir) { Remove-Item -Recurse -Force $bdnDir }
$env:BENCH_RESOURCES_OUT = Join-Path $PSScriptRoot 'out/csharp-resources.csv'
Remove-Item $env:BENCH_RESOURCES_OUT -ErrorAction SilentlyContinue
Invoke-Checked 'C# benchmarks' { dotnet run -c Release --project csharp --no-build -- --artifacts $bdnDir }
$csharpResources = Read-Resources $env:BENCH_RESOURCES_OUT
$report = Get-ChildItem "$bdnDir/results/*-brief.json" | Get-Content -Raw | ConvertFrom-Json
foreach ($b in $report.Benchmarks) {
    $case = ConvertFrom-Pairs $b.Parameters '&'
    $rows.Add((New-Row 'csharp' $case $b.Statistics.Median $csharpResources[(Get-CaseKey $case)]))
}
Remove-Item Env:BENCH_RESOURCES_OUT

$rows |
    Sort-Object payload_bytes, capacity, topics, producers_per_topic, consumers_per_topic, work_rounds, language |
    Export-Csv out/results.csv -NoTypeInformation -UseQuotes Never
Write-Host "Results: out/results.csv ($($rows.Count) rows)"
