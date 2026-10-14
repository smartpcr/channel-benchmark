package main
import (
 "flag"
 "fmt"
 "sync"
 "time"
)
func main() {
 n:=flag.Int("n",1000000,"messages")
 cap:=flag.Int("capacity",64,"channel capacity")
 producers:=flag.Int("producers",1,"producer count")
 flag.Parse()
 if *n<=0 || *cap<0 || *producers<=0 {panic("invalid args")}
 ch:=make(chan int,*cap)
 var wg sync.WaitGroup
 var sum int64
 start:=time.Now()
 done:=make(chan struct{})
 go func(){for v:=range ch {sum+=int64(v)};close(done)}()
 for p:=0;p<*producers;p++ {wg.Add(1);go func(p int){defer wg.Done(); for i:=p;i<*n;i+=*producers {ch<-i}}(p)}
 wg.Wait();close(ch);<-done
 secs:=time.Since(start).Seconds()
 expected:=int64(*n)*int64(*n-1)/2
 if sum!=expected {panic(fmt.Sprintf("checksum mismatch %d != %d",sum,expected))}
 fmt.Printf("go,%d,%d,%d,%.6f,%.0f,%d\n",*n,*cap,*producers,secs,float64(*n)/secs,sum)
}
