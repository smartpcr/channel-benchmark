package channelbench

import (
	"syscall"
	"time"
)

// processCPU returns the user plus kernel CPU time this process has used.
func processCPU() time.Duration {
	var creation, exit, kernel, user syscall.Filetime
	h, err := syscall.GetCurrentProcess()
	if err != nil {
		panic(err)
	}
	if err := syscall.GetProcessTimes(h, &creation, &exit, &kernel, &user); err != nil {
		panic(err)
	}
	// Filetime counts 100-nanosecond intervals.
	ticks := func(f syscall.Filetime) int64 { return int64(f.HighDateTime)<<32 | int64(f.LowDateTime) }
	return time.Duration((ticks(kernel) + ticks(user)) * 100)
}
