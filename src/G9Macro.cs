// Portable, offline macro projects. No input injection, USB, or vendor binary IO.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace Furycube.G9.Macros {
    public enum MacroStepKind {KeyDown,KeyUp,MouseDown,MouseUp,MouseMove,Wheel,Delay}
    public enum MacroMouseButton {Left,Right,Middle,Forward,Back}
    public enum MacroRepeatMode {Counted,Toggle,Hold}
    public enum MacroDelayMode {Recorded,Fixed,Minimum}

    public sealed class MacroStep {
        public MacroStepKind Kind {get;set;}
        public int VirtualKey {get;set;}
        public MacroMouseButton Button {get;set;}
        public int X {get;set;}
        public int Y {get;set;}
        public int Delta {get;set;}
        public int DelayMilliseconds {get;set;}
        public MacroStep Clone() {return (MacroStep)MemberwiseClone();}
        public void Validate() {
            if(!Enum.IsDefined(typeof(MacroStepKind),Kind))throw new InvalidDataException("Unknown macro step kind.");
            if(Kind==MacroStepKind.KeyDown||Kind==MacroStepKind.KeyUp) {
                if(VirtualKey<8||VirtualKey>254)throw new InvalidDataException("Keyboard key must be a Windows virtual-key value 8..254.");
            } else if(VirtualKey!=0)throw new InvalidDataException("Keyboard code on a non-key step.");
            if(!Enum.IsDefined(typeof(MacroMouseButton),Button))throw new InvalidDataException("Unknown mouse button.");
            if(Kind!=MacroStepKind.MouseDown&&Kind!=MacroStepKind.MouseUp&&Button!=MacroMouseButton.Left)throw new InvalidDataException("Mouse button on an unrelated step.");
            if(X<-32768||X>32767||Y<-32768||Y>32767)throw new InvalidDataException("Mouse movement exceeds signed 16-bit bounds.");
            if(Kind!=MacroStepKind.MouseMove&&(X!=0||Y!=0))throw new InvalidDataException("Movement on an unrelated step.");
            if(Delta<-32768||Delta>32767||Kind==MacroStepKind.Wheel&&Delta==0)throw new InvalidDataException("Invalid wheel delta.");
            if(Kind!=MacroStepKind.Wheel&&Delta!=0)throw new InvalidDataException("Wheel delta on an unrelated step.");
            if(DelayMilliseconds<0||DelayMilliseconds>99999)throw new InvalidDataException("Delay must be 0..99999 ms.");
            if(Kind!=MacroStepKind.Delay&&DelayMilliseconds!=0)throw new InvalidDataException("Delay on an unrelated step.");
        }
    }

    public sealed class MacroDefinition {
        public string Name {get;set;}
        public MacroRepeatMode RepeatMode {get;set;}
        public int RepeatCount {get;set;}
        public List<MacroStep> Steps {get;set;}
        public MacroDefinition() {Name="Новый макрос";RepeatCount=1;Steps=new List<MacroStep>();}
        public MacroDefinition Clone() {
            var copy=new MacroDefinition{Name=Name,RepeatMode=RepeatMode,RepeatCount=RepeatCount};
            foreach(var step in Steps)copy.Steps.Add(step.Clone());return copy;
        }
        public void Validate() {
            if(String.IsNullOrWhiteSpace(Name)||Name.Length>20||Name.IndexOfAny(new[]{'\r','\n','\0'})>=0)throw new InvalidDataException("Macro name must contain 1..20 characters on one line.");
            if(!Enum.IsDefined(typeof(MacroRepeatMode),RepeatMode))throw new InvalidDataException("Unknown repeat mode.");
            if(RepeatCount<1||RepeatCount>999)throw new InvalidDataException("Repeat count must be 1..999.");
            if(Steps==null||Steps.Count>4096)throw new InvalidDataException("Macro must contain at most 4096 steps.");
            foreach(var step in Steps){if(step==null)throw new InvalidDataException("Null macro step.");step.Validate();}
        }
        // Useful feedback while authoring; no macro is executed by this model.
        public string[] GetWarnings() {
            Validate();var held=new HashSet<string>();var warnings=new List<string>();
            foreach(var step in Steps) {
                string key=null;bool down=false;
                if(step.Kind==MacroStepKind.KeyDown||step.Kind==MacroStepKind.KeyUp){key="VK "+step.VirtualKey.ToString("X2",CultureInfo.InvariantCulture);down=step.Kind==MacroStepKind.KeyDown;}
                if(step.Kind==MacroStepKind.MouseDown||step.Kind==MacroStepKind.MouseUp){key="Mouse "+step.Button;down=step.Kind==MacroStepKind.MouseDown;}
                if(key==null)continue;
                if(down){if(!held.Add(key))warnings.Add("Repeated press without release: "+key);}
                else if(!held.Remove(key))warnings.Add("Release without press: "+key);
            }
            foreach(string key in held)warnings.Add("Missing release: "+key);
            if(Steps.Count==0)warnings.Add("The macro has no steps.");return warnings.ToArray();
        }
    }

    public sealed class MacroLibrary {
        public int Version {get;set;}
        public List<MacroDefinition> Macros {get;set;}
        public MacroLibrary() {Version=1;Macros=new List<MacroDefinition>();}
        public void Validate() {
            if(Version!=1)throw new InvalidDataException("Unsupported macro project version.");
            if(Macros==null||Macros.Count>256)throw new InvalidDataException("Macro library must contain at most 256 macros.");
            var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(var macro in Macros){if(macro==null)throw new InvalidDataException("Null macro.");macro.Validate();if(!names.Add(macro.Name))throw new InvalidDataException("Macro names must be unique.");}
        }
    }

    public static class MacroProjectCodec {
        const int MaximumFileBytes=4194304;
        static JavaScriptSerializer Serializer() {return new JavaScriptSerializer {MaxJsonLength=MaximumFileBytes,RecursionLimit=20};}
        public static string ToJson(MacroLibrary library) {
            if(library==null)throw new ArgumentNullException("library");library.Validate();
            // Explicit portable schema: symbolic enums do not depend on future enum ordering.
            var macros=new List<object>();
            foreach(var macro in library.Macros) {
                var steps=new List<object>();
                foreach(var step in macro.Steps) {
                    var row=new Dictionary<string,object>{{"kind",step.Kind.ToString()}};
                    switch(step.Kind) {
                    case MacroStepKind.KeyDown:case MacroStepKind.KeyUp:row["virtualKey"]=step.VirtualKey;break;
                    case MacroStepKind.MouseDown:case MacroStepKind.MouseUp:row["button"]=step.Button.ToString();break;
                    case MacroStepKind.MouseMove:row["x"]=step.X;row["y"]=step.Y;break;
                    case MacroStepKind.Wheel:row["delta"]=step.Delta;break;
                    case MacroStepKind.Delay:row["milliseconds"]=step.DelayMilliseconds;break;
                    }
                    steps.Add(row);
                }
                macros.Add(new Dictionary<string,object>{{"name",macro.Name},{"repeatMode",macro.RepeatMode.ToString()},{"repeatCount",macro.RepeatCount},{"steps",steps}});
            }
            string json=Serializer().Serialize(new Dictionary<string,object>{{"format","FURYCUBE-G9-MACROS"},{"version",1},{"macros",macros}});
            if(Encoding.UTF8.GetByteCount(json)>MaximumFileBytes)throw new InvalidDataException("Macro project is too large.");return json;
        }
        static Dictionary<string,object> Object(object value,params string[] fields) {
            var row=value as Dictionary<string,object>;if(row==null)throw new InvalidDataException("Expected a JSON object.");
            if(row.Count!=fields.Length)throw new InvalidDataException("Unexpected or missing JSON fields.");
            foreach(string field in fields)if(!row.ContainsKey(field))throw new InvalidDataException("Missing JSON field: "+field);return row;
        }
        static string Text(object value) {var text=value as string;if(text==null)throw new InvalidDataException("Expected a string.");return text;}
        static int Integer(object value) {if(!(value is int))throw new InvalidDataException("Expected a bounded integer.");return (int)value;}
        static T Symbol<T>(object value) where T:struct {
            string name=Text(value);T result;if(!Enum.TryParse<T>(name,false,out result)||!Enum.IsDefined(typeof(T),result)||result.ToString()!=name)throw new InvalidDataException("Unknown symbolic value: "+name);return result;
        }
        static object[] ArrayValue(object value) {var array=value as object[];if(array==null)throw new InvalidDataException("Expected an array.");return array;}
        public static MacroLibrary FromJson(string json) {
            if(json==null)throw new ArgumentNullException("json");if(Encoding.UTF8.GetByteCount(json)>MaximumFileBytes)throw new InvalidDataException("Macro project is too large.");
            try {
                var top=Object(Serializer().DeserializeObject(json),"format","version","macros");
                if(Text(top["format"])!="FURYCUBE-G9-MACROS"||Integer(top["version"])!=1)throw new InvalidDataException("Unsupported macro project format/version.");
                var library=new MacroLibrary();var macros=ArrayValue(top["macros"]);if(macros.Length>256)throw new InvalidDataException("Too many macros.");
                foreach(object item in macros) {
                    var row=Object(item,"name","repeatMode","repeatCount","steps");var macro=new MacroDefinition{Name=Text(row["name"]),RepeatMode=Symbol<MacroRepeatMode>(row["repeatMode"]),RepeatCount=Integer(row["repeatCount"])};
                    var steps=ArrayValue(row["steps"]);if(steps.Length>4096)throw new InvalidDataException("Too many steps.");
                    foreach(object itemStep in steps) {
                        var preliminary=itemStep as Dictionary<string,object>;if(preliminary==null||!preliminary.ContainsKey("kind"))throw new InvalidDataException("Missing step kind.");
                        MacroStepKind kind=Symbol<MacroStepKind>(preliminary["kind"]);var step=new MacroStep{Kind=kind};
                        switch(kind) {
                        case MacroStepKind.KeyDown:case MacroStepKind.KeyUp:var k=Object(itemStep,"kind","virtualKey");step.VirtualKey=Integer(k["virtualKey"]);break;
                        case MacroStepKind.MouseDown:case MacroStepKind.MouseUp:var b=Object(itemStep,"kind","button");step.Button=Symbol<MacroMouseButton>(b["button"]);break;
                        case MacroStepKind.MouseMove:var m=Object(itemStep,"kind","x","y");step.X=Integer(m["x"]);step.Y=Integer(m["y"]);break;
                        case MacroStepKind.Wheel:var w=Object(itemStep,"kind","delta");step.Delta=Integer(w["delta"]);break;
                        case MacroStepKind.Delay:var d=Object(itemStep,"kind","milliseconds");step.DelayMilliseconds=Integer(d["milliseconds"]);break;
                        }
                        macro.Steps.Add(step);
                    }
                    library.Macros.Add(macro);
                }
                library.Validate();return library;
            } catch(ArgumentException ex){throw new InvalidDataException("Invalid macro JSON.",ex);}catch(InvalidOperationException ex){throw new InvalidDataException("Invalid macro JSON.",ex);}
        }
        public static MacroLibrary Load(string path) {
            var info=new FileInfo(path);if(info.Length>MaximumFileBytes)throw new InvalidDataException("Macro project is too large.");
            return FromJson(File.ReadAllText(path,new UTF8Encoding(false,true)));
        }
        public static void Save(MacroLibrary library,string path) {
            string json=ToJson(library);string full=Path.GetFullPath(path);string directory=Path.GetDirectoryName(full);Directory.CreateDirectory(directory);
            string temporary=full+"."+Guid.NewGuid().ToString("N")+".tmp";
            try {File.WriteAllText(temporary,json,new UTF8Encoding(false,true));if(File.Exists(full))File.Replace(temporary,full,null);else File.Move(temporary,full);}finally{if(File.Exists(temporary))File.Delete(temporary);}
        }
    }

    // Recording transformation is independent of WinForms and does not execute input.
    public sealed class MacroRecorder {
        readonly List<MacroStep> steps=new List<MacroStep>();readonly HashSet<string> held=new HashSet<string>();readonly List<string> pressOrder=new List<string>();
        readonly MacroDelayMode delayMode;readonly int fixedDelay;long? previous;
        public MacroRecorder(MacroDelayMode delayMode,int fixedDelayMilliseconds) {
            if(!Enum.IsDefined(typeof(MacroDelayMode),delayMode))throw new ArgumentOutOfRangeException("delayMode");
            if(fixedDelayMilliseconds<0||fixedDelayMilliseconds>99999)throw new ArgumentOutOfRangeException("fixedDelayMilliseconds");
            this.delayMode=delayMode;fixedDelay=fixedDelayMilliseconds;
        }
        public bool Append(MacroStep step,long monotonicMilliseconds) {
            if(step==null)throw new ArgumentNullException("step");step.Validate();if(step.Kind==MacroStepKind.Delay)throw new ArgumentException("Recorder accepts input events, not delays.");
            if(monotonicMilliseconds<0||previous.HasValue&&monotonicMilliseconds<previous.Value)throw new ArgumentOutOfRangeException("monotonicMilliseconds");
            string key=null;bool down=false;
            if(step.Kind==MacroStepKind.KeyDown||step.Kind==MacroStepKind.KeyUp){key="k"+step.VirtualKey;down=step.Kind==MacroStepKind.KeyDown;}
            if(step.Kind==MacroStepKind.MouseDown||step.Kind==MacroStepKind.MouseUp){key="m"+step.Button;down=step.Kind==MacroStepKind.MouseDown;}
            if(key!=null) {if(down&&held.Contains(key)||!down&&!held.Contains(key))return false;}
            long delay=0;
            if(previous.HasValue)delay=delayMode==MacroDelayMode.Recorded?monotonicMilliseconds-previous.Value:delayMode==MacroDelayMode.Fixed?fixedDelay:0;
            long required=(delay/99999)+(delay%99999==0?0:1)+1;
            int heldAfter=held.Count+(key==null?0:down?1:-1);
            if(required>4096||steps.Count+required+heldAfter>4096)throw new InvalidDataException("Recording reached the 4096-step limit.");
            if(key!=null){if(down){held.Add(key);pressOrder.Add(key);}else{held.Remove(key);pressOrder.Remove(key);}}
            while(delay>0){int part=(int)Math.Min(99999,delay);steps.Add(new MacroStep{Kind=MacroStepKind.Delay,DelayMilliseconds=part});delay-=part;}
            steps.Add(step.Clone());previous=monotonicMilliseconds;return true;
        }
        public List<MacroStep> Finish() {
            var result=new List<MacroStep>();foreach(var step in steps)result.Add(step.Clone());
            // Stopping while a key is held adds its release; no OS input is sent.
            for(int i=pressOrder.Count-1;i>=0;i--) {
                string key=pressOrder[i];
                if(result.Count>=4096)throw new InvalidDataException("Not enough space for recorded release events.");
                if(key[0]=='k')result.Add(new MacroStep{Kind=MacroStepKind.KeyUp,VirtualKey=Int32.Parse(key.Substring(1),CultureInfo.InvariantCulture)});
                else result.Add(new MacroStep{Kind=MacroStepKind.MouseUp,Button=(MacroMouseButton)Enum.Parse(typeof(MacroMouseButton),key.Substring(1))});
            }
            return result;
        }
        public int Count {get{return steps.Count;}}
    }
}
