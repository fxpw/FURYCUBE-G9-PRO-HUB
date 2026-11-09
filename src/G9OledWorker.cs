using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using Furycube.G9.Oled;

// A private helper keeps one verified HID session across frames. The owner starts
// it only after an explicit upload/live action. A failed worker cannot be reused.
internal sealed class G9OledWorker : IDisposable {
    readonly object state=new object(),sendGate=new object(),inputGate=new object();
    readonly AutoResetEvent changed=new AutoResetEvent(false);
    readonly Action<string> log;
    readonly Process process;
    readonly int timeoutMilliseconds;
    bool ready,disposed;
    int sent,acknowledged;
    string error;
    internal int ProcessId {get;private set;}
    internal int FramesSubmitted {get{lock(state)return sent;}}
    internal bool IsStopped {get{int code;return TryExited(out code);}}
    static ProcessStartInfo StartInfo(string root){return new ProcessStartInfo {FileName=Path.Combine(root,"bin","G9Device.exe"),Arguments="oled-stream",WorkingDirectory=root};}
    internal G9OledWorker(string root,Action<string> logger):this(StartInfo(root),logger,30000){}
    // The injectable process is used by fake-helper tests; production always uses
    // the fixed G9Device.exe/oled-stream command above.
    internal G9OledWorker(ProcessStartInfo start,Action<string> logger,int timeout){
        if(start==null)throw new ArgumentNullException("start");if(timeout<100||timeout>30000)throw new ArgumentOutOfRangeException("timeout");
        log=logger;timeoutMilliseconds=timeout;
        start.UseShellExecute=false;start.CreateNoWindow=true;start.WindowStyle=ProcessWindowStyle.Hidden;
        start.RedirectStandardInput=start.RedirectStandardOutput=start.RedirectStandardError=true;
        start.StandardOutputEncoding=start.StandardErrorEncoding=Encoding.UTF8;
        process=new Process {EnableRaisingEvents=true,StartInfo=start};
        process.OutputDataReceived+=delegate(object sender,DataReceivedEventArgs args){if(args.Data!=null)OnOutput(args.Data);};
        process.ErrorDataReceived+=delegate(object sender,DataReceivedEventArgs args){if(args.Data!=null){Fail(args.Data);SafeLog(args.Data);RequestKill();}};
        process.Exited+=delegate{lock(state){if(!disposed&&error==null)error="Вспомогательный процесс OLED завершился.";}Signal();};
        try{
            if(!process.Start())throw new IOException("Не удалось запустить вспомогательный процесс OLED.");
            ProcessId=process.Id;process.BeginOutputReadLine();process.BeginErrorReadLine();
        }catch{StopChild(false);process.Dispose();changed.Dispose();throw;}
    }
    void SafeLog(string message){try{if(log!=null)log(message);}catch(Exception){/* Closing GUI/loggers must not crash reader callbacks. */}}
    void Signal(){try{changed.Set();}catch(ObjectDisposedException){}}
    void Fail(string message){lock(state){if(!disposed&&error==null)error=message;}Signal();}
    bool TryExited(out int code){
        code=-1;try{if(!process.HasExited)return false;code=process.ExitCode;return true;}
        catch(InvalidOperationException){return true;}
    }
    void RequestKill(){try{int code;if(!TryExited(out code))process.Kill();}catch(InvalidOperationException){}catch(System.ComponentModel.Win32Exception){}}
    void OnOutput(string line){
        bool invalid=false;
        lock(state){
            if(disposed||error!=null)return;
            if(line=="OLED_STREAM_READY"){
                if(ready){error="Повторное приветствие вспомогательного процесса OLED.";invalid=true;}else ready=true;
            }else if(line.StartsWith("OLED_FRAME_SENT ",StringComparison.Ordinal)){
                int index;string value=line.Substring(16);
                if(!Int32.TryParse(value,System.Globalization.NumberStyles.None,System.Globalization.CultureInfo.InvariantCulture,out index)||!ready||index!=acknowledged+1||index>sent){error="Неверное подтверждение передачи OLED.";invalid=true;}
                else acknowledged=index;
            }
        }
        Signal();if(invalid)RequestKill();
        if(!line.StartsWith("OLED_SEND ",StringComparison.Ordinal)&&!line.StartsWith("OLED_FRAME_SENT ",StringComparison.Ordinal))SafeLog(line);
    }
    string FailureText(string problem){int code;return TryExited(out code)&&code==4?
        "Штатная прошивка не поддерживает OL2. Кадр не отправлен; требуется проверенное восстановление перед установкой расширения.":problem;}
    void WaitFor(bool handshake,int frame){
        var watch=Stopwatch.StartNew();
        for(;;){
            string problem;lock(state){if(disposed)throw new ObjectDisposedException("OLED session");problem=error;if(problem==null&&(handshake?ready:acknowledged==frame))return;}
            if(problem!=null)throw new InvalidOperationException(FailureText(problem));
            int code;if(TryExited(out code))throw new IOException(FailureText("Соединение с OLED завершилось до подтверждения передачи."));
            int remaining=timeoutMilliseconds-(int)watch.ElapsedMilliseconds;
            if(remaining<=0)throw new TimeoutException("Мышь не ответила за отведённое время. Передача остановлена.");
            changed.WaitOne(Math.Min(remaining,200));
        }
    }
    internal void Send(byte[] pixels){
        OledProtocol.ValidateFrame(pixels);byte[] snapshot=(byte[])pixels.Clone();
        lock(sendGate){
            try{
                WaitFor(true,0);int frame;
                lock(inputGate){
                    lock(state){if(disposed)throw new ObjectDisposedException("OLED session");if(error!=null)throw new InvalidOperationException(error);frame=++sent;}
                    process.StandardInput.WriteLine("FRAME "+Convert.ToBase64String(snapshot));process.StandardInput.Flush();
                }
                WaitFor(false,frame);
            }catch(Exception error){Fail(error.Message);StopChild(false);throw;}
        }
    }
    void StopChild(bool graceful){
        try{
            int code;if(TryExited(out code))return;
            if(graceful){
                try{lock(inputGate){if(!TryExited(out code)){process.StandardInput.WriteLine("STOP");process.StandardInput.Flush();process.StandardInput.Close();}}}
                catch(IOException){}catch(InvalidOperationException){}
                if(process.WaitForExit(500))return;
            }
            RequestKill();
            if(!process.WaitForExit(1000))SafeLog("Вспомогательный процесс OLED ещё завершает операцию; новые кадры заблокированы.");
        }catch(InvalidOperationException){}catch(System.ComponentModel.Win32Exception error){SafeLog("Завершение процесса OLED: "+error.Message);}
    }
    public void Dispose(){
        lock(state){if(disposed)return;disposed=true;}Signal();
        try{StopChild(true);}finally{process.Dispose();changed.Dispose();}
    }
}
