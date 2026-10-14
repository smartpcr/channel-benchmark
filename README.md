# Cross-language async channel benchmark

A reproducible bounded single-consumer / multi-producer (MPSC) throughput benchmark.

- Go: native `chan int` (Go channel operations block goroutines)
- C#: `System.Threading.Channels.Channel<T>` / async `WriteAsync` and `ReadAllAsync`
- Rust: `tokio::sync::mpsc` / async `send` and `recv`

All three send integers from `0` through `n-1` with producers partitioning the sequence, one receiver validates a checksum. Timings include producer/consumer startup, processing, and completion. Output: `language,messages,capacity,producers,seconds,msg_per_second,checksum`.

## Quick start

Requires Go, .NET SDK 8+, Rust/Cargo and (for the script) PowerShell 7+ on Windows.

```powershell
./run.ps1 -Messages 1000000 -Capacities 1,64,1024 -Producers 1,4 -Repeats 3
```

Results: `out/results.csv`. Run from a stable idle machine with Release builds; ignore initial runs for warmup and compare medians across multiple runs. These are not latency percentiles, and Go's goroutine-blocking model differs from .NET / Tokio async scheduling. Do not interpret throughput alone as a universal channel performance ranking.

## Manual runs

```bash
go run ./go --n 1000000 --capacity 64 --producers 4
dotnet run --project csharp -c Release -- --n 1000000 --capacity 64 --producers 4
cargo run --release --manifest-path rust/Cargo.toml -- --n 1000000 --capacity 64 --producers 4
```

Capacity must be >= 1 for C# and Rust. Implementation source is fully editable. No benchmark measurements are bundled.
