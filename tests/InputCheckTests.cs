using System;
using System.Web.Script.Serialization;
class InputCheckTests {
    static int checks;
    static void Check(bool value,string why){checks++;if(!value)throw new Exception(why);}
    static readonly string Radio=@"\\?\HID#VID_1D57&PID_FA60&MI_01#one";
    static readonly string Usb=@"\\?\HID#VID_1D57&PID_2148&MI_01#two";
    static void Full(G9InputCheckState s,string path){
        s.Mouse(path,1,0,0,0);s.Mouse(path,0,0,0x3FF,0);s.Mouse(path,0,0,0x400,120);s.Mouse(path,0,0,0x400,-120);
        s.Keyboard(path,0x30,false);s.Keyboard(path,0x30,true);
    }
    static int Main(){try{
        var s=new G9InputCheckState(0xFA60);Full(s,Usb);Check(s.CompletedCount==0,"wired events cannot qualify receiver input");
        Full(s,@"\\?\HID#VID_9999&PID_FA60#other");Check(s.CompletedCount==0,"another vendor cannot qualify G9");
        Full(s,@"\\?\HID#VID_1D57&PID_FA600#other");Check(s.CompletedCount==0,"partial PID match rejected");
        s.Mouse(Radio,0,0,0x2AA,0);s.Keyboard(Radio,0x30,true);Check(s.CompletedCount==0,"releases alone cannot qualify button clicks");
        s.Mouse(Radio,0,0,0x155,0);s.Keyboard(Radio,0x30,false);Check(s.CompletedCount==0,"held buttons are not completed clicks");
        s.Mouse(Radio,0,0,0x2AA,0);Check(s.CompletedCount==5,"five make/break pairs complete five physical clicks");
        s.Keyboard(Usb,0x30,true);Check(!s.IsDone(8),"screen-key release from another mouse ignored");
        s.Keyboard(Radio,0x31,true);Check(!s.IsDone(8),"unrelated keys cannot qualify screen key");
        s.Keyboard(Radio,0x30,true);Check(s.IsDone(8),"screen-key make/break from receiver completes check");
        s.Mouse(Radio,0,0,0x800,120);Check(!s.IsDone(6)&&!s.IsDone(7),"horizontal scroll does not qualify vertical directions");
        s.Mouse(Radio,0,0,0x400,0);Check(!s.IsDone(6)&&!s.IsDone(7),"zero wheel delta not qualified");
        s.Mouse(Radio,0,0,0x400,120);Check(s.IsDone(6)&&!s.IsDone(7),"up and down qualified separately");
        s.Mouse(Radio,0,0,0x400,-120);Check(!s.Complete,"missing movement prevents overall pass");
        string partial=new JavaScriptSerializer().Serialize(s.Report(DateTime.UtcNow,"closed"));
        Check(partial.Contains("\"success\":false")&&partial.Contains("\"missing\":[\"movement\"]"),"closing partial check records exact missing item");
        s.Mouse(Radio.ToLowerInvariant(),0,-1,0,0);Check(s.Complete&&s.CompletedCount==9,"complete receiver test reaches nine green steps");
        Full(s,Radio);Check(s.CompletedCount==9&&s.Missing.Length==0,"repeated input cannot inflate result");
        string report=new JavaScriptSerializer().Serialize(s.Report(DateTime.UtcNow,"completed"));
        Check(report.Contains("\"success\":true")&&report.Contains("\"deviceWrites\":false"),"full report declares pass and passive scope");
        string english=new JavaScriptSerializer().Serialize(s.Report(DateTime.UtcNow,"completed",true));Check(english.Contains("Mouse movement")&&english.Contains("\"id\":\"screenKey\"")&&english.Contains("\"success\":true"),"English report preserves stable IDs and acceptance result");
        var usb=new G9InputCheckState(0x2148);Full(usb,Radio);Check(!usb.Complete&&usb.CompletedCount==0,"receiver events cannot qualify USB input");Full(usb,Usb);Check(usb.Complete,"USB set independently completes");
        Console.WriteLine("PASS "+checks+" input-check acceptance checks");return 0;
    }catch(Exception error){Console.Error.WriteLine(error);return 1;}}
}
