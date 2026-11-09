using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

internal static class OledWorkerTests {
    static int count;
    static void Check(bool value,string message){if(!value)throw new Exception("FAIL "+message);count++;}
    static void Reject(Action action,string message){try{action();}catch(Exception){count++;return;}throw new Exception("FAIL accepted "+message);}
    static G9OledWorker Worker(string mode,int timeout,Action<string> logger){return new G9OledWorker(new ProcessStartInfo {FileName=typeof(OledWorkerTests).Assembly.Location,Arguments="--fake-helper "+mode},logger,timeout);}
    static int Child(string mode){
        if(mode=="unsupported"){Console.WriteLine("OLED_UNSUPPORTED stock");return 4;}
        if(mode=="silent-exit")return 0;
        Console.WriteLine("FAKE_HELPER_NO_USB");Console.WriteLine("OLED_STREAM_READY");Console.Out.Flush();
        if(mode=="stderr"){Console.Error.WriteLine("ERROR fake failure");Console.Error.Flush();Thread.Sleep(10000);return 1;}
        using(var input=OledStreaming.OpenUtf8Input(Console.OpenStandardInput())){
        int index=0;for(;;){string line=input.ReadLine();if(line==null)return mode=="normal"?3:0;if(line=="STOP")return 0;
            if(!line.StartsWith("FRAME ")||Convert.FromBase64String(line.Substring(6)).Length!=480)return 2;
            if(mode=="hang")Thread.Sleep(Timeout.Infinite);
            if(mode=="delay")Thread.Sleep(80);
            index++;Console.WriteLine("OLED_FRAME_SENT "+(mode=="bad-ack"?7:index));Console.Out.Flush();
        }
        }
    }
    static bool Dead(int pid){try{using(var p=Process.GetProcessById(pid))return p.HasExited;}catch(ArgumentException){return true;}}
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
