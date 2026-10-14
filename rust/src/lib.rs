//! Bounded channel throughput with `async-channel` on the tokio runtime.
//! Each topic has its own channel, its own producers and one consumer group
//! whose consumers compete for the topic's messages.

/// A message moved by value through the channel. `PAD` padding words make
/// the whole message `8 * (PAD + 1)` bytes.
#[derive(Clone, Copy)]
#[repr(C)]
pub struct Msg<const PAD: usize> {
    pub pad: [u64; PAD],
    pub seq: u64,
}

/// One benchmark operation.
#[derive(Clone, Copy, Debug)]
pub struct Config {
    /// Messages sent to each topic.
    pub messages: usize,
    /// Bounded capacity of each topic's channel.
    pub capacity: usize,
    pub topics: usize,
    /// Producers per topic.
    pub producers: usize,
    /// Consumers in each topic's consumer group.
    pub consumers: usize,
    /// Extra hash rounds per message after hashing the payload.
    pub work: usize,
}

/// What all consumers observed.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct Outcome {
    /// Sum of every received sequence number.
    pub seq_sum: u64,
    /// Wrapping sum of every message's processing hash.
    pub digest: u64,
}

impl Outcome {
    fn add(&mut self, other: Outcome) {
        self.seq_sum = self.seq_sum.wrapping_add(other.seq_sum);
        self.digest = self.digest.wrapping_add(other.digest);
    }
}

/// The SplitMix64 finalizer, the unit of simulated work.
#[inline]
pub fn mix(mut x: u64) -> u64 {
    x ^= x >> 30;
    x = x.wrapping_mul(0xBF58_476D_1CE4_E5B9);
    x ^= x >> 27;
    x = x.wrapping_mul(0x94D0_49BB_1331_11EB);
    x ^= x >> 31;
    x
}

/// Simulates handling one message: hashes its padding words then its
/// sequence number, then runs `work` more rounds.
#[inline]
pub fn process<const PAD: usize>(msg: &Msg<PAD>, work: usize) -> u64 {
    let mut h = 0;
    for &w in &msg.pad {
        h = mix(h ^ w);
    }
    h = mix(h ^ msg.seq);
    for _ in 0..work {
        h = mix(h);
    }
    h
}

/// The outcome a correct run of `cfg` must produce.
pub fn expected<const PAD: usize>(cfg: Config) -> Outcome {
    let mut out = Outcome::default();
    for i in 0..cfg.messages as u64 {
        let msg = Msg::<PAD> {
            pad: [0; PAD],
            seq: i,
        };
        out.add(Outcome {
            seq_sum: i,
            digest: process(&msg, cfg.work),
        });
    }
    Outcome {
        seq_sum: out.seq_sum.wrapping_mul(cfg.topics as u64),
        digest: out.digest.wrapping_mul(cfg.topics as u64),
    }
}

/// Sends sequence numbers `0..messages` to every topic and waits for each
/// topic's consumer group to process them. Producer `p` of a topic sends
/// `p`, `p + producers`, `p + 2 * producers`, and so on.
///
/// # Panics
///
/// Panics if a count is zero or the sequence checksum does not match.
pub async fn run<const PAD: usize>(cfg: Config) -> Outcome {
    let Config {
        messages,
        capacity,
        topics,
        producers,
        consumers,
        work,
    } = cfg;
    assert!(
        messages > 0 && capacity > 0 && topics > 0 && producers > 0 && consumers > 0,
        "invalid config: {cfg:?}"
    );
    let mut consumer_handles = Vec::with_capacity(topics * consumers);
    let mut producer_handles = Vec::with_capacity(topics * producers);
    for _ in 0..topics {
        let (tx, rx) = async_channel::bounded::<Msg<PAD>>(capacity);
        for _ in 0..consumers {
            let rx = rx.clone();
            consumer_handles.push(tokio::spawn(async move {
                let mut out = Outcome::default();
                while let Ok(msg) = rx.recv().await {
                    out.add(Outcome {
                        seq_sum: msg.seq,
                        digest: process(&msg, work),
                    });
                }
                out
            }));
        }
        for p in 0..producers {
            let tx = tx.clone();
            producer_handles.push(tokio::spawn(async move {
                for i in (p..messages).step_by(producers) {
                    let msg = Msg {
                        pad: [0; PAD],
                        seq: i as u64,
                    };
                    tx.send(msg).await.expect("consumers closed");
                }
            }));
        }
        // The channel closes when the last producer drops its sender.
    }
    for h in producer_handles {
        h.await.expect("producer failed");
    }
    let mut total = Outcome::default();
    for h in consumer_handles {
        total.add(h.await.expect("consumer failed"));
    }
    let expected_sum = topics as u64 * (messages as u64 * (messages as u64 - 1) / 2);
    assert_eq!(total.seq_sum, expected_sum, "checksum mismatch");
    total
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::mem::size_of;

    /// Pinned by the Go and C# tests too, so all three languages do
    /// identical work per message.
    const REFERENCE_DIGEST: u64 = 13_874_244_992_747_672_955;

    #[test]
    fn message_sizes() {
        assert_eq!(size_of::<Msg<0>>(), 8);
        assert_eq!(size_of::<Msg<7>>(), 64);
        assert_eq!(size_of::<Msg<31>>(), 256);
        assert_eq!(size_of::<Msg<127>>(), 1024);
    }

    #[test]
    fn digest_reference() {
        let cfg = Config {
            messages: 1000,
            capacity: 1,
            topics: 1,
            producers: 1,
            consumers: 1,
            work: 10,
        };
        assert_eq!(expected::<7>(cfg).digest, REFERENCE_DIGEST);
    }

    #[tokio::test(flavor = "multi_thread")]
    async fn run_matches_expected() {
        for (capacity, topics, producers, consumers, work) in [(1, 1, 1, 1, 0), (4, 3, 2, 3, 5)] {
            let cfg = Config {
                messages: 1000,
                capacity,
                topics,
                producers,
                consumers,
                work,
            };
            assert_eq!(run::<0>(cfg).await, expected::<0>(cfg));
            assert_eq!(run::<7>(cfg).await, expected::<7>(cfg));
            assert_eq!(run::<127>(cfg).await, expected::<127>(cfg));
        }
    }
}
