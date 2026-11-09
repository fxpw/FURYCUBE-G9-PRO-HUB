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
    StreamWriter input;
    readonly int timeoutMilliseconds;
    bool ready,disposed;
    bool framePending,statusPending,statusDataReceived;
    int sent,acknowledged;
    int statusSent,statusAcknowledged;
    OledRuntimeStatus pendingStatus,completedStatus;
    string error;
    internal int ProcessId {get;private set;}
    internal int FramesSubmitted {get{lock(state)return sent;}}
    internal int StatusQueriesSubmitted {get{lock(state)return statusSent;}}
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
            // Process.StandardInput inherits Console.InputEncoding on .NET Framework.
            // Framework can emit its UTF-8 preamble during Start; the receiving
            // UTF-8 reader removes that one transport marker. All commands and
            // Base64 after it use this explicit ASCII writer, never console defaults.
            input=new StreamWriter(process.StandardInput.BaseStream,Encoding.ASCII);input.NewLine="\n";
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
            }else if(line.StartsWith("OLED_FRAME_SENT",StringComparison.Ordinal)){
                int index;string value=line.Length>=16?line.Substring(16):"";
                if(!line.StartsWith("OLED_FRAME_SENT ",StringComparison.Ordinal)||!Int32.TryParse(value,System.Globalization.NumberStyles.None,System.Globalization.CultureInfo.InvariantCulture,out index)||!ready||!framePending||statusPending||index!=acknowledged+1||index!=sent){error="Неверное подтверждение передачи OLED.";invalid=true;}
                else {acknowledged=index;framePending=false;}
            }else if(line.StartsWith("OLED_STATUS_",StringComparison.Ordinal)){
                try{
                    if(!ready||!statusPending||framePending)throw new FormatException("Незапрошенный ответ состояния OLED.");
                    if(line=="OLED_STATUS_UNSUPPORTED"){
                        if(statusDataReceived)throw new FormatException("Повторный ответ состояния OLED.");
                        pendingStatus=null;statusDataReceived=true;
                    }else if(line.StartsWith("OLED_STATUS_DATA ",StringComparison.Ordinal)){
                        if(statusDataReceived)throw new FormatException("Повторный ответ состояния OLED.");
                        string encoded=line.Substring(17);
                        if(encoded.Length!=176)throw new FormatException("Неверный размер состояния OLED.");
                        byte[] payload=Convert.FromBase64String(encoded);
                        if(payload.Length!=132||Convert.ToBase64String(payload)!=encoded)throw new FormatException("Неверная кодировка состояния OLED.");
                        pendingStatus=OledRuntimeStatusCodec.Decode(payload,DateTimeOffset.UtcNow);statusDataReceived=true;
                    }else if(line.StartsWith("OLED_STATUS_SENT ",StringComparison.Ordinal)){
                        int index;
                        if(!Int32.TryParse(line.Substring(17),System.Globalization.NumberStyles.None,System.Globalization.CultureInfo.InvariantCulture,out index)||!statusDataReceived||index!=statusAcknowledged+1||index!=statusSent)throw new FormatException("Неверное подтверждение состояния OLED.");
                        // A DATA/UNSUPPORTED line alone never completes the query.
                        completedStatus=pendingStatus;pendingStatus=null;
                        statusAcknowledged=index;statusPending=false;statusDataReceived=false;
                    }else throw new FormatException("Неизвестный ответ состояния OLED.");
                }catch(FormatException problem){error=problem.Message;pendingStatus=completedStatus=null;invalid=true;}
                catch(InvalidDataException problem){error=problem.Message;pendingStatus=completedStatus=null;invalid=true;}
            }
        }
        Signal();if(invalid)RequestKill();
        if(!line.StartsWith("OLED_SEND ",StringComparison.Ordinal)&&!line.StartsWith("OLED_FRAME_SENT",StringComparison.Ordinal)&&!line.StartsWith("OLED_STATUS_",StringComparison.Ordinal))SafeLog(line);
    }
    string FailureText(string problem){int code;return TryExited(out code)&&code==4?
        "Штатная прошивка не поддерживает OL2. Кадр не отправлен; требуется проверенное восстановление перед установкой расширения.":problem;}
    enum Completion { Ready, Frame, Status }
    void WaitFor(Completion completion,int index){
        var watch=Stopwatch.StartNew();
        for(;;){
            string problem;lock(state){if(disposed)throw new ObjectDisposedException("OLED session");problem=error;if(problem==null&&(completion==Completion.Ready?ready:completion==Completion.Frame?acknowledged==index:statusAcknowledged==index))return;}
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
                WaitFor(Completion.Ready,0);int frame;
                lock(inputGate){
                    lock(state){if(disposed)throw new ObjectDisposedException("OLED session");if(error!=null)throw new InvalidOperationException(error);frame=++sent;framePending=true;}
                    input.WriteLine("FRAME "+Convert.ToBase64String(snapshot));input.Flush();
                }
                WaitFor(Completion.Frame,frame);
            }catch(Exception error){Fail(error.Message);StopChild(false);throw;}
        }
    }
    internal OledRuntimeStatus ReadRuntimeStatus(){
        lock(sendGate){
            try{
                WaitFor(Completion.Ready,0);int query;
                lock(inputGate){
                    lock(state){
                        if(disposed)throw new ObjectDisposedException("OLED session");if(error!=null)throw new InvalidOperationException(error);
                        query=++statusSent;statusPending=true;statusDataReceived=false;pendingStatus=completedStatus=null;
                    }
                    input.WriteLine("STATUS");input.Flush();
                }
                WaitFor(Completion.Status,query);
                lock(state){if(disposed)throw new ObjectDisposedException("OLED session");if(error!=null)throw new InvalidOperationException(error);return completedStatus;}
            }catch(Exception problem){Fail(problem.Message);StopChild(false);throw;}
        }
    }
    void StopChild(bool graceful){
        try{
            int code;if(TryExited(out code))return;
            if(graceful){
                try{lock(inputGate){if(!TryExited(out code)){input.WriteLine("STOP");input.Flush();input.Close();}}}
                catch(IOException){}catch(InvalidOperationException){}
                if(process.WaitForExit(500))return;
            }
            RequestKill();
            if(!process.WaitForExit(1000))SafeLog("Вспомогательный процесс OLED ещё завершает операцию; новые кадры заблокированы.");
        }catch(InvalidOperationException){}catch(System.ComponentModel.Win32Exception error){SafeLog("Завершение процесса OLED: "+error.Message);}
    }
    public void Dispose(){
        lock(state){if(disposed)return;disposed=true;}Signal();
        try{StopChild(true);}finally{
            lock(inputGate){try{if(input!=null)input.Dispose();}catch(IOException){}catch(InvalidOperationException){}}
            process.Dispose();changed.Dispose();
        }
    }
}
