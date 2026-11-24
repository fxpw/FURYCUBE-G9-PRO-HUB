using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using Furycube.G9.Bindings;

// Pure bridge boundary: no HID, process, registry or file-write APIs.
internal static class G9WebApi {
    static readonly HashSet<string> Methods=new HashSet<string>(new[]{"app.state","app.preferences","app.exit","device.scan","device.assign","device.settings","screen.update","screen.chooseImage","screen.open","screen.save","screen.exportPng","screen.upload","screen.live","screen.schedule","macros.save","macros.delete","macros.import","macros.export","profiles.save","profiles.open","profiles.rename","profiles.delete","profiles.import","profiles.export"},StringComparer.Ordinal);
    internal sealed class Request {internal string Id,Method;internal Dictionary<string,object> Payload;}
    internal static JavaScriptSerializer Json() {return new JavaScriptSerializer{MaxJsonLength=4194304,RecursionLimit=40};}
    internal static bool TrustedOrigin(string source) {Uri uri;return Uri.TryCreate(source,UriKind.Absolute,out uri)&&uri.Scheme=="https"&&uri.Host=="g9.local"&&uri.IsDefaultPort&&String.IsNullOrEmpty(uri.UserInfo);}
    internal static Request Parse(string source,string json) {
        if(!TrustedOrigin(source))throw new InvalidDataException("Недопустимый источник интерфейса.");
        if(String.IsNullOrEmpty(json)||json.Length>2097152)throw new InvalidDataException("Слишком большой запрос интерфейса.");
        new UniqueJson(json).Check();
        var row=Json().DeserializeObject(json) as Dictionary<string,object>;
        if(row==null||row.Count!=3||!row.ContainsKey("id")||!row.ContainsKey("method")||!row.ContainsKey("payload"))throw new InvalidDataException("Неверный запрос интерфейса.");
        string id=row["id"] as string,method=row["method"] as string;
        if(id==null||!Regex.IsMatch(id,@"\A[a-zA-Z0-9:_-]{1,80}\z")||method==null||!Methods.Contains(method))throw new InvalidDataException("Неизвестная операция интерфейса.");
        var payload=row["payload"] as Dictionary<string,object>;
        if(payload==null)throw new InvalidDataException("Параметры должны быть объектом.");
        return new Request{Id=id,Method=method,Payload=payload};
    }
    internal static string Text(Dictionary<string,object> row,string name,int maximum=100) {
        object value;string text;if(!row.TryGetValue(name,out value)||(text=value as string)==null||text.Length>maximum||text.IndexOfAny(new[]{'\0','\r','\n'})>=0)throw new InvalidDataException("Неверный параметр: "+name);return text;
    }
    internal static int Number(Dictionary<string,object> row,string name,int min,int max) {
        object value;if(!row.TryGetValue(name,out value)||!(value is int)||(int)value<min||(int)value>max)throw new InvalidDataException("Неверное число: "+name);return (int)value;
    }
    internal static bool Flag(Dictionary<string,object> row,string name) {object value;if(!row.TryGetValue(name,out value)||!(value is bool))throw new InvalidDataException("Неверный переключатель: "+name);return (bool)value;}
    internal static string Quote(string value) {
        if(String.IsNullOrEmpty(value)||value.IndexOfAny(new[]{'"','\0','\r','\n'})>=0||value.EndsWith("\\",StringComparison.Ordinal))throw new InvalidDataException("Неверный аргумент native API.");
        return "\""+value+"\"";
    }
    internal static string DeviceSuffix(bool wired,string identity) {
        int at=identity.IndexOf(" path=",StringComparison.OrdinalIgnoreCase);
        if(at<0)throw new InvalidDataException("Нет подтверждённого пути мыши.");
        string path=identity.Substring(at+6);
        string expected=wired?"vid_1d57&pid_2148&mi_02&col04":"vid_1d57&pid_fa60&mi_02&col04";
        if(path.IndexOf(expected,StringComparison.OrdinalIgnoreCase)<0||path.IndexOf('\n')>=0)throw new InvalidDataException("Подключение мыши неоднозначно.");
        return " --pid "+(wired?"2148":"fa60")+" --expected-path "+Quote(path);
    }
    internal static string Assign(Dictionary<string,object> payload) {
        int profile=Number(payload,"profile",1,5),index=Number(payload,"index",0,17);
        string id=Text(payload,"actionId",80);BindingAction action=null;
        foreach(var item in BindingCatalog.Actions)if(item.Id==id)action=item;
        if(action==null)throw new InvalidDataException("Неизвестное назначение.");
        byte p1=(byte)(payload.ContainsKey("param1")?Number(payload,"param1",0,255):action.DefaultParam1),p2=(byte)(payload.ContainsKey("param2")?Number(payload,"param2",0,255):action.DefaultParam2);
        BindingCatalog.Validate(action.Function,p1,p2);
        return "assign --profile "+profile+" --index "+index+" --function "+action.Function.ToString("x2")+" --param1 "+p1.ToString("x2")+" --param2 "+p2.ToString("x2");
    }
    internal static string Settings(int profile,string id,string value) {
        if(profile<1||profile>5||id==null||!Regex.IsMatch(id,@"\A[a-zA-Z0-9.-]{1,80}\z")||value==null||!Regex.IsMatch(value,@"\A[a-zA-Z0-9.#,-]{1,100}\z"))throw new InvalidDataException("Неверный параметр настроек.");
        return "settings-set --profile "+profile+" --field "+Quote(id)+" --value "+Quote(value);
    }
    // JavaScriptSerializer silently accepts duplicate members. Reject them
    // before binding a command, including escaped duplicate keys in nested DTOs.
    sealed class UniqueJson {
        readonly string text;int position;
        internal UniqueJson(string value){text=value;}
        void Invalid(){throw new InvalidDataException("Неоднозначный JSON-запрос.");}
        void Space(){while(position<text.Length&&Char.IsWhiteSpace(text[position]))position++;}
        string String(){
            if(position>=text.Length||text[position]!='"')Invalid();int start=position++;
            while(position<text.Length){char c=text[position++];if(c=='"')return Json().Deserialize<string>(text.Substring(start,position-start));if(c<32)Invalid();if(c=='\\'){if(position>=text.Length)Invalid();position++;}}
            Invalid();return null;
        }
        void Value(int depth){
            if(depth>40)Invalid();Space();if(position>=text.Length)Invalid();char c=text[position];
            if(c=='"'){String();return;}
            if(c=='{'){
                position++;Space();if(position<text.Length&&text[position]=='}'){position++;return;}var keys=new HashSet<string>(StringComparer.Ordinal);
                while(true){Space();if(!keys.Add(String()))Invalid();Space();if(position>=text.Length||text[position++]!=':')Invalid();Value(depth+1);Space();if(position>=text.Length)Invalid();c=text[position++];if(c=='}')return;if(c!=',')Invalid();}
            }
            if(c=='['){position++;Space();if(position<text.Length&&text[position]==']'){position++;return;}while(true){Value(depth+1);Space();if(position>=text.Length)Invalid();c=text[position++];if(c==']')return;if(c!=',')Invalid();}}
            int start=position;while(position<text.Length&&!Char.IsWhiteSpace(text[position])&&text[position]!=','&&text[position]!=']'&&text[position]!='}')position++;if(position==start)Invalid();
        }
        internal void Check(){Value(0);Space();if(position!=text.Length)Invalid();}
    }
}
