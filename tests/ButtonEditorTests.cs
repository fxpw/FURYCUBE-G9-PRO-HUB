using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Furycube.G9.Bindings;

internal static class ButtonEditorTests {
    static int count;
    const string Captured="083b010200000300000400001100270d00003c00000500000600000100000100000100000100000100000100000100000100000a000009000000b0";
    static readonly JavaScriptSerializer Json=new JavaScriptSerializer();
    static void Check(bool value,string name){if(!value)throw new Exception("FAIL "+name);count++;}
    static void Reject(Action action,string name){try{action();}catch(InvalidDataException){count++;return;}throw new Exception("FAIL accepted "+name);}
    static byte[] Bytes(string value){var data=new byte[value.Length/2];for(int i=0;i<data.Length;i++)data[i]=byte.Parse(value.Substring(i*2,2),NumberStyles.HexNumber,CultureInfo.InvariantCulture);return data;}
    static string Hex(byte[] data){return BitConverter.ToString(data).Replace("-","").ToLowerInvariant();}
    static void Checksum(byte[] data){int sum=0;for(int i=3;i<57;i++)sum+=data[i];data[57]=(byte)(sum>>8);data[58]=(byte)sum;}
    static Dictionary<string,object> Document(byte[] data){
        var rows=new List<object>();for(int i=0;i<18;i++){int at=3+i*3;rows.Add(new Dictionary<string,object>{{"index",i},{"function",(int)data[at]},{"param1",(int)data[at+1]},{"param2",(int)data[at+2]}});}
        return new Dictionary<string,object>{{"profile",1},{"keymapHex",Hex(data)},{"buttons",rows.ToArray()}};
    }
    static string Output(Dictionary<string,object> document){return "DEVICE wired2148\r\nMAPPING_JSON "+Json.Serialize(document)+"\r\nDONE\r\n";}
    static Dictionary<string,object> Row(Dictionary<string,object> document,int row){return (Dictionary<string,object>)((object[])document["buttons"])[row];}
    static void Parsing(){
        byte[] wire=Bytes(Captured);var parsed=G9MappingSnapshot.Parse(Output(Document(wire)));
        Check(parsed.profile==1&&parsed.Keys.Length==59&&parsed.Keys[3]==2,"captured profile-one table retains its left click");
        Check(parsed.buttons[3].index==3&&parsed.buttons[3].function==0x11&&parsed.buttons[3].param2==0x27,"captured physical button6 is keyboard zero");
        Check(parsed.buttons[6].function==5&&parsed.buttons[16].function==10&&parsed.buttons[17].function==9,"captured side and wheel slots preserve their own indices");
        var full=Document(wire);
        foreach(var definition in BindingCatalog.Buttons){var row=Row(full,definition.Index);row["name"]=definition.Name;row["physicalLabel"]=definition.PhysicalLabel;row["visible"]=definition.IsVisible;row["bindingId"]=BindingCatalog.Resolve((byte)(int)row["function"],(byte)(int)row["param1"],(byte)(int)row["param2"]).Id;}
        Check(((object[])full["buttons"]).Length==18&&Row(full,3).Count==8&&Hex(G9MappingSnapshot.Parse(Output(full)).Keys)==Captured,"complete eight-field G9Device PrintMapping schema");
        const string realOledRow="{\"index\":3,\"name\":\"Кнопка экрана OLED\",\"physicalLabel\":\"6. Кнопка экрана OLED\",\"visible\":true,\"function\":17,\"param1\":0,\"param2\":39,\"bindingId\":\"keyboard\"}";
        ((object[])full["buttons"])[3]=Json.DeserializeObject(realOledRow);Check(G9MappingSnapshot.Parse(Output(full)).buttons[3].param2==39,"literal full backend OLED row remains compatible");
        var reversed=Document(wire);Array.Reverse((object[])reversed["buttons"]);Check(Hex(G9MappingSnapshot.Parse(Output(reversed)).Keys)==Captured,"row order does not change wire indices");
        var upper=Document(wire);upper["keymapHex"]=Captured.ToUpperInvariant();Check(Hex(G9MappingSnapshot.Parse(Output(upper)).Keys)==Captured,"uppercase wire hex is accepted");
        Reject(delegate{G9MappingSnapshot.Parse("DEVICE only");},"missing mapping marker");
        Reject(delegate{G9MappingSnapshot.Parse(Output(Document(wire))+Output(Document(wire)));},"multiple snapshots in one transaction");
        Reject(delegate{G9MappingSnapshot.Parse("MAPPING_JSON {");},"truncated JSON");
        var wrong=Document(wire);wrong["profile"]=2;Reject(delegate{G9MappingSnapshot.Parse(Output(wrong));},"wrong profile");
        for(int profile=2;profile<=5;profile++) {
            byte[] other=(byte[])wire.Clone();other[2]=(byte)profile;var document=Document(other);document["profile"]=profile;
            var table=G9MappingSnapshot.Parse(Output(document));
            byte[] edited=BindingCatalog.WithAssignment(table.Keys,3,0x11,0,4,profile);
            Check(table.profile==profile&&edited[2]==profile&&edited[14]==4,"profile "+profile+" assignment stays in the explicitly read profile");
            int different=profile==5?1:profile+1;
            Reject(delegate{BindingCatalog.WithAssignment(table.Keys,3,0x11,0,4,different);},"mismatched assignment profile");
        }
        wrong=Document(wire);wrong["profile"]="1";Reject(delegate{G9MappingSnapshot.Parse(Output(wrong));},"string coerced to profile number");
        wrong=Document(wire);wrong["profile"]=1.1;Reject(delegate{G9MappingSnapshot.Parse(Output(wrong));},"fraction coerced to profile number");
        wrong=Document(wire);wrong["keymapHex"]=" 8"+Captured.Substring(2);Reject(delegate{G9MappingSnapshot.Parse(Output(wrong));},"spaces masquerading as valid hex digits");
        wrong=Document(wire);wrong["keymapHex"]=Captured.Substring(0,117);Reject(delegate{G9MappingSnapshot.Parse(Output(wrong));},"short hex table");
        wrong=Document(wire);wrong["keymapHex"]="0z"+Captured.Substring(2);Reject(delegate{G9MappingSnapshot.Parse(Output(wrong));},"non-hex character");
        byte[] corrupt=(byte[])wire.Clone();corrupt[58]^=1;wrong=Document(corrupt);Reject(delegate{G9MappingSnapshot.Parse(Output(wrong));},"bad wire checksum");
        corrupt=(byte[])wire.Clone();corrupt[0]=9;wrong=Document(corrupt);Reject(delegate{G9MappingSnapshot.Parse(Output(wrong));},"stale macro report");
        corrupt=(byte[])wire.Clone();corrupt[2]=2;wrong=Document(corrupt);Reject(delegate{G9MappingSnapshot.Parse(Output(wrong));},"wire profile conflicts with declared profile");
        wrong=Document(wire);Row(wrong,3)["param2"]=0x26;Reject(delegate{G9MappingSnapshot.Parse(Output(wrong));},"row disagrees with checksum-valid wire bytes");
        wrong=Document(wire);Row(wrong,3)["index"]=2;Reject(delegate{G9MappingSnapshot.Parse(Output(wrong));},"duplicate wire index");
        wrong=Document(wire);Row(wrong,3)["index"]=18;Reject(delegate{G9MappingSnapshot.Parse(Output(wrong));},"out-of-range wire index");
        wrong=Document(wire);Row(wrong,8).Remove("param1");Reject(delegate{G9MappingSnapshot.Parse(Output(wrong));},"missing zero-valued row member cannot silently default");
        wrong=Document(wire);Row(wrong,3)["param2"]="39";Reject(delegate{G9MappingSnapshot.Parse(Output(wrong));},"string coerced to HID usage");
        wrong=Document(wire);((object[])wrong["buttons"])[3]=null;Reject(delegate{G9MappingSnapshot.Parse(Output(wrong));},"null row");
        wrong=Document(wire);wrong.Remove("profile");Reject(delegate{G9MappingSnapshot.Parse(Output(wrong));},"missing profile member");
        string duplicate=Json.Serialize(Document(wire)).Replace("\"profile\":1","\"profile\":2,\"profile\":1");Reject(delegate{G9MappingSnapshot.Parse("MAPPING_JSON "+duplicate);},"duplicate profile member");
        duplicate=Json.Serialize(Document(wire)).Replace("\"profile\":1","\"profile\":1,\"prof\\u0069le\":1");Reject(delegate{G9MappingSnapshot.Parse("MAPPING_JSON "+duplicate);},"escaped duplicate member");
        var unknown=(byte[])wire.Clone();unknown[27]=0xf0;unknown[28]=0xee;unknown[29]=0xff;Checksum(unknown);
        var reserved=G9MappingSnapshot.Parse(Output(Document(unknown)));Check(reserved.Keys[27]==0xf0&&reserved.Keys[28]==0xee&&reserved.Keys[29]==0xff,"unsupported reserved-slot bytes survive strict parsing without remapping");
        parsed.RequireMatches(wire);count++;Reject(delegate{parsed.RequireMatches(unknown);},"post-write unrelated slot changes are not reported as success");
        byte[] another=(byte[])wire.Clone();another[14]=0x26;Checksum(another);Reject(delegate{parsed.RequireMatches(another);},"post-write target usage mismatch");
    }
    static T Field<T>(object target,string name){return (T)target.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(target);}
    static void SetSnapshot(G9ButtonEditor editor,byte[] bytes){typeof(G9ButtonEditor).GetField("snapshot",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(editor,G9MappingSnapshot.Parse(Output(Document(bytes))));typeof(G9ButtonEditor).GetMethod("FillButtons",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(editor,null);}
    static void SelectIndex(G9ButtonEditor editor,int index){var list=Field<ListBox>(editor,"buttons");for(int i=0;i<list.Items.Count;i++){object item=list.Items[i];var definition=(ButtonDefinition)item.GetType().GetField("Definition",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(item);if(definition.Index==index){list.SelectedIndex=i;return;}}throw new Exception("Missing visible index "+index);}
    static void SelectionRegression(){
        int calls=0;
        using(var editor=new G9ButtonEditor(delegate(string command){calls++;throw new Exception("No device calls permitted in editor fixtures");},delegate{})) {
            editor.BindingContext=new BindingContext();
            byte[] data=Bytes(Captured);data[6]=0x11;data[7]=0;data[8]=4;data[14]=0x80;Checksum(data);SetSnapshot(editor,data);
            var keyCombo=Field<ComboBox>(editor,"keys");var apply=Field<Button>(editor,"apply");
            SelectIndex(editor,1);Check(SelectedKey(editor).Usage==4&&apply.Enabled,"known keyboard assignment is represented exactly");
            SelectIndex(editor,3);Check(SelectedKey(editor)==null&&!apply.Enabled&&keyCombo.Text.Contains("80"),"unknown keyboard usage shows an explicit placeholder without inheriting the prior key");
            Check(Field<CheckBox[]>(editor,"modifiers")[0].Checked==false,"unknown keyboard keeps its actual modifier mask");
            Field<CheckBox[]>(editor,"modifiers")[0].Checked=true;Check(SelectedKey(editor)==null&&!apply.Enabled,"changing a modifier cannot turn an unknown key into a modifier-only binding");
            byte[] unsupported=(byte[])data.Clone();unsupported[12]=0xfe;unsupported[13]=0xa5;unsupported[14]=0x5a;Checksum(unsupported);SetSnapshot(editor,unsupported);SelectIndex(editor,3);
            Check(Field<ComboBox>(editor,"actions").SelectedIndex==-1&&!apply.Enabled,"unknown action never inherits a prior selected action");
            var source=Field<G9MappingSnapshot>(editor,"snapshot");Check(source.Keys[12]==0xfe&&source.Keys[13]==0xa5&&source.Keys[14]==0x5a,"rendering unsupported action leaves its bytes intact");
            byte[] modifierOnly=Bytes(Captured);modifierOnly[13]=1;modifierOnly[14]=0;Checksum(modifierOnly);SetSnapshot(editor,modifierOnly);SelectIndex(editor,3);
            Check(SelectedKey(editor).Usage==0&&Field<CheckBox[]>(editor,"modifiers")[0].Checked&&apply.Enabled,"modifier-only current binding is represented without substituting zero");
            byte[] invalidMacro=Bytes(Captured);invalidMacro[12]=18;invalidMacro[13]=0;invalidMacro[14]=0;Checksum(invalidMacro);SetSnapshot(editor,invalidMacro);SelectIndex(editor,3);
            Check(Field<ComboBox>(editor,"actions").SelectedIndex==-1&&!apply.Enabled,"unsupported macro slot zero is not silently clamped to slot one");
            Check(calls==0,"constructing and inspecting the editor invokes no device command");
            editor.InvalidateProfile();Check(Field<G9MappingSnapshot>(editor,"snapshot")==null&&!apply.Enabled,"profile switch invalidates the prior table and blocks writes");
        }
    }
    static KeyboardUsage SelectedKey(G9ButtonEditor editor){return (KeyboardUsage)typeof(G9ButtonEditor).GetMethod("SelectedKey",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(editor,null);}
    [STAThread] static int Main(){try{Parsing();SelectionRegression();Console.WriteLine("PASS "+count+" button-editor snapshot and selection checks; no HID/device APIs called.");return 0;}catch(Exception error){Console.Error.WriteLine(error);return 1;}}
}
