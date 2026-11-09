using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Furycube.G9.Settings;

internal static class MouseSettingsEditorTests {
    const string Sensor="04380101013f0000204080c040e0000003060c121f2e000002ff000000ff000000ffffff0000ffffff00ffff4000ffffff0110ab00000000";
    const string Light="050f010003a800ff00140401c20000";
    const string Keys="083b010200000300000400001100270d00003c00000500000600000100000100000100000100000100000100000100000100000a000009000000b0";
    static readonly JavaScriptSerializer Json=new JavaScriptSerializer();
    static int count;
    static void Check(bool value,string name) {if(!value)throw new Exception("FAIL "+name);count++;}
    static void Reject(Action action,string name) {try{action();}catch(ArgumentException){count++;return;}catch(FormatException){count++;return;}catch(InvalidOperationException){count++;return;}catch(InvalidDataException){count++;return;}throw new Exception("FAIL accepted "+name);}
    static byte[] Hex(string text) {return G9SettingsSnapshot.Hex(text,text.Length/2);}
    static string Text(byte[] bytes) {return BitConverter.ToString(bytes).Replace("-","");}
    static byte[] Fixture() {
        var sensor=Hex(Sensor);var light=Hex(Light);var keys=Hex(Keys);var result=new byte[120];
        result[0]=10;result[1]=120;result[2]=1;result[3]=0x44;result[4]=0xBB;
        Buffer.BlockCopy(sensor,3,result,5,47);result[52]=0xA7;result[53]=sensor[51];result[54]=sensor[50];
        Buffer.BlockCopy(light,3,result,55,8);result[63]=light[12];result[64]=light[11];
        Buffer.BlockCopy(keys,3,result,65,54);result[119]=keys[58];return result;
    }
    static Dictionary<string,object> Document(byte[] packet,bool applied) {
        var model=MouseSettingsCodec.DecodeAllData(packet,packet[2]);
        var keys=new byte[59];keys[0]=8;keys[1]=59;keys[2]=packet[2];Buffer.BlockCopy(packet,65,keys,3,54);
        int sum=0;for(int i=3;i<57;i++)sum+=keys[i];keys[57]=(byte)(sum>>8);keys[58]=(byte)sum;
        var fields=new List<object>();
        foreach(var f in MouseSettingsCodec.GetFields(model))fields.Add(new G9SettingsField {
            id=f.Id,name=f.Name,type=f.Type,value=f.Value,unit=f.Unit,min=f.Min==0&&f.Max==0?(int?)null:f.Min,max=f.Min==0&&f.Max==0?(int?)null:f.Max,step=f.Step==0?(int?)null:f.Step,canWrite=f.CanWrite,unsupportedReason=f.UnsupportedReason,
            options=f.Options.Select(o=>new G9SettingsOption {value=o.Value,name=o.Name}).ToArray()});
        foreach(string global in new[]{"active-profile","max-profiles"})fields.Add(new G9SettingsField {id=global,value=(global=="active-profile"?packet[2]:5).ToString(CultureInfo.InvariantCulture),type="integer",min=1,max=5,step=1,canWrite=true});
        return new Dictionary<string,object> {
            {"schemaVersion",1},{"source","FirmwareLogicalProfile"},{"devicePath","offline fixture"},{"productId",0x2148},
            {"profile",(int)packet[2]},{"activeProfile",(int)packet[2]},{"maxProfiles",5},{"readAtUtc","2026-11-09T06:00:00Z"},
            {"fullBodyHex",Text(model.GetBodyCopy())},{"rawReports",new Dictionary<string,string>{{"allData",Text(packet)},{"keys",Text(keys)}}},{"fields",fields.ToArray()},
            {"archivePath","offline-only"},{"verified",applied},{"changedField",applied?"debounce.ms":null},{"backupPath",applied?"offline-backup":null}};
    }
    static string Output(Dictionary<string,object> data,bool applied) {return (applied?"SETTINGS_APPLIED_JSON ":"SETTINGS_JSON ")+Json.Serialize(data)+"\r\n";}
    static G9SettingsSnapshot Snapshot(byte[] packet) {return G9SettingsSnapshot.Parse(Output(Document(packet,false),false),false);}
    static T Field<T>(object target,string name) {return (T)target.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(target);}
    static void Populate(G9MouseSettingsEditor editor,G9SettingsSnapshot snapshot) {typeof(G9MouseSettingsEditor).GetField("snapshot",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(editor,snapshot);typeof(G9MouseSettingsEditor).GetMethod("ShowSnapshot",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(editor,null);}
    static object Row(G9MouseSettingsEditor editor,string id) {foreach(object row in Field<System.Collections.IEnumerable>(editor,"rows"))if(Field<G9SettingsField>(row,"Field").id==id)return row;throw new Exception("Missing row "+id);}
    static void Draft(G9MouseSettingsEditor editor,string id,string value) {object row=Row(editor,id);row.GetType().GetMethod("SetDraft",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(row,new object[]{value});}
    static void Parsing() {
        var packet=Fixture();var parsed=Snapshot(packet);
        Check(parsed.profile==1&&parsed.Model.DpiValues[1]==1600,"full settings parser decodes original numeric DPI bytes");
        Check(parsed.fields.Any(f=>!f.canWrite&&f.value==null),"unsupported fields remain honestly unknown");
        Check(parsed.fields.First(f=>f.id=="sleep.level1Seconds").value=="600"&&parsed.fields.First(f=>f.id=="sleep.idleMinutes").value=="10","timer units follow native scaling");
        var document=Document(packet,false);var fields=(object[])document["fields"];((G9SettingsField)fields[0]).value="8";
        Reject(delegate{G9SettingsSnapshot.Parse(Output(document,false),false);},"field text cannot contradict source bytes");
        document=Document(packet,false);document["fields"]=new[]{((object[])document["fields"])[0],((object[])document["fields"])[0]};
        Reject(delegate{G9SettingsSnapshot.Parse(Output(document,false),false);},"duplicate or missing field rejected");
        document=Document(packet,false);document["fullBodyHex"]=new string('0',234);
        Reject(delegate{G9SettingsSnapshot.Parse(Output(document,false),false);},"mismatched body copy rejected");
        document=Document(packet,true);document["verified"]=false;
        Reject(delegate{G9SettingsSnapshot.Parse(Output(document,true),true);},"unverified write is not success");
        document=Document(packet,true);document["backupPath"]=null;
        Reject(delegate{G9SettingsSnapshot.Parse(Output(document,true),true);},"write without archived baseline rejected");
        Reject(delegate{G9SettingsSnapshot.Parse(Output(Document(packet,false),false)+Output(Document(packet,false),false),false);},"two snapshots rejected");
        for(int profile=2;profile<=5;profile++){packet[2]=(byte)profile;Check(Snapshot(packet).profile==profile,"settings profile "+profile+" is explicit");}
    }
    static void Preflight() {
        var before=Snapshot(Fixture());
        var colors=G9MouseSettingsEditor.PrepareEdits(before,new Dictionary<string,string>{{"dpi.color1","123456"}});
        Check(colors.Count==1&&colors[0].Key=="dpi.color1","color-only draft produces a real edit");
        var move=G9MouseSettingsEditor.PrepareEdits(before,new Dictionary<string,string>{{"dpi.value8","800"},{"dpi.enabledMask","128"},{"dpi.stage","8"}});
        Check(move.Select(p=>p.Key).SequenceEqual(new[]{"dpi.value8","dpi.enabledMask","dpi.stage","dpi.enabledMask"}),"DPI transition sets value, enables union, selects, then disables old levels");
        var model=before.Model;foreach(var pair in move)model=MouseSettingsCodec.BuildEdit(model,pair.Key,pair.Value).Updated;
        Check(model.EnabledStageMask==128&&model.SelectedStage==8&&model.DpiValues[7]==800,"DPI transition has a valid final configuration");
        Reject(delegate{G9MouseSettingsEditor.PrepareEdits(before,new Dictionary<string,string>{{"dpi.enabledMask","1"}});},"cannot silently disable current stage");
        Reject(delegate{G9MouseSettingsEditor.PrepareEdits(before,new Dictionary<string,string>{{"dpi.value1","10050"}});},"invalid high-DPI increment rejected before USB");
        Reject(delegate{G9MouseSettingsEditor.PrepareEdits(before,new Dictionary<string,string>{{"dpi.unknown","1"}});},"unknown draft is not dropped as successful empty edit");
        Reject(delegate{G9MouseSettingsEditor.PrepareEdits(before,new Dictionary<string,string>{{"sensor.motionSync","1"}});},"unproven control cannot write");
        Reject(delegate{G9MouseSettingsEditor.PrepareEdits(before,new Dictionary<string,string>{{"active-profile","2"},{"debounce.ms","10"}});},"switch and settings cannot mix");
        Reject(delegate{G9MouseSettingsEditor.PrepareEdits(before,new Dictionary<string,string>{{"sleep.level1Seconds","610"}});},"sleep scaling cannot round an invalid draft into another value");
    }
    static async Task Interface(Form host) {
        int calls=0;var packet=Fixture();bool failRead=false,failWrite=false;
        Func<string,string> run=delegate(string command) {
            calls++;
            if(command.StartsWith("settings-read",StringComparison.Ordinal)){if(failRead)throw new IOException("fixture unplugged");return Output(Document(packet,false),false);}
            if(command.StartsWith("restore \"",StringComparison.Ordinal)) {
                int end=command.IndexOf('"',9);byte[] keys=File.ReadAllBytes(command.Substring(9,end-9));
                Check(keys.Length==59&&keys[2]==1,"profile restore is explicitly retargeted to the current profile");
                Buffer.BlockCopy(keys,3,packet,65,54);packet[119]=keys[58];return "CONFIG VERIFIED";
            }
            if(!command.StartsWith("settings-set --profile 1 --field ",StringComparison.Ordinal))throw new Exception("Unexpected command "+command);
            if(failWrite)throw new IOException("fixture write failure");
            var parts=command.Split(' ');string field=parts[4],value=parts[6];
            var edit=MouseSettingsCodec.BuildEdit(MouseSettingsCodec.DecodeAllData(packet,1),field,value);
            Buffer.BlockCopy(edit.Updated.GetBodyCopy(),0,packet,3,117);
            var data=Document(packet,true);data["changedField"]=field;return Output(data,true);
        };
        using(var editor=new G9MouseSettingsEditor(run,delegate{})) {
            try {
            host.Controls.Add(editor);Populate(editor,Snapshot(packet));
            Check(calls==0&&editor.DraftChanges().Count==0&&!Field<Button>(editor,"apply").Enabled,"opening settings does not read/write or turn disabled zero slots into drafts");
            var saved=(G9SettingsPreset)typeof(G9MouseSettingsEditor).GetMethod("CapturePreset",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(editor,null);
            typeof(G9MouseSettingsEditor).GetMethod("OpenPreset",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(editor,new object[]{saved});
            Check(saved.values["dpi.value8"]=="0"&&editor.DraftChanges().Count==0&&calls==0,"own profile export reopens with inactive zero stages without writes or clamping drafts");
            Draft(editor,"dpi.value8","800");
            typeof(G9MouseSettingsEditor).GetMethod("OpenPreset",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(editor,new object[]{saved});
            Check(editor.DraftChanges().Count==0,"opening a saved profile clears previous drafts including inactive stage edits");
            object row=Row(editor,"sleep.level1Seconds");
            Check(Field<NumericUpDown>(row,"number").Value==10m&&Field<NumericUpDown>(row,"number").Increment==0.5m,"level1 sleep displays minutes with half-minute increments");
            Draft(editor,"sleep.level1Seconds","900");Draft(editor,"dpi.color1","123456");
            Check(editor.DraftChanges()["sleep.level1Seconds"]=="900"&&editor.DraftChanges().Count==2,"display minutes retain exact canonical seconds");
            var task=(Task)typeof(G9MouseSettingsEditor).GetMethod("ApplyChanges",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(editor,null);await task;
            Check(calls==2&&editor.DraftChanges().Count==0,"explicit Apply writes only two changed parameters and verifies both");
            Check(MouseSettingsCodec.GetFields(MouseSettingsCodec.DecodeAllData(packet,1)).First(f=>f.Id=="debounce.ms").Value=="8","unrelated click filter remains eight milliseconds");
            Check(packet[52]==0xA7,"unknown alignment byte survives the edit workflow");
            var fullPreset=(G9SettingsPreset)typeof(G9MouseSettingsEditor).GetMethod("CapturePreset",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(editor,null);
            byte[] different=Hex(fullPreset.keymapHex);different[2]=3;different[14]=4;int sum=0;for(int i=3;i<57;i++)sum+=different[i];different[57]=(byte)(sum>>8);different[58]=(byte)sum;fullPreset.keymapHex=Text(different);
            typeof(G9MouseSettingsEditor).GetMethod("OpenPreset",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(editor,new object[]{fullPreset});
            Check(editor.DraftChanges().Count==0&&Field<CheckBox>(editor,"includeButtons").Checked&&Field<Button>(editor,"apply").Enabled,"button-only imported profile is visible and enables an explicit Apply");
            task=(Task)typeof(G9MouseSettingsEditor).GetMethod("ApplyChanges",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(editor,null);await task;
            Check(calls==4&&packet[76]==4&&Field<G9SettingsSnapshot>(editor,"snapshot").Keys[14]==4,"profile transfers and independently rereads the button table without changing other settings");
            failWrite=true;Draft(editor,"debounce.ms","10");Draft(editor,"sensor.ripple","0");
            task=(Task)typeof(G9MouseSettingsEditor).GetMethod("ApplyChanges",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(editor,null);await task;
            Check(calls==5&&Field<G9SettingsSnapshot>(editor,"snapshot")==null&&!Field<Button>(editor,"apply").Enabled,"first write failure stops later changes and invalidates stale UI");
            Populate(editor,Snapshot(packet));failRead=true;await editor.ReadSettings();
            Check(Field<G9SettingsSnapshot>(editor,"snapshot")==null&&!Field<Button>(editor,"apply").Enabled,"failed refresh cannot leave an old writable snapshot");
            } catch(Exception error) { Console.Error.WriteLine("UI fixture original failure (calls="+calls+", status="+Field<Label>(editor,"status").Text+"): "+error);throw; }
        }
    }
    [STAThread] static int Main(string[] args) {
        try {
            if(args.Length==2 && args[0]=="--readback") {
                var state=G9SettingsSnapshot.Parse(File.ReadAllText(args[1]),false);
                Console.WriteLine("Validated saved physical settings response: profile="+state.profile+", fields="+state.fields.Length+", debounce="+state.fields.First(f=>f.id=="debounce.ms").value+"ms. No USB commands sent by this parser.");return 0;
            }
            Application.EnableVisualStyles();Parsing();Preflight();Exception failure=null;
            using(var host=new Form {Width=1100,Height=760,ShowInTaskbar=false,Opacity=0})
            using(var watchdog=new Timer {Interval=20000}) {
                watchdog.Tick+=delegate {failure=new TimeoutException("Settings editor UI fixture did not complete.");host.Close();};
                host.Shown+=async delegate {try{await Interface(host);}catch(Exception error){failure=error;}finally{watchdog.Stop();host.Close();}};
                watchdog.Start();Application.Run(host);
            }
            if(failure!=null)throw failure;
            Console.WriteLine("PASS "+count+" settings-editor provenance, UI, transition and failure checks; fake CLI only.");return 0;
        } catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
