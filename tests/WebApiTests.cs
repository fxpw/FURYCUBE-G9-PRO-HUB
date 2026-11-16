using System;
using System.Collections.Generic;
using System.IO;
using Furycube.G9.Bindings;
using Furycube.G9.Macros;

internal static class WebApiTests {
    static int checks;
    const string Origin="https://g9.local/index.html";
    static void Check(bool value,string name){if(!value)throw new Exception("FAIL "+name);checks++;}
    static void Reject(Action action,string name){try{action();}catch(InvalidDataException){checks++;return;}catch(ArgumentException){checks++;return;}catch(InvalidOperationException){checks++;return;}throw new Exception("FAIL accepted "+name);}
    static string Request(object id,string method,object payload){return G9WebApi.Json().Serialize(new Dictionary<string,object>{{"id",id},{"method",method},{"payload",payload}});}
    static G9WebApi.Request Parse(string json){return G9WebApi.Parse(Origin,json);}
    static Dictionary<string,object> Payload(params object[] values){var result=new Dictionary<string,object>();for(int i=0;i<values.Length;i+=2)result.Add((string)values[i],values[i+1]);return result;}
    static void Origins() {
        foreach(string origin in new[]{Origin,"https://g9.local/","https://G9.LOCAL:443/editor?x=a#layers"})Check(G9WebApi.TrustedOrigin(origin),"same HTTPS application origin accepted");
        foreach(string origin in new[]{null,"","https://g9.local.evil.test/","https://g9.local:444/","http://g9.local/","file:///C:/index.html","https://evil.test/","https://g9.local@evil.test/","https://evil@g9.local/","javascript:alert(1)","https://g9.local./","data:text/html,test"}) {
            Check(!G9WebApi.TrustedOrigin(origin),"foreign or malformed origin rejected");
            Reject(delegate{G9WebApi.Parse(origin,Request("1","app.exit",Payload()));},"foreign page cannot dispatch an allowed native method");
        }
    }
    static void Envelopes() {
        foreach(string method in new[]{"app.state","app.preferences","app.exit","device.scan","device.assign","device.settings","screen.update","screen.chooseImage","screen.open","screen.save","screen.exportPng","screen.upload","screen.live","macros.save","macros.delete","macros.import","macros.export","profiles.save","profiles.open","profiles.rename","profiles.delete","profiles.import","profiles.export"}) {
            var parsed=Parse(Request("ui:2_1-x",method,Payload()));Check(parsed.Id=="ui:2_1-x"&&parsed.Method==method&&parsed.Payload.Count==0,"documented method available: "+method);
        }
        foreach(object id in new object[]{null,"",123,true,1.5,"bad id","тест",new string('x',81),"line\nother"})Reject(delegate{Parse(Request(id,"app.state",Payload()));},"request ID must be a bounded explicit string");
        foreach(string method in new[]{null,"","device.flash","system.exec","app.STATE","app.state\napp.exit"})Reject(delegate{Parse(Request("1",method,Payload()));},"unlisted method rejected");
        foreach(string json in new[]{"", "{", "null", "[]", "{}", "{\"id\":\"1\",\"method\":\"app.state\"}", "{\"id\":\"1\",\"method\":\"app.state\",\"payload\":{},\"extra\":1}", Request("1","app.state",null),Request("1","app.state",new object[0]),new string(' ',2097153)})Reject(delegate{Parse(json);},"malformed or oversized envelope rejected");
        foreach(string json in new[]{
            "{\"id\":\"1\",\"id\":\"2\",\"method\":\"app.state\",\"payload\":{}}",
            "{\"id\":\"1\",\"method\":\"app.state\",\"method\":\"app.exit\",\"payload\":{}}",
            "{\"id\":\"1\",\"method\":\"app.state\",\"m\\u0065thod\":\"app.exit\",\"payload\":{}}",
            "{\"id\":\"1\",\"method\":\"device.assign\",\"payload\":{\"profile\":1,\"profile\":2}}",
            "{\"id\":\"1\",\"method\":\"device.settings\",\"payload\":{\"changes\":[{\"value\":\"1\",\"value\":\"5\"}]}}"})Reject(delegate{Parse(json);},"duplicate JSON members rejected before native dispatch");
        var valid=Parse(Request("1","app.state",Payload("a",Payload("x",1),"b",Payload("x",2),"text","{\"x\":1,\"x\":2}")));
        Check(valid.Payload.Count==3,"repeated names in separate objects and JSON-looking string literals are valid");
    }
    static void PrimitiveFields() {
        Check(G9WebApi.Text(Payload("x","Russian русский"),"x",100)=="Russian русский","native labels retain Unicode");
        foreach(object value in new object[]{null,1,true,new object[0],"line\nnext","a\rb","nul\0",new string('x',101)})Reject(delegate{G9WebApi.Text(Payload("x",value),"x",100);},"invalid text input rejected");
        Reject(delegate{G9WebApi.Text(Payload(),"x");},"missing text rejected");
        Check(G9WebApi.Number(Payload("x",1),"x",1,5)==1&&G9WebApi.Number(Payload("x",5),"x",1,5)==5,"integer range endpoints accepted");
        foreach(object value in new object[]{null,"1",true,1.0,1L,0,6})Reject(delegate{G9WebApi.Number(Payload("x",value),"x",1,5);},"integer type and bounds strict");
        Check(G9WebApi.Flag(Payload("x",true),"x")&&!G9WebApi.Flag(Payload("x",false),"x"),"literal Boolean flags accepted");
        foreach(object value in new object[]{null,"true",0,1})Reject(delegate{G9WebApi.Flag(Payload("x",value),"x");},"Boolean coercion rejected");
    }
    static void Commands() {
        Check(G9WebApi.Assign(Payload("profile",1,"index",3,"actionId","keyboard"))=="assign --profile 1 --index 3 --function 11 --param1 00 --param2 27","ordinary key command exact and bounded");
        Check(G9WebApi.Assign(Payload("profile",5,"index",17,"actionId","macro","param1",0,"param2",32)).EndsWith("--function 12 --param1 00 --param2 20"),"macro selector represented as validated byte");
        foreach(var action in BindingCatalog.Actions) {
            string command=G9WebApi.Assign(Payload("profile",1,"index",3,"actionId",action.Id));
            Check(command.Contains("--function "+action.Function.ToString("x2")),"catalog action remains available: "+action.Id);
        }
        foreach(var payload in new[]{Payload("profile",0,"index",3,"actionId","left"),Payload("profile",1,"index",18,"actionId","left"),Payload("profile",1,"index",3,"actionId","left\" --pid fa60"),Payload("profile",1,"index",3,"actionId","keyboard","param1",0,"param2",0),Payload("profile",1,"index",3,"actionId","macro","param2",0),Payload("profile",1,"index",3,"actionId","sniper","param2",9),Payload("profile",1,"index",3,"actionId","keyboard","param2",256)})Reject(delegate{G9WebApi.Assign(payload);},"invalid assignment cannot create native command");
        Check(G9WebApi.Settings(2,"dpi.value1","1600")=="settings-set --profile 2 --field \"dpi.value1\" --value \"1600\"","setting values quoted and profile explicit");
        foreach(string text in new[]{"x y","x\" --pid fa60","x\n","x\0","x&whoami","$(x)","`x`"}) {
            Reject(delegate{G9WebApi.Settings(1,text,"1");},"unsafe field characters rejected");
            Reject(delegate{G9WebApi.Settings(1,"debounce.ms",text);},"unsafe value characters rejected");
        }
        Check(G9WebApi.Quote(@"C:\folder with spaces\image.png")=="\"C:\\folder with spaces\\image.png\"","one native argument fully quoted");
        foreach(string value in new[]{null,"","x\" y","x\r","x\n","x\0","trailing\\"})Reject(delegate{G9WebApi.Quote(value);},"invalid quoted native argument rejected");
        const string path=@"\\?\hid#vid_1d57&pid_2148&mi_02&col04#fixture";
        string identity="caps vid=1d57 pid=2148 version=0108 featurebytes=262 path="+path;
        Check(G9WebApi.DeviceSuffix(true,identity)==" --pid 2148 --expected-path \""+path+"\"","native operations pin configuration path and cable PID");
        Reject(delegate{G9WebApi.DeviceSuffix(false,identity);},"radio operation cannot use wired interface path");
        Reject(delegate{G9WebApi.DeviceSuffix(true,identity+"\n"+identity);},"multiple configuration identities refused");
        Reject(delegate{G9WebApi.DeviceSuffix(true,"no path");},"missing configuration identity refused");
    }
    static void MacroContract() {
        var document=Payload("name","Zero","repeatMode","Counted","repeatCount",1,"steps",new[]{Payload("kind","KeyDown","virtualKey",48),Payload("kind","Delay","milliseconds",50),Payload("kind","KeyUp","virtualKey",48)});
        var request=Parse(Request("macro:1","macros.save",Payload("document",document)));
        string json=G9WebApi.Json().Serialize(Payload("format","FURYCUBE-G9-MACROS","version",1,"macros",new[]{request.Payload["document"]}));
        var library=MacroProjectCodec.FromJson(json);
        Check(library.Macros[0].Steps.Count==3&&library.Macros[0].Steps[0].VirtualKey==48,"Vue macro document uses actual portable codec schema");
        Check(MacroProjectCodec.FromJson(MacroProjectCodec.ToJson(library)).Macros[0].Name=="Zero","portable macro round trip remains exact");
        foreach(int repeats in new[]{0,1000,65535})Reject(delegate{MacroProjectCodec.FromJson(json.Replace("\"repeatCount\":1","\"repeatCount\":"+repeats));},"native macro repeat bounds remain enforced");
        foreach(int vk in new[]{1,7,255})Reject(delegate{MacroProjectCodec.FromJson(json.Replace("\"virtualKey\":48","\"virtualKey\":"+vk));},"native macro VK bounds remain enforced");
    }
    static int Main() {try{Origins();Envelopes();PrimitiveFields();Commands();MacroContract();Console.WriteLine("PASS "+checks+" Vue bridge origin, envelope, native command and portable macro checks; no registry/HID/GUI.");return 0;}catch(Exception error){Console.Error.WriteLine(error);return 1;}}
}
