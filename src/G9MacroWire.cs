// Pure, bounded stock C9 macro encoder. No HID/COM, input injection, keyboard
// layout API, vendor execution or physical-flash reconstruction.
using System;
using System.Collections.Generic;
using System.IO;

namespace Furycube.G9.Macros {
    public sealed class MacroWirePacket {
        readonly byte[] frame;
        public int Slot {get;private set;}
        public int EventCount {get;private set;}
        public int EncodedDurationMilliseconds {get;private set;}
        public int ImplicitMinimumDelayCount {get;private set;}
        internal MacroWirePacket(int slot,byte[] bytes,int events,int duration,int minimums) {
            Slot=slot;frame=(byte[])bytes.Clone();EventCount=events;
            EncodedDurationMilliseconds=duration;ImplicitMinimumDelayCount=minimums;
        }
        // Logical SET09 bytes, NOT a 128-byte flash slot or a transport envelope.
        public byte[] GetLogicalFrameCopy(){return (byte[])frame.Clone();}
    }

    public static class MacroWireCodec {
        public const int LogicalFrameBytes=111,MaximumEvents=48,MaximumEventDelayMilliseconds=127,MinimumEventDelayMilliseconds=1;
        // Stock x86 FURYCUBE.exe VA41E5B0..41E9DD (evidence/macro-serializer.asm):
        // 09 6F slot type RGB[3] repeats count, then delay/usage pairs from offset9.
        // Down kind1 => delay; up kind2 => delay|80. Sum offsets3..108 =>109/110 BE.
        // Type00 / repeat01 / whiteRGB are the original captured profile defaults.
        // Firmware 338B6..339D2 executes the event THEN waits; 3374C adapts RF timing.
        // Delays therefore attach to the PRECEDING event. Omitted delays use the
        // stock serializer's explicit minimum1ms, counted in the returned packet.
        // Only a SINGLE slot is emitted: no FE continuation or long-delay03 records.
        // Transport, actual timing/output and the stored slot's hidden20 bytes are
        // separate qualifications. Names stay in the host library (no name field).
        public static MacroWirePacket Encode(MacroDefinition macro,int slot) {
            if(macro==null)throw new ArgumentNullException("macro");
            if(slot<1||slot>32)throw new ArgumentOutOfRangeException("slot","Macro slot must be 1..32.");
            macro.Validate();
            if(macro.RepeatMode!=MacroRepeatMode.Counted||macro.RepeatCount!=1)
                throw new InvalidDataException("Device upload currently supports one counted execution only; toggle, hold and other repeat counts are not encoded.");
            var usages=new List<byte>();var ups=new List<bool>();var delays=new List<int>();
            var held=new HashSet<byte>();int heldOrdinary=0;
            foreach(var step in macro.Steps) {
                if(step.Kind==MacroStepKind.Delay) {
                    if(delays.Count==0)throw new InvalidDataException("A leading delay is not supported by this event-first device format.");
                    if(step.DelayMilliseconds<1)throw new InvalidDataException("Explicit device delays must be at least 1 ms.");
                    int last=delays.Count-1;int total=delays[last]+step.DelayMilliseconds;
                    if(total>MaximumEventDelayMilliseconds)throw new InvalidDataException("Delay after an event exceeds 127 ms; extended delay records are not enabled.");
                    delays[last]=total;continue;
                }
                bool keyboard=step.Kind==MacroStepKind.KeyDown||step.Kind==MacroStepKind.KeyUp;
                bool mouse=step.Kind==MacroStepKind.MouseDown||step.Kind==MacroStepKind.MouseUp;
                if(!keyboard&&!mouse)throw new InvalidDataException("Device upload supports keyboard and five mouse-button press/release events; movement and wheel steps are not encoded.");
                byte usage=keyboard?GetPhysicalUsUsage(step.VirtualKey):MouseUsage(step.Button);
                bool up=step.Kind==MacroStepKind.KeyUp||step.Kind==MacroStepKind.MouseUp;
                bool ordinary=keyboard&&usage<0xE0;
                if(up) {
                    if(!held.Remove(usage))throw new InvalidDataException("Release without matching press in the encoded macro.");
                    if(ordinary)heldOrdinary--;
                } else {
                    if(!held.Add(usage))throw new InvalidDataException("Repeated press without release (including equivalent USB usages).");
                    if(ordinary&&++heldOrdinary>6)throw new InvalidDataException("At most six ordinary keyboard keys may be held together.");
                }
                if(usages.Count>=MaximumEvents)throw new InvalidDataException("A device macro supports at most 48 balanced events in one slot; continuation slots are not enabled.");
                usages.Add(usage);ups.Add(up);delays.Add(0);
            }
            if(usages.Count==0)throw new InvalidDataException("The device macro has no input events.");
            if(held.Count!=0)throw new InvalidDataException("Every encoded key and mouse press must have a matching release.");
            var frame=new byte[LogicalFrameBytes];frame[0]=9;frame[1]=111;frame[2]=(byte)slot;
            frame[4]=255;frame[5]=255;frame[6]=255;frame[7]=1;frame[8]=(byte)usages.Count;
            int duration=0,minimums=0;
            for(int i=0;i<usages.Count;i++) {
                int delay=delays[i];if(delay==0){delay=MinimumEventDelayMilliseconds;minimums++;}
                duration+=delay;frame[9+i*2]=(byte)(delay|(ups[i]?0x80:0));frame[10+i*2]=usages[i];
            }
            int sum=0;for(int i=3;i<109;i++)sum+=frame[i];frame[109]=(byte)(sum>>8);frame[110]=(byte)sum;
            return new MacroWirePacket(slot,frame,usages.Count,duration,minimums);
        }
        static byte MouseUsage(MacroMouseButton button) {
            // Stock macro executor33918..33932 uses usage-F1 as the button bit:
            // 1 left,2 right,4 middle,8 back,10 forward; not VK mouse codes.
            switch(button){case MacroMouseButton.Left:return 0xF1;case MacroMouseButton.Right:return 0xF2;
            case MacroMouseButton.Middle:return 0xF3;case MacroMouseButton.Back:return 0xF4;case MacroMouseButton.Forward:return 0xF5;}
            throw new InvalidDataException("Unsupported mouse button.");
        }
        // Fixed physical US mapping transcribed from original VA46D070 table
        // (112 records, VK/usage/scancode). Unlike native403160, this NEVER calls
        // MapVirtualKeyW. Characters depend on the receiving OS keyboard layout.
        // Generic Shift/Ctrl/Alt, ambiguous Separator/OEM102 and OEM pseudo-VKs
        // are rejected; callers must choose explicit left/right modifier VKs.
        public static byte GetPhysicalUsUsage(int virtualKey) {
            if(virtualKey>=0x41&&virtualKey<=0x5A)return (byte)(virtualKey-0x41+4);
            if(virtualKey>=0x31&&virtualKey<=0x39)return (byte)(virtualKey-0x31+0x1E);
            if(virtualKey>=0x70&&virtualKey<=0x7B)return (byte)(virtualKey-0x70+0x3A);
            if(virtualKey>=0x61&&virtualKey<=0x69)return (byte)(virtualKey-0x61+0x59);
            switch(virtualKey) {
            case 0x30:return 0x27;case 0x0D:return 0x28;case 0x1B:return 0x29;case 0x08:return 0x2A;case 0x09:return 0x2B;case 0x20:return 0x2C;
            case 0xBD:return 0x2D;case 0xBB:return 0x2E;case 0xDB:return 0x2F;case 0xDD:return 0x30;case 0xDC:return 0x31;
            case 0xBA:return 0x33;case 0xDE:return 0x34;case 0xC0:return 0x35;case 0xBC:return 0x36;case 0xBE:return 0x37;case 0xBF:return 0x38;case 0x14:return 0x39;
            case 0x2C:return 0x46;case 0x91:return 0x47;case 0x13:return 0x48;case 0x2D:return 0x49;case 0x24:return 0x4A;case 0x21:return 0x4B;
            case 0x2E:return 0x4C;case 0x23:return 0x4D;case 0x22:return 0x4E;case 0x27:return 0x4F;case 0x25:return 0x50;case 0x28:return 0x51;case 0x26:return 0x52;
            case 0x90:return 0x53;case 0x6F:return 0x54;case 0x6A:return 0x55;case 0x6D:return 0x56;case 0x6B:return 0x57;case 0x60:return 0x62;case 0x6E:return 0x63;case 0x5D:return 0x65;
            case 0xA2:return 0xE0;case 0xA0:return 0xE1;case 0xA4:return 0xE2;case 0x5B:return 0xE3;
            case 0xA3:return 0xE4;case 0xA1:return 0xE5;case 0xA5:return 0xE6;case 0x5C:return 0xE7;
            }
            throw new InvalidDataException("VK 0x"+virtualKey.ToString("X2")+" has no supported unambiguous physical US device mapping; choose an explicit key/modifier.");
        }
    }
}
