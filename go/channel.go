// Package channelbench measures bounded channel throughput with native Go
// channels. Each topic has its own channel, its own producers and one
// consumer group whose consumers compete for the topic's messages.
package channelbench

import (
	"fmt"
	"sync"
	"unsafe"
)

// Msg is a message copied by value through the channel. P is a uint64 array
// that pads the message to its payload size. Pad comes first because Go adds
// padding after a trailing zero-size field, which would make the 8-byte
// message 16 bytes.
type Msg[P any] struct {
	Pad P
	Seq uint64
}

// Config describes one benchmark operation.
type Config struct {
	Messages  int // messages sent to each topic
	Capacity  int // bounded capacity of each topic's channel
	Topics    int
	Producers int // producers per topic
	Consumers int // consumers in each topic's consumer group
	Work      int // extra hash rounds per message after hashing the payload
}

// Result is what all consumers observed.
type Result struct {
	SeqSum uint64 // sum of every received sequence number
	Digest uint64 // wrapping sum of every message's processing hash
}

// Mix is the SplitMix64 finalizer, the unit of simulated work.
func Mix(x uint64) uint64 {
	x ^= x >> 30
	x *= 0xBF58476D1CE4E5B9
	x ^= x >> 27
	x *= 0x94D049BB133111EB
	x ^= x >> 31
	return x
}

// Process simulates handling one message: it hashes every 8-byte word of the
// message, then runs work more rounds.
func Process(words []uint64, work int) uint64 {
	var h uint64
	for _, w := range words {
		h = Mix(h ^ w)
	}
	for i := 0; i < work; i++ {
		h = Mix(h)
	}
	return h
}

// Expected returns the Result a correct run of cfg must produce. Producers
// leave the padding zero, so a message's words are its padding then its
// sequence number.
func Expected[P any](cfg Config) Result {
	var m Msg[P]
	words := unsafe.Slice((*uint64)(unsafe.Pointer(&m)), unsafe.Sizeof(m)/8)
	var r Result
	for i := 0; i < cfg.Messages; i++ {
		m.Seq = uint64(i)
		r.SeqSum += uint64(i)
		r.Digest += Process(words, cfg.Work)
	}
	r.SeqSum *= uint64(cfg.Topics)
	r.Digest *= uint64(cfg.Topics)
	return r
}

// Run sends sequence numbers 0..Messages-1 to every topic and waits for each
// topic's consumer group to process them. Producer p of a topic sends p,
// p+Producers, p+2*Producers, and so on.
func Run[P any](cfg Config) (Result, error) {
	if cfg.Messages <= 0 || cfg.Capacity < 0 || cfg.Topics <= 0 || cfg.Producers <= 0 || cfg.Consumers <= 0 || cfg.Work < 0 {
		return Result{}, fmt.Errorf("invalid config: %+v", cfg)
	}
	results := make(chan Result, cfg.Topics*cfg.Consumers)
	var consumers, producers sync.WaitGroup
	for t := 0; t < cfg.Topics; t++ {
		ch := make(chan Msg[P], cfg.Capacity)
		for c := 0; c < cfg.Consumers; c++ {
			consumers.Add(1)
			go func() {
				defer consumers.Done()
				var r Result
				for m := range ch {
					words := unsafe.Slice((*uint64)(unsafe.Pointer(&m)), unsafe.Sizeof(m)/8)
					r.SeqSum += m.Seq
					r.Digest += Process(words, cfg.Work)
				}
				results <- r
			}()
		}
		var topic sync.WaitGroup
		for p := 0; p < cfg.Producers; p++ {
			topic.Add(1)
			go func(p int) {
				defer topic.Done()
				for i := p; i < cfg.Messages; i += cfg.Producers {
					ch <- Msg[P]{Seq: uint64(i)}
				}
			}(p)
		}
		producers.Add(1)
		go func() {
			topic.Wait()
			close(ch)
			producers.Done()
		}()
	}
	producers.Wait()
	consumers.Wait()
	close(results)
	var total Result
	for r := range results {
		total.SeqSum += r.SeqSum
		total.Digest += r.Digest
	}
	if expected := uint64(cfg.Topics) * uint64(cfg.Messages) * uint64(cfg.Messages-1) / 2; total.SeqSum != expected {
		return total, fmt.Errorf("checksum mismatch: %d != %d", total.SeqSum, expected)
	}
	return total, nil
}
