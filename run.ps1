param([int]$Messages=1000000, [int[]]$Capacities=@(1,64,1024), [int[]]$Producers=@(1,4), [int]$Repeats=3)
$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
New-Item -ItemType Directory -Force -Path out | Out-Null
& go build -o out/go-bench.exe ./go
if ($LASTEXITCODE -ne 0) {throw 'Go build failed'}
& dotnet build csharp/ChannelBench.csproj -c Release -o out/dotnet | Out-Host
if ($LASTEXITCODE -ne 0) {throw 'C# build failed'}
& cargo build --release --manifest-path rust/Cargo.toml | Out-Host
if ($LASTEXITCODE -ne 0) {throw 'Rust build failed'}
'language,messages,capacity,producers,seconds,msg_per_second,checksum' | Set-Content out/results.csv
foreach($c in $Capacities) { foreach($p in $Producers) { for($r=1;$r -le $Repeats;$r++) {
  $commands=@(
    @{exe='./out/go-bench.exe';a=@('--n',$Messages,'--capacity',$c,'--producers',$p)},
    @{exe='dotnet';a=@('./out/dotnet/ChannelBench.dll','--n',$Messages,'--capacity',$c,'--producers',$p)},
    @{exe='./rust/target/release/channel-bench.exe';a=@('--n',$Messages,'--capacity',$c,'--producers',$p)}
  )
  foreach($cmd in $commands) {
    $line=& $cmd.exe @($cmd.a)
    if ($LASTEXITCODE -ne 0) {throw "Failed: $($cmd.exe)"}
    $line | Tee-Object -FilePath out/results.csv -Append
  }
}}}
Write-Host 'Results: out/results.csv'
