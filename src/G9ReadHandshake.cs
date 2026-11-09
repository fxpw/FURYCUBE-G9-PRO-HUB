// Pure A0 readiness policy. Transport, prepare requests and payload consumption
// remain owned by the caller; this helper can only observe the A0 ACK.
using System;
using System.IO;

namespace Furycube.G9.Protocol {
    public static class G9ReadHandshake {
        public static bool DecodeReady(byte[] acknowledgement) {
            if(acknowledgement==null||acknowledgement.Length!=8||acknowledgement[0]!=0xA0||acknowledgement[1]>1)
                throw new InvalidDataException("Unexpected A0 read-state response; stop this transaction.");
            for(int i=2;i<8;i++)if(acknowledgement[i]!=0)
                throw new InvalidDataException("Unexpected A0 read-state padding; stop this transaction.");
            return acknowledgement[1]==1;
        }
        // This is a single initial observation, not a drain of a stale response.
        public static void ValidateIdle(byte[] acknowledgement) {
            if(DecodeReady(acknowledgement))throw new InvalidDataException("A previous response is pending; stop and reconnect before preparing another read.");
        }
        public static void WaitForReady(Func<byte[]> readA0,Action<int> wait,Func<long> monotonicMilliseconds,
            int timeoutMilliseconds=3000,int pollMilliseconds=100,int initialWaitMilliseconds=500) {
            if(readA0==null)throw new ArgumentNullException("readA0");
            if(wait==null)throw new ArgumentNullException("wait");
            if(monotonicMilliseconds==null)throw new ArgumentNullException("monotonicMilliseconds");
            if(timeoutMilliseconds<1||timeoutMilliseconds>10000)throw new ArgumentOutOfRangeException("timeoutMilliseconds");
            if(pollMilliseconds<10||pollMilliseconds>1000)throw new ArgumentOutOfRangeException("pollMilliseconds");
            if(initialWaitMilliseconds<0||initialWaitMilliseconds>timeoutMilliseconds)throw new ArgumentOutOfRangeException("initialWaitMilliseconds");
            long start=monotonicMilliseconds();if(start<0)throw new InvalidOperationException("The monotonic clock is negative.");
            long previous=start;int waited=0;
            // A second bound prevents a nonadvancing/faulty clock or wait delegate
            // from turning a readiness wait into an unlimited stream of GETs.
            int maximumPolls=2+(timeoutMilliseconds-initialWaitMilliseconds)/pollMilliseconds;
            if(initialWaitMilliseconds>0){wait(initialWaitMilliseconds);waited=initialWaitMilliseconds;}
            for(int attempt=0;attempt<maximumPolls;attempt++) {
                long now=monotonicMilliseconds();ValidateClock(start,previous,now);previous=now;
                long elapsed=now-start;
                if(elapsed>timeoutMilliseconds||waited>timeoutMilliseconds)throw Timeout();
                // No exception is caught: transport failures and malformed ACKs
                // abort immediately. They are never treated as not-ready.
                bool ready=DecodeReady(readA0());
                now=monotonicMilliseconds();ValidateClock(start,previous,now);previous=now;elapsed=now-start;
                if(elapsed>timeoutMilliseconds)throw Timeout();
                if(ready)return;
                long remaining=Math.Min(timeoutMilliseconds-elapsed,timeoutMilliseconds-waited);
                if(remaining<=0)throw Timeout();
                int pause=(int)Math.Min(pollMilliseconds,remaining);wait(pause);waited+=pause;
            }
            throw Timeout();
        }
        static void ValidateClock(long start,long previous,long now) {
            if(now<start||now<previous)throw new InvalidOperationException("The monotonic clock moved backwards; stop this transaction.");
        }
        static TimeoutException Timeout() {return new TimeoutException("The prepared read did not become ready within its deadline; do not resend or consume a response. Stop this session.");}
    }
}
