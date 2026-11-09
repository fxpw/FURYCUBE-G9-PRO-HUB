using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Furycube.G9.Oled;

internal static class OledWorkerTests {
    static int count;
    // Independent Python-generated CRC fixtures, shared with protocol review.
    const string ValidStatus="IoRHUwMBAgOADAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAADcn";
    const string InvalidStatus="IoRHUwMAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAOyn";
    const string BusyStatus="IoRHUwMCAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAKPc";
    static void Check(bool value,string message){if(!value)throw new Exception("FAIL "+message);count++;}
    static void Reject(Action action,string message){try{action();}catch(Exception){count++;return;}throw new Exception("FAIL accepted "+message);}
    static G9OledWorker Worker(string mode,int timeout,Action<string> logger,string ackFile=null){
        var start=new ProcessStartInfo {FileName=typeof(OledWorkerTests).Assembly.Location,Arguments="--fake-helper "+mode};
        if(ackFile!=null)start.EnvironmentVariables["G9_FAKE_ACK_FILE"]=ackFile;
        return new G9OledWorker(start,logger,timeout);
    }
    static void Emit(string line){Console.WriteLine(line);Console.Out.Flush();}
    static void ChildStatus(string mode,int index){
        if(mode=="status-hang")Thread.Sleep(Timeout.Infinite);
        if(mode=="status-ack-only"){Emit("OLED_STATUS_SENT "+index);return;}
        string payload=mode=="status-changing"?(index==2?BusyStatus:index==3?InvalidStatus:ValidStatus):ValidStatus;
        bool unsupported=mode=="ol2"||(mode=="status-changing"&&index==4)||mode=="status-unsupported-no-ack";
        if(!unsupported)Emit("RUNTIME_QUERY len=8");
        if(mode=="status-bad-base64")payload="!"+payload.Substring(1);
        if(mode=="status-bad-length")payload=payload.Substring(4);
        if(mode=="status-bad-crc"){byte[] data=Convert.FromBase64String(payload);data[8]^=1;payload=Convert.ToBase64String(data);}
        string dataLine=unsupported?"OLED_STATUS_UNSUPPORTED":"OLED_STATUS_DATA "+payload;
        Emit(dataLine);
        if(mode=="status-data-twice")Emit(dataLine);
        if(mode=="status-mixed-data")Emit("OLED_STATUS_UNSUPPORTED");
        if(mode=="status-no-ack"||mode=="status-unsupported-no-ack")Thread.Sleep(Timeout.Infinite);
        if(mode=="status-gated"){
            Emit("FAKE_STATUS_DATA_SENT");string release=Environment.GetEnvironmentVariable("G9_FAKE_ACK_FILE");
            while(!File.Exists(release))Thread.Sleep(10);
        }
        if(mode=="status-frame-ack")Emit("OLED_FRAME_SENT 1");
        else Emit("OLED_STATUS_SENT "+(mode=="status-bad-ack"?7:index));
        if(mode=="status-replay")Emit(dataLine);
    }
    static int Child(string mode){
        if(mode=="unsupported"){Console.WriteLine("OLED_UNSUPPORTED stock");return 4;}
        if(mode=="silent-exit")return 0;
        Console.WriteLine("FAKE_HELPER_NO_USB");Console.WriteLine("OLED_STREAM_READY");Console.Out.Flush();
        if(mode=="stderr"){Console.Error.WriteLine("ERROR fake failure");Console.Error.Flush();Thread.Sleep(10000);return 1;}
        if(mode=="status-unsolicited"){Emit("OLED_STATUS_DATA "+ValidStatus);Thread.Sleep(Timeout.Infinite);}
        using(var input=OledStreaming.OpenUtf8Input(Console.OpenStandardInput())){
        int index=0,status=0;for(;;){string line=input.ReadLine();if(line==null)return mode=="normal"?3:0;if(line=="STOP")return 0;
            if(line=="STATUS"){ChildStatus(mode,++status);continue;}
            if(!line.StartsWith("FRAME ")||Convert.FromBase64String(line.Substring(6)).Length!=480)return 2;
            if(mode=="hang")Thread.Sleep(Timeout.Infinite);
            if(mode=="delay")Thread.Sleep(80);
            index++;Console.WriteLine("OLED_FRAME_SENT "+(mode=="bad-ack"?7:index));Console.Out.Flush();
        }
        }
    }
    static bool Dead(int pid){try{using(var p=Process.GetProcessById(pid))return p.HasExited;}catch(ArgumentException){return true;}}
    static void CheckStatus(byte[] frame){
        int diagnostics=0;
        using(var worker=Worker("normal",3000,delegate(string line){if(line=="RUNTIME_QUERY len=8")Interlocked.Increment(ref diagnostics);})){
            worker.Send(frame);OledRuntimeStatus first=worker.ReadRuntimeStatus();worker.Send(frame);OledRuntimeStatus second=worker.ReadRuntimeStatus();
            Check(first!=null&&first.IsValid&&!first.IsBusy&&first.ProfileId==2&&first.Stage==3&&first.ConfiguredDpi==3200,
                "confirmed status decodes atomic profile/stage/configured DPI fixture");
            Check(second!=null&&!Object.ReferenceEquals(first,second)&&second.ReceivedAtUtc>=first.ReceivedAtUtc,"each query publishes a newly received status");
            Check(worker.FramesSubmitted==2&&worker.StatusQueriesSubmitted==2,"frame and status acknowledgements use separate counters in one process");
            Check(diagnostics==2,"native RUNTIME_QUERY diagnostics before DATA do not alter strict status protocol");
        }
        using(var worker=Worker("ol2",3000,null)){
            Check(worker.ReadRuntimeStatus()==null,"OL2 unsupported status returns null only after its ACK");worker.Send(frame);
            Check(worker.FramesSubmitted==1&&worker.StatusQueriesSubmitted==1,"OL2 remains usable for pixel uploads after unsupported status");
        }
        using(var worker=Worker("status-changing",3000,null)){
            Check(worker.ReadRuntimeStatus().ConfiguredDpi==3200,"changing-status fixture starts with valid DPI");
            OledRuntimeStatus busy=worker.ReadRuntimeStatus();Check(busy!=null&&busy.IsBusy&&!busy.IsValid&&busy.ConfiguredDpi==null&&busy.ProfileId==null&&busy.Stage==null,"busy status never reuses previous numeric/profile fields");
            OledRuntimeStatus invalid=worker.ReadRuntimeStatus();Check(invalid!=null&&!invalid.IsBusy&&!invalid.IsValid&&invalid.ConfiguredDpi==null,"invalid status never reuses previous numeric value");
            Check(worker.ReadRuntimeStatus()==null,"unsupported status replaces previous response without caching valid data");worker.Send(frame);
            Check(worker.FramesSubmitted==1&&worker.StatusQueriesSubmitted==4,"busy/invalid/unsupported remain nonfatal for frame transfer");
        }
        using(var worker=Worker("delay",3000,null)){
            Task a=Task.Run(delegate{worker.ReadRuntimeStatus();}),b=Task.Run(delegate{worker.Send(frame);}),c=Task.Run(delegate{worker.ReadRuntimeStatus();});
            Check(Task.WaitAll(new[]{a,b,c},5000)&&worker.FramesSubmitted==1&&worker.StatusQueriesSubmitted==2,"concurrent status/frame callers share the same serialized command gate");
        }
        string ackFile=Path.Combine(Path.GetTempPath(),"g9-fake-status-"+Guid.NewGuid().ToString("N")+".tmp");
        using(var dataSeen=new ManualResetEvent(false))try{
            using(var worker=Worker("status-gated",5000,delegate(string line){if(line=="FAKE_STATUS_DATA_SENT")dataSeen.Set();},ackFile)){
                Task<OledRuntimeStatus> query=Task.Run(()=>worker.ReadRuntimeStatus());
                Check(dataSeen.WaitOne(3000),"fake helper emitted complete DATA before gated acknowledgement");
                Check(!query.IsCompleted,"DATA cannot be published before matching STATUS_SENT acknowledgement");
                DateTimeOffset beforeAck=DateTimeOffset.UtcNow;File.WriteAllText(ackFile,"release");
                Check(query.Wait(3000)&&query.Result.IsValid&&query.Result.ReceivedAtUtc<=beforeAck,"matching ACK publishes data with original receipt time, not ACK time");
            }
        }finally{if(File.Exists(ackFile))File.Delete(ackFile);}
        foreach(string mode in new[]{"status-ack-only","status-data-twice","status-mixed-data","status-bad-base64","status-bad-length","status-bad-crc","status-bad-ack","status-frame-ack"}){
            int pid;using(var worker=Worker(mode,2000,null)){
                pid=worker.ProcessId;Reject(delegate{worker.ReadRuntimeStatus();},mode+" rejected");int submitted=worker.StatusQueriesSubmitted;
                Reject(delegate{worker.ReadRuntimeStatus();},mode+" cannot retry failed status session");
                Reject(delegate{worker.Send(frame);},mode+" cannot send a frame after status failure");
                Check(worker.StatusQueriesSubmitted==submitted&&worker.FramesSubmitted==0,"status failure submits no later command");
            }Check(Dead(pid),mode+" helper terminated");
        }
        using(var worker=Worker("status-unsolicited",2000,null)){
            Check(SpinWait.SpinUntil(()=>worker.IsStopped,2500),"unsolicited status stops helper without an outstanding query");
            Reject(delegate{worker.ReadRuntimeStatus();},"unsolicited data cannot satisfy future request");Check(worker.StatusQueriesSubmitted==0,"unsolicited data cannot start a query");
        }
        using(var worker=Worker("status-replay",2000,null)){
            try{worker.ReadRuntimeStatus();}catch(InvalidOperationException){}catch(IOException){}
            Check(SpinWait.SpinUntil(()=>worker.IsStopped,2500),"replayed DATA after ACK poisons the session");
            Reject(delegate{worker.ReadRuntimeStatus();},"completed status cannot replay into next query");Check(worker.StatusQueriesSubmitted==1,"replay rejection does not submit another STATUS");
        }
        foreach(string mode in new[]{"status-hang","status-no-ack","status-unsupported-no-ack"})using(var worker=Worker(mode,250,null)){
            var watch=Stopwatch.StartNew();Reject(delegate{worker.ReadRuntimeStatus();},mode+" times out");
            Check(watch.ElapsedMilliseconds<2500&&worker.IsStopped,"status timeout terminates helper within bound");
        }
        using(var dataSeen=new ManualResetEvent(false)){
            var worker=Worker("status-gated",10000,delegate(string line){if(line=="FAKE_STATUS_DATA_SENT")dataSeen.Set();},ackFile);int pid=worker.ProcessId;
            Task pending=Task.Run(delegate{try{worker.ReadRuntimeStatus();}catch(ObjectDisposedException){}catch(InvalidOperationException){}catch(IOException){}});
            try{
                Check(dataSeen.WaitOne(3000),"status cancellation fixture reached DATA without ACK");
                var watch=Stopwatch.StartNew();worker.Dispose();
                Check(pending.Wait(2500)&&watch.ElapsedMilliseconds<2500&&Dead(pid),"Dispose cancels an in-flight unacknowledged status and terminates helper");
            }finally{worker.Dispose();}
        }
    }
    static int Main(string[] args){
        if(args.Length==2&&args[0]=="--fake-helper")return Child(args[1]);
        try{
            byte[] frame=new byte[480];
            Encoding originalInput=Console.InputEncoding;
            try{
                // Process.Start itself can put this BOM in the pipe, before the
                // worker's explicitly encoded writer has written any command.
                Console.InputEncoding=new UTF8Encoding(true);
                int bomPid;
                using(var worker=Worker("normal",3000,null)){
                    bomPid=worker.ProcessId;
                    using(var child=Process.GetProcessById(bomPid)){
                        // Keep the process handle so its final status remains
                        // inspectable after the worker closes its own Process.
                        IntPtr handle=child.Handle;
                        Check(worker.ReadRuntimeStatus().ConfiguredDpi==3200,"first STATUS command accepts Framework startup UTF-8 BOM");
                        worker.Send(frame);worker.Send(frame);
                        Check(worker.FramesSubmitted==2,"UTF-8 BOM transport accepts two exact frames");
                        Check(Console.InputEncoding.CodePage==65001&&Console.InputEncoding.GetPreamble().Length==3,"worker does not change global console encoding");
                        worker.Dispose();
                        Check(child.WaitForExit(1000)&&child.ExitCode==0,"UTF-8 BOM transport terminates through the exact STOP command, not EOF or kill");
                    }
                }
                Check(Dead(bomPid),"UTF-8 BOM transport closes the helper");
            }finally{Console.InputEncoding=originalInput;}
            int normalPid;
            using(var worker=Worker("normal",3000,delegate(string text){throw new InvalidOperationException("Logger closed");})){normalPid=worker.ProcessId;worker.Send(frame);worker.Send(frame);Check(worker.FramesSubmitted==2,"two frames share one helper despite a throwing logger");}
            Check(Dead(normalPid),"Dispose terminates the normal helper");
            CheckStatus(frame);
            using(var worker=Worker("delay",3000,null)){
                Task a=Task.Run(delegate{worker.Send(frame);}),b=Task.Run(delegate{worker.Send(frame);});
                Check(Task.WaitAll(new[]{a,b},5000)&&worker.FramesSubmitted==2,"concurrent callers are serialized without skipped acknowledgements");
            }
            foreach(string mode in new[]{"unsupported","silent-exit","stderr","bad-ack"}){
                int pid;using(var worker=Worker(mode,2000,null)){pid=worker.ProcessId;Reject(delegate{worker.Send(frame);},mode+" is rejected");int submitted=worker.FramesSubmitted;Reject(delegate{worker.Send(frame);},mode+" cannot retry a failed session");Check(worker.FramesSubmitted==submitted,"failure does not submit another frame");}
                Check(Dead(pid),mode+" helper has terminated");
            }
            using(var worker=Worker("hang",250,null)){
                var watch=Stopwatch.StartNew();Reject(delegate{worker.Send(frame);},"unacknowledged frame times out");Check(watch.ElapsedMilliseconds<2500&&worker.IsStopped,"timeout kills helper within a bounded interval");
            }
            var cancel=Worker("hang",10000,null);int cancelPid=cancel.ProcessId;
            Task sending=Task.Run(delegate{try{cancel.Send(frame);}catch(ObjectDisposedException){}catch(InvalidOperationException){}catch(IOException){}});
            Check(SpinWait.SpinUntil(delegate{return cancel.FramesSubmitted>0;},2000),"cancellation fixture submitted its frame");
            var close=Stopwatch.StartNew();cancel.Dispose();Check(sending.Wait(2500)&&close.ElapsedMilliseconds<2500&&Dead(cancelPid),"Dispose cancels an in-flight send and closes its helper");cancel.Dispose();
            Console.WriteLine("PASS "+count+" OLED worker lifecycle checks using fake child processes only; no HID/device APIs called.");return 0;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
