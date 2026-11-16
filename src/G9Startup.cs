using System;
using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Collections.Generic;
using System.Web.Script.Serialization;
using Microsoft.Win32;

// Reading registration never creates a key. Only an explicit UI change writes it.
internal interface IG9StartupStore {
    string Read();
    void Write(string command);
    void Delete();
}
internal sealed class G9StartupStore : IG9StartupStore {
    const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Name = "FURYCUBE G9 PRO HUB";
    public string Read() { using(var key=Registry.CurrentUser.OpenSubKey(Key,false)) return key==null?null:key.GetValue(Name,null,RegistryValueOptions.DoNotExpandEnvironmentNames) as string; }
    public void Write(string command) { using(var key=Registry.CurrentUser.CreateSubKey(Key)) key.SetValue(Name,command,RegistryValueKind.String); }
    public void Delete() { using(var key=Registry.CurrentUser.OpenSubKey(Key,true)) if(key!=null)key.DeleteValue(Name,false); }
}
internal static class G9Startup {
    internal static string Command(string executable) {
        if(String.IsNullOrWhiteSpace(executable) || executable.IndexOfAny(new[]{'"','\r','\n','\0'})>=0 || !Path.IsPathRooted(executable))
            throw new ArgumentException("Для автозапуска нужен полный путь к EXE.");
        string path=Path.GetFullPath(executable), drive=Path.GetPathRoot(path);
        if(!(executable.StartsWith(@"\\",StringComparison.Ordinal) || executable.Length>2 && executable[1]==':' && (executable[2]=='\\' || executable[2]=='/')) ||
            String.IsNullOrEmpty(drive) || !String.Equals(Path.GetExtension(path),".exe",StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Для автозапуска нужен полный путь к EXE.");
        return "\""+path+"\" --minimized";
    }
    internal static bool IsEnabled(IG9StartupStore store,string executable) { return String.Equals(store.Read(),Command(executable),StringComparison.OrdinalIgnoreCase); }
    internal static void SetEnabled(IG9StartupStore store,string executable,bool enabled) {
        string command=Command(executable), current=store.Read();
        if(enabled) {if(!String.Equals(current,command,StringComparison.OrdinalIgnoreCase))store.Write(command);}
        else if(current!=null)store.Delete();
    }
    internal static bool TryArguments(string[] args,out bool minimized) {
        minimized=args!=null && args.Length==1 && args[0]=="--minimized";
        return args!=null && (args.Length==0 || minimized);
    }
    // Enumeration requests no feature reports. PID is then pinned for the read;
    // a disappearing cable never falls back to an unqualified RF settings read.
    internal static G9StartupRead ReadDevice(Func<string,string> run) {
        return ReadDeviceFromCaps(run,run("caps"));
    }
    internal static string SelectedIdentity(string caps) {
        var wired=new List<string>();var radio=new List<string>();
        foreach(string line in caps.Split(new[]{'\r','\n'},StringSplitOptions.RemoveEmptyEntries)) {
            if(line.StartsWith("CAPS VID=1d57 PID=2148 ",StringComparison.Ordinal))wired.Add(line.ToLowerInvariant());
            if(line.StartsWith("CAPS VID=1d57 PID=fa60 ",StringComparison.Ordinal))radio.Add(line.ToLowerInvariant());
        }
        var selected=wired.Count>0?wired:radio;selected.Sort(StringComparer.Ordinal);
        return String.Join("\n",selected);
    }
    internal static G9StartupRead ReadDeviceFromCaps(Func<string,string> run,string caps) {
        string identity=SelectedIdentity(caps);
        if(identity.StartsWith("caps vid=1d57 pid=2148 ",StringComparison.Ordinal))return new G9StartupRead {Wired=true,Output=run("settings-read"+DeviceSuffix(identity))};
        if(identity.StartsWith("caps vid=1d57 pid=fa60 ",StringComparison.Ordinal))return new G9StartupRead {Wired=false,Output=run("mapping --active"+DeviceSuffix(identity))};
        return null;
    }
    internal static string DeviceSuffix(string identity) {
        if(String.IsNullOrEmpty(identity) || identity.IndexOfAny(new[]{'\r','\n','\0'})>=0)throw new InvalidDataException("Неоднозначное подключение мыши.");
        string pid=identity.StartsWith("caps vid=1d57 pid=2148 ",StringComparison.Ordinal)?"2148":identity.StartsWith("caps vid=1d57 pid=fa60 ",StringComparison.Ordinal)?"fa60":null;
        int at=identity.IndexOf(" path=",StringComparison.Ordinal);string path=at<0?null:identity.Substring(at+6);
        if(pid==null || String.IsNullOrEmpty(path) || path.Length<20 || path.Length>1024 || path.IndexOf('"')>=0 || path.EndsWith("\\",StringComparison.Ordinal))throw new InvalidDataException("Неизвестный путь интерфейса мыши.");
        return " --pid "+pid+" --expected-path \""+path+"\"";
    }
}
internal sealed class G9StartupRead {
    internal bool Wired;
    internal string Output;
}
internal sealed class G9HubInstance : IDisposable {
    readonly Mutex mutex;
    readonly EventWaitHandle show;
    internal readonly bool IsOwner;
    internal G9HubInstance() : this(@"Local\FURYCUBE-G9-PRO-HUB-"+System.Security.Principal.WindowsIdentity.GetCurrent().User.Value) {}
    internal G9HubInstance(string name) {
        mutex=new Mutex(false,name);
        show=new EventWaitHandle(false,EventResetMode.AutoReset,name+"-Show");
        try {IsOwner=mutex.WaitOne(0,false);}catch(AbandonedMutexException){IsOwner=true;}
    }
    internal void RequestShow() {show.Set();}
    internal bool ConsumeShowRequest() {return show.WaitOne(0,false);}
    public void Dispose() {show.Dispose();if(IsOwner)mutex.ReleaseMutex();mutex.Dispose();}
}
// Two equal samples after a device notification are required. An error is not
// a new identity and cannot authorize an automatic retry on the same device.
internal sealed class G9DeviceChangeTracker {
    internal string Current {get;private set;}
    string candidate;
    internal bool Pending {get {return candidate!=null;}}
    internal void Reset(string identity) {Current=identity??"";candidate=null;}
    internal bool Observe(string identity) {
        identity=identity??"";
        if(Current==identity) {candidate=null;return false;}
        if(candidate!=identity) {candidate=identity;return false;}
        Current=identity;candidate=null;return true;
    }
}
internal sealed class G9BackgroundPreferences {
    readonly string path;
    internal G9BackgroundPreferences(string file) {path=Path.GetFullPath(file);}
    Dictionary<string,object> Read() {
        if(!File.Exists(path))return new Dictionary<string,object> {{"schemaVersion",1},{"resumeOled",false}};
        if(new FileInfo(path).Length>32768)throw new InvalidDataException("Слишком большой файл фоновых настроек HUB.");
        Dictionary<string,object> data;
        try {data=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(path));}
        catch(ArgumentException error) {throw new InvalidDataException("Повреждены фоновые настройки HUB.",error);}
        object version,resume;
        if(data==null || !data.TryGetValue("schemaVersion",out version) || !(version is int) || (int)version!=1 ||
            !data.TryGetValue("resumeOled",out resume) || !(resume is bool))throw new InvalidDataException("Неизвестный формат фоновых настроек HUB.");
        return data;
    }
    internal bool ResumeOled {get {return (bool)Read()["resumeOled"];}}
    internal void SetResumeOled(bool enabled) {
        var data=Read();if((bool)data["resumeOled"]==enabled && File.Exists(path))return;
        data["resumeOled"]=enabled;
        string directory=Path.GetDirectoryName(path);Directory.CreateDirectory(directory);
        string temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try {
            File.WriteAllText(temporary,new JavaScriptSerializer().Serialize(data),new System.Text.UTF8Encoding(false));
            if(File.Exists(path))File.Replace(temporary,path,null);else File.Move(temporary,path);
        }finally {if(File.Exists(temporary))File.Delete(temporary);}
    }
}
