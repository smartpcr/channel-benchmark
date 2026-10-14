//! Criterion benchmarks. One iteration sends `BENCH_MESSAGES` messages to
//! each topic, for every combination of the `BENCH_*` lists.
//!
//! After Criterion times a case, a resource pass measures process CPU time
//! and heap bytes allocated over at least 3 operations and 1 second, and
//! appends them to the CSV named by `BENCH_RESOURCES_OUT`.

use std::{
    alloc::{GlobalAlloc, Layout, System},
    env,
    fs::OpenOptions,
    io::Write,
    sync::atomic::{AtomicU64, Ordering::Relaxed},
    time::{Duration, Instant},
};

use channel_bench::{run, Config, Outcome};
use cpu_time::ProcessTime;
use criterion::{
    criterion_group, criterion_main, BenchmarkId, Criterion, SamplingMode, Throughput,
};
use tokio::runtime::Runtime;

/// Counts every byte the process allocates, like Go's B/op and .NET's
/// allocated bytes. Reallocations count their new size.
struct CountingAlloc;

static ALLOCATED: AtomicU64 = AtomicU64::new(0);

unsafe impl GlobalAlloc for CountingAlloc {
    unsafe fn alloc(&self, layout: Layout) -> *mut u8 {
        ALLOCATED.fetch_add(layout.size() as u64, Relaxed);
        System.alloc(layout)
    }

    unsafe fn alloc_zeroed(&self, layout: Layout) -> *mut u8 {
        ALLOCATED.fetch_add(layout.size() as u64, Relaxed);
        System.alloc_zeroed(layout)
    }

    unsafe fn realloc(&self, ptr: *mut u8, layout: Layout, new_size: usize) -> *mut u8 {
        ALLOCATED.fetch_add(new_size as u64, Relaxed);
        System.realloc(ptr, layout, new_size)
    }

    unsafe fn dealloc(&self, ptr: *mut u8, layout: Layout) {
        System.dealloc(ptr, layout)
    }
}

#[global_allocator]
static GLOBAL: CountingAlloc = CountingAlloc;

fn env_list(name: &str, fallback: &str) -> Vec<usize> {
    env::var(name)
        .unwrap_or_else(|_| fallback.to_string())
        .split(',')
        .map(|s| s.trim().parse().unwrap_or_else(|e| panic!("{name}: {e}")))
        .collect()
}

fn run_payload(rt: &Runtime, payload: usize, cfg: Config) -> Outcome {
    match payload {
        8 => rt.block_on(run::<0>(cfg)),
        64 => rt.block_on(run::<7>(cfg)),
        256 => rt.block_on(run::<31>(cfg)),
        1024 => rt.block_on(run::<127>(cfg)),
        _ => panic!("unsupported payload size {payload}"),
    }
}

fn measure_resources(path: &str, id: &str, rt: &Runtime, payload: usize, cfg: Config) {
    let cpu = ProcessTime::now();
    let allocated = ALLOCATED.load(Relaxed);
    let clock = Instant::now();
    let mut ops = 0;
    while ops < 3 || clock.elapsed() < Duration::from_secs(1) {
        run_payload(rt, payload, cfg);
        ops += 1;
    }
    let allocated = ALLOCATED.load(Relaxed) - allocated;
    let cpu_ns = cpu.elapsed().as_nanos();
    let mut file = OpenOptions::new()
        .create(true)
        .append(true)
        .open(path)
        .expect("open BENCH_RESOURCES_OUT");
    writeln!(file, "{id},{ops},{cpu_ns},{allocated}").expect("write BENCH_RESOURCES_OUT");
}

fn channel(c: &mut Criterion) {
    let messages = env_list("BENCH_MESSAGES", "100000")[0];
    let resources_out = env::var("BENCH_RESOURCES_OUT").ok();
    let rt = Runtime::new().expect("tokio runtime");
    let mut group = c.benchmark_group("channel");
    // Each iteration is a whole batch, so use equal-size samples.
    group.sampling_mode(SamplingMode::Flat);
    group.sample_size(20);
    group.measurement_time(Duration::from_secs(3));
    for payload in env_list("BENCH_PAYLOADS", "64,1024") {
        for capacity in env_list("BENCH_CAPACITIES", "64") {
            for topics in env_list("BENCH_TOPICS", "1,4") {
                for producers in env_list("BENCH_PRODUCERS", "4") {
                    for consumers in env_list("BENCH_CONSUMERS", "1,4") {
                        for work in env_list("BENCH_WORK", "0,1000") {
                            let cfg = Config {
                                messages,
                                capacity,
                                topics,
                                producers,
                                consumers,
                                work,
                            };
                            let id = format!(
                                "payload={payload}_capacity={capacity}_topics={topics}_producers={producers}_consumers={consumers}_work={work}"
                            );
                            group.throughput(Throughput::Elements((messages * topics) as u64));
                            group.bench_function(BenchmarkId::from_parameter(&id), |b| {
                                b.iter(|| run_payload(&rt, payload, cfg))
                            });
                            if let Some(path) = &resources_out {
                                measure_resources(path, &id, &rt, payload, cfg);
                            }
                        }
                    }
                }
            }
        }
    }
    group.finish();
}

criterion_group!(benches, channel);
criterion_main!(benches);
