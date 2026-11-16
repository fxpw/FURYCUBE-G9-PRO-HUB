using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Web.Script.Serialization;
using System.Reflection;
using Furycube.G9.Screen;
using Furycube.G9.Oled;

internal static class ScreenBackgroundTests {
    static int checks;
    static readonly JavaScriptSerializer Json=new JavaScriptSerializer();
    static void Check(bool value,string name){if(!value)throw new Exception("FAIL "+name);checks++;}
    static string NumericProject() {var value=new ScreenProject();value.Widgets.Clear();value.Widgets.Add(new ScreenWidget {Kind=WidgetKind.Dpi});return Json.Serialize(value);}
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
