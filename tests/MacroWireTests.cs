using System;
using System.IO;
using Furycube.G9.Macros;

internal static class MacroWireTests {
    static int checks;
    static void Check(bool value,string message){checks++;if(!value)throw new Exception(message);}
    static void Reject(Action action,string message){checks++;try{action();}catch(InvalidDataException){return;}throw new Exception("Expected rejection: "+message);}
    static byte[] Hex(string text){var bytes=new byte[text.Length/2];for(int i=0;i<bytes.Length;i++)bytes[i]=Convert.ToByte(text.Substring(i*2,2),16);return bytes;}
    static void Equal(byte[] actual,string hex,string message){byte[] expected=Hex(hex);Check(actual.Length==expected.Length,message+" length");for(int i=0;i<expected.Length;i++)Check(actual[i]==expected[i],message+" byte "+i);}
    static MacroStep Key(bool down,int vk){return new MacroStep{Kind=down?MacroStepKind.KeyDown:MacroStepKind.KeyUp,VirtualKey=vk};}
    static MacroStep Wait(int ms){return new MacroStep{Kind=MacroStepKind.Delay,DelayMilliseconds=ms};}
    static MacroDefinition Zero(int downDelay,int upDelay){var m=new MacroDefinition{Name="zero"};m.Steps.Add(Key(true,48));if(downDelay>=0)m.Steps.Add(Wait(downDelay));m.Steps.Add(Key(false,48));if(upDelay>=0)m.Steps.Add(Wait(upDelay));return m;}
    static byte[] Encode(MacroDefinition m){return MacroWireCodec.Encode(m,13).GetLogicalFrameCopy();}
    // Full fixtures from original x86 serializer41E5B0, executed under Unicorn
    // with only memory/CRT/HID-send stubs (tools/emulate_serializer.py).
    // First fixture is existing evidence/vendor-macro.bin, not a device readback.
    // Additional fixtures modify original profile event fields then run that SAME
    // original machine code. They do not derive expected bytes with this encoder.
    static void OriginalFixtures() {
        Equal(Encode(Zero(10,1)),"096f0d00ffffff01020a27812700000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000003d9","stock zero10/1");
        Equal(Encode(Zero(50,50)),"096f0d00ffffff01023227b2270000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000432","original serializer zero50/50");
        Equal(Encode(Zero(50,-1)),"096f0d00ffffff0102322781270000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000401","zero50 plus explicit hardware minimum");
        var m=new MacroDefinition{Name="Ctrl A"};m.Steps.AddRange(new[]{Key(true,0xA2),Wait(1),Key(true,65),Wait(50),Key(false,65),Wait(2),Key(false,0xA2),Wait(1)});
        Equal(Encode(m),"096f0d00ffffff010401e03204820481e000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000600","original serializer explicit leftCtrl A");
        m=new MacroDefinition{Name="mouse"};foreach(var button in new[]{MacroMouseButton.Left,MacroMouseButton.Right,MacroMouseButton.Middle,MacroMouseButton.Back,MacroMouseButton.Forward}) {
            m.Steps.Add(new MacroStep{Kind=MacroStepKind.MouseDown,Button=button});m.Steps.Add(Wait(10));m.Steps.Add(new MacroStep{Kind=MacroStepKind.MouseUp,Button=button});m.Steps.Add(Wait(10));
        }
        Equal(Encode(m),"096f0d00ffffff010a0af18af10af28af20af38af30af48af40af58af500000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000f6a","original serializer five button usages");
    }
    static void TimingAndEnvelope() {
        var packet=MacroWireCodec.Encode(Zero(50,-1),13);Check(packet.Slot==13&&packet.EventCount==2,"slot and events");Check(packet.EncodedDurationMilliseconds==51&&packet.ImplicitMinimumDelayCount==1,"50ms held plus1ms release interval exposed");
        byte[] frame=packet.GetLogicalFrameCopy();frame[9]=99;Check(packet.GetLogicalFrameCopy()[9]==50,"packet bytes immutable");
        var split=Zero(20,1);split.Steps.Insert(2,Wait(30));Check(Encode(split)[9]==50,"consecutive delays sum after preceding event");
        Check(Encode(Zero(127,127))[9]==127&&Encode(Zero(127,127))[11]==255,"full short delay range and release bit");
        var source=Zero(50,50);var before=source.Clone();Encode(source);Check(source.Steps.Count==before.Steps.Count&&source.Steps[1].DelayMilliseconds==50,"source untouched");
        byte[] first=MacroWireCodec.Encode(source,1).GetLogicalFrameCopy(),last=MacroWireCodec.Encode(source,32).GetLogicalFrameCopy();
        for(int i=0;i<111;i++)Check(i==2?first[i]==1&&last[i]==32:first[i]==last[i],"slot header excluded from sum "+i);
        bool invalid=false;try{MacroWireCodec.Encode(source,0);}catch(ArgumentOutOfRangeException){invalid=true;}Check(invalid,"slot zero rejected");invalid=false;try{MacroWireCodec.Encode(source,33);}catch(ArgumentOutOfRangeException){invalid=true;}Check(invalid,"slot33 rejected");
    }
    static void Mapping() {
        // Independent source-table VK/USB pairs at VA46D070 (layout API bypassed).
        string pairs="4104 5A1D 311E 3926 3027 0D28 1B29 082A 092B 202C BD2D BB2E DB2F DD30 DC31 BA33 DE34 C035 BC36 BE37 BF38 1439 703A 7B45 2C46 9147 1348 2D49 244A 214B 2E4C 234D 224E 274F 2550 2851 2652 9053 6F54 6A55 6D56 6B57 6159 675F 6062 6E63 5D65 A2E0 A0E1 A4E2 5BE3 A3E4 A1E5 A5E6 5CE7";
        foreach(string pair in pairs.Split(' '))Check(MacroWireCodec.GetPhysicalUsUsage(Convert.ToInt32(pair.Substring(0,2),16))==Convert.ToByte(pair.Substring(2),16),"original VK table "+pair);
        Check(MacroWireCodec.GetPhysicalUsUsage(0x67)==0x5F&&MacroWireCodec.GetPhysicalUsUsage(0x69)==0x61,"native keypad7/9 ordering");
        foreach(int vk in new[]{0x10,0x11,0x12,0x6C,0xDF,0xE2,0x7C,0xF0,0xFF}){int key=vk;Reject(delegate{MacroWireCodec.GetPhysicalUsUsage(key);},"unqualified or ambiguous VK "+vk);}
    }
    static void Rejections() {
        foreach(var mode in new[]{MacroRepeatMode.Toggle,MacroRepeatMode.Hold}){var m=Zero(50,1);m.RepeatMode=mode;Reject(delegate{Encode(m);},"unbounded repeat mode");}
        foreach(int count in new[]{2,255,999}){var m=Zero(50,1);m.RepeatCount=count;Reject(delegate{Encode(m);},"unqualified repeat count");}
        Reject(delegate{Encode(new MacroDefinition());},"empty macro");
        var leading=Zero(50,1);leading.Steps.Insert(0,Wait(10));Reject(delegate{Encode(leading);},"leading delay");
        Reject(delegate{Encode(Zero(0,1));},"explicit zero delay never rounded silently");Reject(delegate{Encode(Zero(128,1));},"extended delay");
        var accumulated=Zero(100,1);accumulated.Steps.Insert(2,Wait(28));Reject(delegate{Encode(accumulated);},"accumulated delay overflow");
        foreach(var step in new[]{new MacroStep{Kind=MacroStepKind.MouseMove,X=1},new MacroStep{Kind=MacroStepKind.Wheel,Delta=120}}){var m=Zero(50,1);m.Steps.Insert(0,step);Reject(delegate{Encode(m);},"unsupported movement/wheel");}
        var missing=new MacroDefinition();missing.Steps.Add(Key(true,65));Reject(delegate{Encode(missing);},"missing release");
        var orphan=new MacroDefinition();orphan.Steps.Add(Key(false,65));Reject(delegate{Encode(orphan);},"orphan release");
        var duplicate=Zero(50,1);duplicate.Steps.Insert(1,Key(true,48));Reject(delegate{Encode(duplicate);},"duplicate press");
        var chord=new MacroDefinition();for(int i=0;i<7;i++)chord.Steps.Add(Key(true,65+i));for(int i=6;i>=0;i--)chord.Steps.Add(Key(false,65+i));Reject(delegate{Encode(chord);},"seven-key rollover overflow");
        chord.Steps.RemoveAt(6);chord.Steps.RemoveAt(6);Check(MacroWireCodec.Encode(chord,13).EventCount==12,"six held ordinary keys accepted");
        var capacity=new MacroDefinition();for(int i=0;i<24;i++){capacity.Steps.Add(Key(true,48));capacity.Steps.Add(Key(false,48));}Check(MacroWireCodec.Encode(capacity,32).EventCount==48,"full bounded balanced slot");capacity.Steps.Add(Key(true,48));capacity.Steps.Add(Key(false,48));Reject(delegate{Encode(capacity);},"continuation rejected without truncation");
    }
    static int Main(){try{OriginalFixtures();TimingAndEnvelope();Mapping();Rejections();Console.WriteLine("MacroWireTests PASS "+checks);return 0;}catch(Exception ex){Console.Error.WriteLine(ex);return 1;}}
}
