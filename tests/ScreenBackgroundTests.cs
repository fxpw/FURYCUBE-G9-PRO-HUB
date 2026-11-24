using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Web.Script.Serialization;
using System.Reflection;
using Furycube.G9.Screen;
using Furycube.G9.Oled;
using Furycube.G9.Telemetry;

internal static class ScreenBackgroundTests {
    static int checks;
    static readonly JavaScriptSerializer Json=new JavaScriptSerializer();
    static void Check(bool value,string name){if(!value)throw new Exception("FAIL "+name);checks++;}
    static string NumericProject() {var value=new ScreenProject();value.Widgets.Clear();value.Widgets.Add(new ScreenWidget {Kind=WidgetKind.Dpi});return Json.Serialize(value);}
    static string TextProject(string text){var value=new ScreenProject();value.Widgets.Clear();value.Widgets.Add(new ScreenWidget{Kind=WidgetKind.Text,Text=text});return Json.Serialize(value);}
    static bool Same(byte[] a,byte[] b){if(a==null||b==null||a.Length!=b.Length)return false;for(int i=0;i<a.Length;i++)if(a[i]!=b[i])return false;return true;}
    static async Task Run(string directory) {
        int statuses=0,frames=0;var delayedStatus=new TaskCompletionSource<OledRuntimeStatus>();
        Func<Task<OledRuntimeStatus>> status=delegate {statuses++;return statuses==1?delayedStatus.Task:Task.FromResult(new OledRuntimeStatus(1,1,2,1600,DateTimeOffset.UtcNow));};
        var oldSend=new TaskCompletionSource<string>();
        Func<byte[],Task<string>> send=delegate(byte[] bytes){frames++;Check(bytes.Length==480,"rendered frame length");return frames==1?oldSend.Task:Task.FromResult("new device sent");};
        using(var editor=new G9ScreenEditor(delegate{},send,status,Path.Combine(directory,"editor.json"),false)) {
            editor.AcceptProjectJson(NumericProject());
            Check(statuses==0&&frames==0&&!editor.IsLive,"construction and preview do not start live or query HID");
            Check(editor.PreviewPngDataUrl().StartsWith("data:image/png;base64,"),"preview available without transport");
            var telemetryField=typeof(G9ScreenEditor).GetField("telemetry",BindingFlags.Instance|BindingFlags.NonPublic);
            var initialTelemetry=(G9TelemetryReader)telemetryField.GetValue(editor);
            editor.SuspendDevice("scan");
            var nextTelemetry=(G9TelemetryReader)telemetryField.GetValue(editor);
            Check(!Object.ReferenceEquals(initialTelemetry,nextTelemetry)&&(bool)typeof(G9TelemetryReader).GetField("disposed",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(initialTelemetry),"suspend closes old passive reader and resets telemetry cache");
            await editor.ResumeLive();await editor.UploadCurrent();
            Check(statuses==0&&frames==0&&!editor.IsLive,"disconnected/startup scan blocks resume and upload");
            editor.AllowDevice();Task first=editor.ResumeLive();
            Check(!(bool)typeof(G9TelemetryReader).GetField("started",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(nextTelemetry),"offline test allow does not start passive HID services");
            Check(statuses==1&&editor.IsLive,"explicit resume starts exactly one status operation");
            Check(editor.SuspendDevice("device changed"),"transition remembers prior live state");
            delayedStatus.SetResult(new OledRuntimeStatus(1,1,2,800,DateTimeOffset.UtcNow));await first;
            Check(frames==0&&!editor.IsLive&&Json.Serialize(editor.BackgroundState()).Contains("device changed"),"late old status cannot send or overwrite disconnected state");
            editor.AllowDevice();Task second=editor.ResumeLive();Check(frames==1,"fresh target reaches send");
            editor.SuspendDevice("second transition");editor.AllowDevice();await editor.ResumeLive();
            Check(frames==2&&editor.IsLive,"new target can resume while old frame completion is pending");
            oldSend.SetException(new IOException("old device failed"));await second;
            Check(editor.IsLive&&Json.Serialize(editor.BackgroundState()).Contains("new device sent"),"late old frame error cannot stop new live session");
            editor.StopLive();Check(!editor.IsLive,"explicit stop remains stopped");
            var bad=new ScreenProject();bad.ImageFile=@"C:\not-approved.png";bool rejected=false;try{editor.AcceptProjectJson(Json.Serialize(bad));}catch(InvalidDataException){rejected=true;}
            Check(rejected,"project JSON cannot open an unapproved local image");
        }
        int attempts=0;
        using(var editor=new G9ScreenEditor(delegate{},delegate(byte[] pixels){attempts++;var task=new TaskCompletionSource<string>();task.SetException(new IOException("same device failure"));return task.Task;},null,Path.Combine(directory,"failure.json"),false)) {
            var project=new ScreenProject();project.Widgets.Clear();project.Widgets.Add(new ScreenWidget {Kind=WidgetKind.Text});editor.AcceptProjectJson(Json.Serialize(project));
            await editor.ResumeLive();await Task.Delay(40);Check(attempts==1&&!editor.IsLive,"failed same device stops without automatic retry");
        }
        var delayed=new TaskCompletionSource<OledRuntimeStatus>();int disposedFrames=0;
        var closed=new G9ScreenEditor(delegate{},delegate(byte[] bytes){disposedFrames++;return Task.FromResult("bad");},delegate{return delayed.Task;},Path.Combine(directory,"closed.json"),false);
        closed.AcceptProjectJson(NumericProject());Task pending=closed.ResumeLive();closed.Dispose();delayed.SetResult(new OledRuntimeStatus(1,1,2,1600,DateTimeOffset.UtcNow));await pending;
        Check(disposedFrames==0,"dispose cancels late status-to-frame continuation");
        int dpi=1600,radioStatusRequests=0,radioFrames=0;
        byte[] radioImage=null;
        using(var editor=new G9ScreenEditor(delegate{},delegate(byte[] pixels){
            radioFrames++;
            if(radioImage!=null)Check(!Same(radioImage,pixels),"radio renders changes in the cached DPI value");
            radioImage=(byte[])pixels.Clone();
            return Task.FromResult("radio sent");
        },delegate{radioStatusRequests++;throw new Exception("Radio rendering must not poll DPI");},Path.Combine(directory,"radio.json"),false,delegate{return dpi;})) {
            editor.AcceptProjectJson(NumericProject());
            editor.AllowDevice(true);
            await editor.ResumeLive();
            dpi=800;await editor.UploadCurrent();
            Check(radioFrames==2&&radioStatusRequests==0&&editor.IsLive,"radio live frames use cached DPI without status queries");
        }
        await RunResume(directory);
        await RunRadioResume(directory);
        await RunRadioStartupResume(directory);
        await RunPartialLive(directory);
        await RunLiveSchedule(directory);
    }
    static async Task RunLiveSchedule(string directory) {
        var now=TimeSpan.Zero;
        int frames=0,manual=0;
        bool defer=false;
        using(var editor=new G9ScreenEditor(delegate{},pixels=>{manual++;return Task.FromResult("manual");},
            savedEditorFile:Path.Combine(directory,"schedule.json"),backgroundServices:false,
            liveFrameSender:pixels=>Task.FromResult("full"),
            liveDeltaSender:pixels=>{frames++;return Task.FromResult(defer?null:"confirmed");},elapsed:()=>now)){
            editor.AllowDevice(true);
            editor.SetLiveSchedule(60,false);
            await editor.ResumeLive();
            Check(frames==1,"enabling live starts the first frame");
            now=TimeSpan.FromSeconds(59);
            await editor.CheckLiveUpdate(true);
            Check(frames==1,"idle before deadline does not start a frame");
            now=TimeSpan.FromSeconds(60);
            await editor.CheckLiveUpdate(false);
            Check(frames==2,"due frame may update during input with idle option off");
            editor.SetLiveSchedule(3600,true);
            now=TimeSpan.FromSeconds(3659);
            await editor.CheckLiveUpdate(true);
            Check(frames==2,"hourly interval uses the last confirmed update");
            now=TimeSpan.FromSeconds(3660);
            await editor.CheckLiveUpdate(false);
            Check(frames==2,"due frame waits during input with idle option on");
            await editor.UploadCurrent();
            Check(manual==1,"manual upload bypasses the background schedule");
            now+=TimeSpan.FromSeconds(3);defer=true;
            await editor.CheckLiveUpdate(true);
            Check(frames==3,"due frame starts at the first eligible idle tick");
            defer=false;now+=TimeSpan.FromSeconds(1);
            await editor.CheckLiveUpdate(true);
            Check(frames==4,"unconfirmed radio frame remains due at the next tick");
            now+=TimeSpan.FromSeconds(1);
            await editor.CheckLiveUpdate(true);
            Check(frames==4,"confirmation starts the next interval");
        }
    }
    static async Task RunPartialLive(string directory) {
        int manual=0, guard=0, partial=0;
        using(var editor=new G9ScreenEditor(delegate{},pixels=>{manual++;return Task.FromResult("manual");},
            savedEditorFile:Path.Combine(directory,"radio-delta.json"),backgroundServices:false,
            liveFrameSender:pixels=>{guard++;return Task.FromResult("guarded");},
            liveDeltaSender:pixels=>{partial++;return Task.FromResult("partial");})){
            editor.AllowDevice(true);
            await editor.UploadCurrent();
            await editor.ResumeDisplay();
            await editor.CheckRadioRestore(new TelemetrySnapshot(),true);
            Check(manual==1&&guard==1&&partial==0,"automatic static replay stays on the guarded full-frame path");
            await editor.ResumeLive();
            Check(partial==1&&editor.IsLive,"radio live widgets enter the opt-in partial path");
            editor.SuspendDevice("USB selected");
            editor.AllowDevice();
            await editor.ResumeDisplay();
            Check(manual==2&&partial==1&&editor.IsLive,"USB live widgets use the ordinary full-frame sender");
        }
    }
    static async Task RunRadioStartupResume(string directory) {
        string projectFile=Path.Combine(directory,"radio-startup-resume.json");
        var original=new byte[480];original[0]=13;
        var cache=new OledResumeCache();cache.Remember(original,false);cache.Save(projectFile+".last-frame");
        int unguarded=0,guarded=0;
        Func<byte[],Task<string>> manualSend=pixels=>{unguarded++;return Task.FromResult("USB");};
        Func<byte[],Task<string>> guardedSend=pixels=>{
            guarded++;Check(Same(original,pixels),"startup replay sends the saved static frame");
            return Task.FromResult(guarded==1?null:"radio");
        };
        var noRadioTelemetry=new TelemetrySnapshot();
        using(var editor=new G9ScreenEditor(delegate{},manualSend,null,projectFile,false,liveFrameSender:guardedSend)){
            editor.AllowDevice(true);
            await editor.ResumeDisplay();
            await editor.CheckRadioRestore(noRadioTelemetry,false);
            Check(unguarded==0&&guarded==0,"automatic radio startup waits for input to pause");
            await editor.CheckRadioRestore(noRadioTelemetry,true);
            await editor.CheckRadioRestore(noRadioTelemetry,false);
            Check(guarded==1,"desktop input interrupt defers the startup replay");
            await editor.CheckRadioRestore(noRadioTelemetry,true);
            await editor.CheckRadioRestore(noRadioTelemetry,true);
            Check(unguarded==0&&guarded==2&&!editor.IsLive,"static frame replays once through the guarded sender with no telemetry");
            await editor.ResumeDisplay();editor.StopLive();
            await editor.CheckRadioRestore(noRadioTelemetry,true);
            Check(guarded==2,"explicit stop cancels queued replay");
            await editor.ResumeDisplay();
            Check(editor.SuspendDevice("USB selected"),"pending replay remains eligible after reconnect");
            editor.AllowDevice();await editor.ResumeDisplay();
            await editor.CheckRadioRestore(noRadioTelemetry,true);
            Check(unguarded==1&&guarded==2,"pending radio replay does not survive a switch to USB");
        }
    }
    static async Task RunRadioResume(string directory) {
        var returned = DateTimeOffset.UtcNow.AddSeconds(15);
        var sample = new TelemetrySnapshot { SampledAt=returned, BatterySampledAt=returned, RadioBatteryResumedAt=returned };
        int manual = 0, guarded = 0;
        byte[] lastSent = null;
        Func<byte[],Task<string>> manualSend = pixels => {
            manual++;
            lastSent = (byte[])pixels.Clone();
            return Task.FromResult("manual");
        };
        Func<byte[],Task<string>> guardedSend = pixels => {
            Check(Same(pixels,lastSent),"radio return sends confirmed pixels, ignoring local edits");
            guarded++;
            if(guarded==3)throw new IOException("receiver write failed");
            return Task.FromResult(guarded==1?null:"guarded");
        };
        using(var editor=new G9ScreenEditor(delegate{},manualSend,null,Path.Combine(directory,"radio-resume.json"),false,liveFrameSender:guardedSend)) {
            editor.AllowDevice(true);
            editor.AcceptProjectJson(TextProject("SENT"));
            await editor.UploadCurrent();
            var settling=DateTimeOffset.UtcNow;
            await editor.CheckRadioRestore(new TelemetrySnapshot {
                SampledAt=settling,BatterySampledAt=settling,RadioBatteryResumedAt=settling
            },true);
            Check(guarded==0,"a return during OLED settling cannot trigger another transfer");
            editor.AcceptProjectJson(TextProject("UNSENT"));
            await editor.CheckRadioRestore(sample,false);
            sample.ReportedProfileId=2;
            await editor.CheckRadioRestore(sample,true);
            sample.ReportedProfileId=1;
            sample.SampledAt=returned.AddSeconds(10);
            await editor.CheckRadioRestore(sample,true);
            Check(manual==1&&guarded==0,"desktop activity, wrong profile and stale telemetry block replay");
            sample.BatterySampledAt=sample.SampledAt;
            await editor.CheckRadioRestore(sample,true);
            Check(guarded==1&&!editor.IsLive,"input-deferred replay leaves static mode intact");
            await editor.CheckRadioRestore(sample,true);
            await editor.CheckRadioRestore(sample,true);
            Check(manual==1&&guarded==2,"deferred replay retries and completes once per radio return");
            returned=returned.AddSeconds(20);
            sample.SampledAt=returned;sample.BatterySampledAt=returned;sample.RadioBatteryResumedAt=returned;
            await editor.CheckRadioRestore(sample,true);
            await editor.CheckRadioRestore(sample,true);
            Check(guarded==3,"receiver error does not retrigger every timer tick");
            editor.SuspendDevice("USB selected");editor.AllowDevice();
            await editor.CheckRadioRestore(sample,true);
            Check(guarded==3,"USB transport ignores radio return");
        }
    }
    static async Task RunResume(string directory){
        string projectFile=Path.Combine(directory,"resume.json"),cacheFile=projectFile+".last-frame";
        byte[] first=null;int sent=0,statuses=0;
        Func<byte[],Task<string>> sender=delegate(byte[] bytes){sent++;if(first==null)first=(byte[])bytes.Clone();else Check(Same(first,bytes),"static restore resends exact successful pixels, not unsent edits");return Task.FromResult("sent");};
        Func<Task<OledRuntimeStatus>> status=delegate{statuses++;throw new Exception("Static restore must not query DPI");};
        using(var editor=new G9ScreenEditor(delegate{},sender,status,projectFile,false)){
            editor.AcceptProjectJson(TextProject("SAVED"));await editor.UploadCurrent();
            Check(sent==1&&!editor.IsLive&&File.Exists(cacheFile),"manual successful upload persists a static frame");
            editor.AcceptProjectJson(NumericProject());
            Check(editor.SuspendDevice("reconnect"),"manual static upload requests reconnect restore");
            await editor.ResumeDisplay();Check(sent==1,"disconnected display cannot restore");
            editor.AllowDevice();await editor.ResumeDisplay();
            Check(sent==2&&statuses==0&&!editor.IsLive,"static reconnect does not enable live or query runtime state");
        }
        using(var editor=new G9ScreenEditor(delegate{},sender,status,projectFile,false)){
            Check(sent==2,"loading a cached frame alone never sends a frame");
            Check(!editor.SuspendDevice("startup scan"),"cache from a previous process does not claim current display ownership");
            editor.AllowDevice();await editor.ResumeDisplay();Check(sent==3&&!editor.IsLive,"explicit startup resume preserves static mode");
        }
        var bytesBefore=File.ReadAllBytes(cacheFile);
        using(var editor=new G9ScreenEditor(delegate{},delegate(byte[] bytes){var failure=new TaskCompletionSource<string>();failure.SetException(new IOException("failed transfer"));return failure.Task;},null,projectFile,false)){
            editor.AcceptProjectJson(TextProject("FAILED"));await editor.UploadCurrent();
            Check(Same(bytesBefore,File.ReadAllBytes(cacheFile)),"failed upload does not replace last successful disk frame");
        }
        string lateFile=Path.Combine(directory,"late.json");var late=new TaskCompletionSource<string>();int lateSends=0;byte[] saved=null;
        using(var editor=new G9ScreenEditor(delegate{},delegate(byte[] bytes){lateSends++;if(lateSends==1)saved=(byte[])bytes.Clone();if(lateSends==2)return late.Task;if(lateSends==3)Check(Same(saved,bytes),"late old device acknowledgement cannot replace cached frame");return Task.FromResult("ok");},null,lateFile,false)){
            editor.AcceptProjectJson(TextProject("FIRST"));await editor.UploadCurrent();
            editor.AcceptProjectJson(TextProject("LATE"));Task pending=editor.UploadCurrent();
            editor.SuspendDevice("changed");late.SetResult("old ack");await pending;
            editor.AllowDevice();await editor.ResumeDisplay();Check(lateSends==3&&!editor.IsLive,"fresh device restores prior successful frame after stale acknowledgement");
        }
        byte[] corrupted=(byte[])bytesBefore.Clone();corrupted[100]^=1;File.WriteAllBytes(cacheFile,corrupted);int corruptSends=0;
        using(var editor=new G9ScreenEditor(delegate{},delegate(byte[] bytes){corruptSends++;return Task.FromResult("ok");},null,projectFile,false)){
            await editor.ResumeDisplay();Check(corruptSends==0,"corrupt cached frame fails closed during automatic resume");
            editor.AcceptProjectJson(TextProject("RECOVER"));await editor.UploadCurrent();
            Check(corruptSends==1&&OledResumeCache.Load(cacheFile).HasFrame,"explicit upload replaces corrupt cache with a valid snapshot");
        }
        string liveFile=Path.Combine(directory,"live.json");int liveSends=0;
        using(var editor=new G9ScreenEditor(delegate{},delegate(byte[] bytes){liveSends++;return Task.FromResult("ok");},null,liveFile,false)){
            editor.AcceptProjectJson(TextProject("LIVE"));await editor.ResumeLive();
            Check(OledResumeCache.Load(liveFile+".last-frame").Live,"successful live frame persists live mode");
            editor.SuspendDevice("live reconnect");editor.AllowDevice();await editor.ResumeDisplay();
            Check(liveSends==2&&editor.IsLive,"live reconnect resumes current rendering");
            editor.StopLive();Check(!OledResumeCache.Load(liveFile+".last-frame").Live,"explicit stop persists frozen static mode");
        }
        var cache=new OledResumeCache();byte[] original=new byte[480];original[0]=1;cache.Remember(original,false);original[0]=9;
        Check(cache.GetFrame()[0]==1,"caller cannot mutate remembered frame");byte[] copy=cache.GetFrame();copy[0]=8;Check(cache.GetFrame()[0]==1,"reader cannot mutate remembered frame");
        string atomic=Path.Combine(directory,"atomic.frame");cache.Save(atomic);byte[] old=File.ReadAllBytes(atomic);cache.Remember(new byte[480],true);bool failed=false;
        using(var locked=new FileStream(atomic,FileMode.Open,FileAccess.Read,FileShare.Read)){try{cache.Save(atomic);}catch(IOException){failed=true;}}
        Check(failed&&Same(old,File.ReadAllBytes(atomic)),"failed atomic replacement keeps previous valid disk snapshot");
        Check(Directory.GetFiles(directory,"*.tmp").Length==0,"failed atomic replacement cleans only its temporary file");
    }
    [STAThread] static int Main() {
        string directory=Path.Combine(Path.GetTempPath(),"G9ScreenBackground-"+Guid.NewGuid().ToString("N"));Exception failure=null;
        try {
            Application.EnableVisualStyles();
            using(var host=new Form {ShowInTaskbar=false,Opacity=0})using(var watchdog=new Timer {Interval=15000}) {
                watchdog.Tick+=delegate {failure=new TimeoutException("screen background fixture timed out");host.Close();};
                host.Shown+=async delegate {try{await Run(directory);}catch(Exception e){failure=e;}finally{watchdog.Stop();host.Close();}};
                watchdog.Start();Application.Run(host);
            }
            if(failure!=null)throw failure;Console.WriteLine("PASS "+checks+" OLED background epoch, late completion, live and DTO checks; fake transport, telemetry disabled.");return 0;
        }catch(Exception e){Console.Error.WriteLine(e);return 1;}
        finally {if(Directory.Exists(directory)&&Path.GetFullPath(directory).StartsWith(Path.GetFullPath(Path.GetTempPath()),StringComparison.OrdinalIgnoreCase))Directory.Delete(directory,true);}
    }
}
