//go:build !windows

package channelbench

import (
	"syscall"
	"time"
)

// processCPU returns the user plus kernel CPU time this process has used.
func processCPU() time.Duration {
	var ru syscall.Rusage
	if err := syscall.Getrusage(syscall.RUSAGE_SELF, &ru); err != nil {
		panic(err)
	}
	return time.Duration(ru.Utime.Nano() + ru.Stime.Nano())
}
