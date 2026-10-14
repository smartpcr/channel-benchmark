use std::{env, time::Instant};
use tokio::sync::mpsc;

#[tokio::main(flavor = "multi_thread")]
async fn main() {
    let mut n: usize = 1_000_000;
    let mut capacity: usize = 64;
    let mut producers: usize = 1;
    let mut args = env::args().skip(1);
    while let Some(key) = args.next() {
        let value: usize = args.next().expect("missing argument value").parse().expect("invalid number");
        match key.as_str() {
            "--n" => n = value,
            "--capacity" => capacity = value,
            "--producers" => producers = value,
            _ => panic!("unknown argument: {key}"),
        }
    }
    assert!(n > 0 && capacity > 0 && producers > 0);
    let (tx, mut rx) = mpsc::channel::<usize>(capacity);
    let start = Instant::now();
    let consumer = tokio::spawn(async move {
        let mut sum: u128 = 0;
        while let Some(x) = rx.recv().await { sum += x as u128; }
        sum
    });
    let mut handles = Vec::new();
    for p in 0..producers {
        let tx = tx.clone();
        handles.push(tokio::spawn(async move {
            for i in (p..n).step_by(producers) { tx.send(i).await.expect("receiver closed"); }
        }));
    }
    drop(tx);
    for h in handles { h.await.expect("producer failed"); }
    let sum = consumer.await.expect("consumer failed");
    let expected = (n as u128) * ((n - 1) as u128) / 2;
    assert_eq!(sum, expected, "checksum mismatch");
    let secs = start.elapsed().as_secs_f64();
    println!("rust,{n},{capacity},{producers},{secs:.6},{:.0},{sum}", n as f64 / secs);
}
