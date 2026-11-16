using System;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;

internal static class StartupTests {
    static int checks;
    static void Check(bool value,string name) {if(!value)throw new Exception("FAIL "+name);checks++;}
    sealed class Store : IG9StartupStore {
        internal string Value;internal int Reads,Writes,Deletes;
        public string Read(){Reads++;return Value;}
        public void Write(string value){Writes++;Value=value;}
        public void Delete(){Deletes++;Value=null;}
    }
    static void Registration() {
        const string path=@"C:\Project with spaces\bin\FURYCUBE-G9-PRO-HUB.exe";
        string expected="\""+path+"\" --minimized";
        Check(G9Startup.Command(path)==expected,"entire absolute executable quoted, switch outside quotes");
        Check(G9Startup.Command(@"C:\русский путь\hub.EXE").Contains("русский путь"),"Unicode path remains literal");
        Check(G9Startup.Command(@"\\computer\share with spaces\hub.exe")=="\"\\\\computer\\share with spaces\\hub.exe\" --minimized","UNC path quoted");
        var store=new Store();Check(!G9Startup.IsEnabled(store,path)&&store.Writes==0&&store.Deletes==0,"normal startup reads registration without creating it");
        G9Startup.SetEnabled(store,path,true);Check(store.Value==expected&&store.Writes==1,"explicit enable registers exactly one command");
        G9Startup.SetEnabled(store,path,true);Check(store.Writes==1,"enable is idempotent");
        store.Value=expected.ToUpperInvariant();Check(G9Startup.IsEnabled(store,path),"Windows path comparison is case insensitive");
        store.Value="\"C:\\old location\\hub.exe\" --minimized";Check(!G9Startup.IsEnabled(store,path),"moved executable is not reported as matching registration");
        G9Startup.SetEnabled(store,path,true);Check(store.Value==expected&&store.Writes==2,"explicit enable repairs a moved registration");
        G9Startup.SetEnabled(store,path,false);Check(store.Value==null&&store.Deletes==1,"explicit disable removes own application value");
        G9Startup.SetEnabled(store,path,false);Check(store.Deletes==1,"disable missing entry has no mutation");
        foreach(string invalid in new[]{null,"","hub.exe",@"C:hub.exe",@"\hub.exe",@"C:\hub.cmd","C:\\hub.exe\" --prepare x","C:\\hub.exe\r\nanything","C:\\hub.exe\0"}) {
            int reads=store.Reads;bool failed=false;try{G9Startup.SetEnabled(store,invalid,true);}catch(ArgumentException){failed=true;}
            Check(failed&&reads==store.Reads&&store.Writes==2,"invalid registration rejected before reading/writing "+invalid);
        }
        bool minimized;
        Check(G9Startup.TryArguments(new string[0],out minimized)&&!minimized,"normal launch visible");
        Check(G9Startup.TryArguments(new[]{"--minimized"},out minimized)&&minimized,"autostart launch minimized");
        foreach(var args in new[]{new[]{"--unknown"},new[]{"--minimized","extra"},new[]{"--prepare"},null})Check(!G9Startup.TryArguments(args,out minimized),"unknown launch arguments rejected");
    }
    static G9StartupRead Read(string caps,string expectedCommand,List<string> calls) {
        return G9Startup.ReadDevice(delegate(string command) {calls.Add(command);if(command=="caps")return caps;if(command!=expectedCommand)throw new Exception("Unexpected command: "+command);return "snapshot payload";});
    }
    static void Scan() {
        const string wirePath=@"\\?\hid#vid_1d57&pid_2148&mi_02&col04#offline fixture";
        const string radioPath=@"\\?\hid#vid_1d57&pid_fa60&mi_02&col04#offline fixture";
        const string wire="CAPS VID=1d57 PID=2148 version=0108 featureBytes=262 path="+wirePath+"\r\n";
        const string radio="CAPS VID=1d57 PID=fa60 version=0108 featureBytes=262 path="+radioPath+"\n";
        const string wireCommand="settings-read --pid 2148 --expected-path \""+wirePath+"\"";
        const string radioCommand="settings-read --pid fa60 --expected-path \""+radioPath+"\"";
        var calls=new List<string>();var result=Read(wire,wireCommand,calls);
        Check(result.Wired&&result.Output=="snapshot payload"&&String.Join("|",calls)=="caps|"+wireCommand,"USB has one qualified exact-path full read and no second keymap read");
        calls.Clear();result=Read(radio,radioCommand,calls);
        Check(!result.Wired&&calls.Count==2,"RF reads settings through exact receiver path without writes");
        calls.Clear();result=Read(radio+wire,wireCommand,calls);Check(result.Wired&&calls.Count==2,"cable preferred over simultaneously present receiver");
        foreach(string caps in new[]{"","CAPS ONLY: no feature reports requested or settings written.","TARGET VID=1d57 PID=2148 featureBytes=262","CAPS VID=1d57 PID=21480 path=fake","CAPS VID=other PID=2148 path=fake"}) {
            calls.Clear();Check(Read(caps,null,calls)==null&&calls.Count==1,"disconnected or nonmatching enumeration invokes no device query");
        }
        calls.Clear();bool failed=false;
        try {G9Startup.ReadDevice(delegate(string command){calls.Add(command);if(command=="caps")return wire+radio;throw new IOException("cable disconnected");});}catch(IOException){failed=true;}
        Check(failed&&String.Join("|",calls)=="caps|"+wireCommand,"USB failure never retries or falls back to radio/full writes");
        Check(G9Startup.DeviceSuffix(G9Startup.SelectedIdentity(wire))==" --pid 2148 --expected-path \""+wirePath+"\"","worker target suffix pins quoted exact HID path");
        foreach(string invalid in new[]{"",G9Startup.SelectedIdentity(wire+wire),G9Startup.SelectedIdentity(wire).Replace(wirePath,"bad\"path"),G9Startup.SelectedIdentity(wire).Replace(wirePath,"short"),G9Startup.SelectedIdentity(wire).Replace(wirePath,wirePath+"\\")}) {
            failed=false;try{G9Startup.DeviceSuffix(invalid);}catch(InvalidDataException){failed=true;}Check(failed,"ambiguous or invalid target cannot authorize worker startup");
        }
    }
    static int Probe(string name,bool show=false) {
        using(var child=Process.Start(new ProcessStartInfo {FileName=System.Reflection.Assembly.GetExecutingAssembly().Location,Arguments=(show?"--instance-show ":"--instance-probe ")+name,UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden})) {
            if(!child.WaitForExit(5000)) {child.Kill();throw new TimeoutException("Instance probe timed out");}return child.ExitCode;
        }
    }
    static void SingleInstance() {
        string name=@"Local\G9StartupOfflineTest-"+Guid.NewGuid().ToString("N");
        using(var first=new G9HubInstance(name)) {
            Check(first.IsOwner,"first instance acquires isolated test mutex");
            Check(Probe(name)==32,"a separate process cannot become competing owner");
            Check(!first.ConsumeShowRequest(),"quiet duplicate does not request focus");
            Check(Probe(name,true)==32&&first.ConsumeShowRequest(),"manual duplicate signals owner even when no visible window exists");
            Check(!first.ConsumeShowRequest(),"show request consumed exactly once");
        }
        Check(Probe(name)==31,"closing first owner permits a future launch");
    }
    static void DeviceChanges() {
        var state=new G9DeviceChangeTracker();state.Reset("wired");
        Check(!state.Observe("wired")&&!state.Pending,"same target cannot authorize retry");
        Check(!state.Observe("radio")&&state.Pending&&state.Current=="wired","one changed enumeration is insufficient");
        Check(!state.Observe("wired")&&!state.Pending,"transient change returning to current device ignored");
        Check(!state.Observe("radio")&&state.Observe("radio")&&state.Current=="radio","two stable samples authorize exactly one transition");
        Check(!state.Observe("radio")&&!state.Pending,"repeated same receiver does not restart failed worker");
        Check(!state.Observe("")&&state.Observe("")&&state.Current=="","stable disconnect recognized");
        Check(!state.Observe("radio")&&state.Observe("radio"),"reconnect after observed disconnect is a new transition");
        const string wired="CAPS VID=1d57 PID=2148 version=0108 featureBytes=262 path=wire\n";
        const string radio="CAPS VID=1d57 PID=fa60 version=0108 featureBytes=262 path=radio\n";
        Check(G9Startup.SelectedIdentity(wired+radio)==G9Startup.SelectedIdentity(wired),"background target follows cable preference");
        Check(G9Startup.SelectedIdentity(radio+wired)==G9Startup.SelectedIdentity(wired+radio),"enumeration order does not cause transition");
        Check(G9Startup.SelectedIdentity(wired.Replace("path=wire","path=other"))!=G9Startup.SelectedIdentity(wired),"different device path causes transition");
        Check(G9Startup.SelectedIdentity(wired.Replace("version=0108","version=0109"))!=G9Startup.SelectedIdentity(wired),"firmware descriptor version affects session identity");
    }
    static void Preferences() {
        string directory=Path.Combine(Path.GetTempPath(),"G9StartupPreferences-"+Guid.NewGuid().ToString("N")),path=Path.Combine(directory,"background.json");
        try {
            var preferences=new G9BackgroundPreferences(path);
            Check(!preferences.ResumeOled&&!Directory.Exists(directory),"missing resume preference defaults off without creating directory");
            preferences.SetResumeOled(true);Check(preferences.ResumeOled,"explicit resume preference saved");
            File.WriteAllText(path,"{\"schemaVersion\":1,\"resumeOled\":true,\"future\":{\"theme\":\"dark\"}}");
            preferences.SetResumeOled(false);Check(!preferences.ResumeOled&&File.ReadAllText(path).Contains("\"future\":{\"theme\":\"dark\"}"),"unrelated preferences survive explicit update");
            string before=File.ReadAllText(path);preferences.SetResumeOled(false);Check(File.ReadAllText(path)==before,"idempotent preference save preserves bytes");
            foreach(string invalid in new[]{"{","null","{}","{\"schemaVersion\":2,\"resumeOled\":true}","{\"schemaVersion\":1,\"resumeOled\":\"true\"}",new string('x',32769)}) {
                File.WriteAllText(path,invalid);bool failed=false;try{preferences.SetResumeOled(true);}catch(InvalidDataException){failed=true;}
                Check(failed&&File.ReadAllText(path)==invalid,"invalid preference cannot be overwritten silently");
            }
            Check(Directory.GetFiles(directory,"*.tmp").Length==0,"preference writes leave no pending temporary files");
        }finally {if(Directory.Exists(directory)&&Path.GetFullPath(directory).StartsWith(Path.GetFullPath(Path.GetTempPath()),StringComparison.OrdinalIgnoreCase))Directory.Delete(directory,true);}
    }
    static int Main(string[] args) {try {
        if(args.Length==2 && (args[0]=="--instance-probe" || args[0]=="--instance-show"))using(var instance=new G9HubInstance(args[1])){if(args[0]=="--instance-show")instance.RequestShow();return instance.IsOwner?31:32;}
        Registration();Scan();SingleInstance();DeviceChanges();Preferences();Console.WriteLine("PASS "+checks+" startup arguments, read-only scan, isolated instance, background preferences and fake registration checks; no registry or HID touched.");return 0;
    }catch(Exception e){Console.Error.WriteLine(e);return 1;}}
}
