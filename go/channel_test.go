package channelbench

import (
	"fmt"
	"os"
	"strconv"
	"strings"
	"testing"
	"unsafe"
)

// payload holds the Run and Expected instantiations for one message size.
type payload struct {
	size     uintptr
	run      func(Config) (Result, error)
	expected func(Config) Result
}

func newPayload[P any]() payload {
	return payload{unsafe.Sizeof(Msg[P]{}), Run[P], Expected[P]}
}

// payloads maps a message size in bytes to its instantiations.
var payloads = map[int]payload{
	8:    newPayload[[0]uint64](),
	64:   newPayload[[7]uint64](),
	256:  newPayload[[31]uint64](),
	1024: newPayload[[127]uint64](),
}

func envInts(t testing.TB, name, fallback string) []int {
	raw := os.Getenv(name)
	if raw == "" {
		raw = fallback
	}
	var out []int
	for _, s := range strings.Split(raw, ",") {
		v, err := strconv.Atoi(strings.TrimSpace(s))
		if err != nil {
			t.Fatalf("%s: %v", name, err)
		}
		out = append(out, v)
	}
	return out
}

func TestMessageSizes(t *testing.T) {
	for want, p := range payloads {
		if int(p.size) != want {
			t.Errorf("message for payload %d is %d bytes", want, p.size)
		}
	}
}

func TestRunMatchesExpected(t *testing.T) {
	for size, p := range payloads {
		for _, cfg := range []Config{
			{Messages: 1000, Capacity: 1, Topics: 1, Producers: 1, Consumers: 1, Work: 0},
			{Messages: 1000, Capacity: 4, Topics: 3, Producers: 2, Consumers: 3, Work: 5},
		} {
			got, err := p.run(cfg)
			if err != nil {
				t.Fatalf("payload=%d %+v: %v", size, cfg, err)
			}
			if want := p.expected(cfg); got != want {
				t.Errorf("payload=%d %+v: got %+v, want %+v", size, cfg, got, want)
			}
		}
	}
}

// TestDigestReference pins the digest that the C# and Rust tests also
// assert, so all three languages do identical work per message.
func TestDigestReference(t *testing.T) {
	cfg := Config{Messages: 1000, Topics: 1, Work: 10}
	if got := payloads[64].expected(cfg).Digest; got != referenceDigest {
		t.Errorf("digest %d, want %d", got, referenceDigest)
	}
}

const referenceDigest uint64 = 13874244992747672955

// BenchmarkChannel runs one operation per batch of BENCH_MESSAGES messages
// per topic, for every combination of the BENCH_* lists. Besides time per
// operation it reports heap bytes allocated per operation (B/op) and process
// CPU time per message (cpu-ns/msg), both over the timed operations.
func BenchmarkChannel(b *testing.B) {
	messages := envInts(b, "BENCH_MESSAGES", "100000")[0]
	for _, size := range envInts(b, "BENCH_PAYLOADS", "64,1024") {
		p, ok := payloads[size]
		if !ok {
			b.Fatalf("unsupported payload size %d", size)
		}
		for _, capacity := range envInts(b, "BENCH_CAPACITIES", "64") {
			for _, topics := range envInts(b, "BENCH_TOPICS", "1,4") {
				for _, producers := range envInts(b, "BENCH_PRODUCERS", "4") {
					for _, consumers := range envInts(b, "BENCH_CONSUMERS", "1,4") {
						for _, work := range envInts(b, "BENCH_WORK", "0,1000") {
							cfg := Config{messages, capacity, topics, producers, consumers, work}
							name := fmt.Sprintf("payload=%d/capacity=%d/topics=%d/producers=%d/consumers=%d/work=%d",
								size, capacity, topics, producers, consumers, work)
							b.Run(name, func(b *testing.B) {
								b.ReportAllocs()
								cpu := processCPU()
								for i := 0; i < b.N; i++ {
									if _, err := p.run(cfg); err != nil {
										b.Fatal(err)
									}
								}
								cpu = processCPU() - cpu
								total := float64(messages) * float64(topics) * float64(b.N)
								b.ReportMetric(total/b.Elapsed().Seconds(), "msgs/s")
								b.ReportMetric(float64(cpu.Nanoseconds())/total, "cpu-ns/msg")
							})
						}
					}
				}
			}
		}
	}
}
