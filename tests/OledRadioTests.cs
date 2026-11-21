using System;
using System.Collections.Generic;
using System.IO;
using Furycube.G9.Oled;

internal static class OledRadioTests {
    sealed class Session : IOledDeviceSession, IOledRuntimeStatusDeviceSession {
        internal ushort Vendor = 0x1D57, Product = 0xFA60;
        internal char Revision = '9';
        internal int Versions, Writes, StatusReads, FailWrite;
        internal Action DuringVersion;
        internal byte[] Status = StatusFrame();
        internal readonly List<byte[]> Reports = new List<byte[]>();
        public ushort VendorId { get { return Vendor; } }
        public ushort ProductId { get { return Product; } }
        public byte[] ReadOledFirmware() { Versions++; if(DuringVersion!=null)DuringVersion(); return new byte[]{11,8,1,1,8,(byte)'O',(byte)'L',(byte)Revision}; }
        public void SendOledFeature(byte[] report) { Writes++; if(Writes==FailWrite)throw new IOException("radio transport failure");Reports.Add((byte[])report.Clone()); }
        public byte[] ReadOledRuntimeStatus() { StatusReads++; return (byte[])Status.Clone(); }
    }
    static int checks;
    static void Check(bool condition,string name) { if(!condition)throw new Exception("FAIL "+name);checks++; }
    static void Reject(Action action,string name) { try{action();}catch(NotSupportedException){checks++;return;}catch(InvalidOperationException){checks++;return;}catch(FormatException){checks++;return;}catch(InvalidDataException){checks++;return;}catch(IOException){checks++;return;}throw new Exception("FAIL accepted "+name); }
    static byte[] StatusFrame() { var data=new byte[64];data[0]=0x22;data[1]=64;data[2]=71;data[3]=83;data[4]=4;data[5]=1;data[6]=1;data[7]=2;data[8]=0x40;data[9]=6;ushort crc=OledProtocol.Crc16CcittFalse(data,0,62);data[62]=(byte)crc;data[63]=(byte)(crc>>8);return data; }
    static int Main() { try {
        var frame=new byte[480];for(int i=0;i<480;i++)frame[i]=(byte)(i&(i<360?255:15));
        foreach(char revision in new[]{'2','3','4','5','6','7','8','0'}) {
            var old=new Session{Revision=revision};var transport=new NativeOledTransport(old);var sender=new OledSender(transport,delegate(int ms){});
            Reject(delegate{sender.Discover();},"old radio firmware rejected "+revision);
            Reject(delegate{sender.WriteFrame(frame);},"no writes after rejected radio discovery "+revision);
            Reject(delegate{transport.ReadRuntimeStatus();},"no status after rejected radio discovery "+revision);
            Check(old.Versions==1&&old.Writes==0&&old.StatusReads==0,"only existing safe version query reached old radio "+revision);
        }
        var device=new Session();var adapter=new NativeOledTransport(device);var delays=new List<int>();var upload=new OledSender(adapter,delays.Add);
        Reject(delegate{adapter.SendFeature(OledShortProtocol.BuildWriteChunk(0,0,new byte[40]));},"radio cannot write before discovery");
        var caps=upload.Discover();Check(caps.Extension=="OL9"&&caps.SupportsRadioWrites&&caps.SupportsShortWrites&&caps.SupportsRuntimeStatus,"OL9 capabilities on exact receiver session");
        Check(device.Versions==1&&device.Writes==0,"discovery itself has no image side effect");
        Check(upload.ReadRuntimeStatusFrame().Length==64&&device.StatusReads==1,"OL9 uses strict existing status codec on the host");
        upload.WriteFrame(frame);Check(device.Writes==12&&delays.Count==12&&delays.TrueForAll(delegate(int value){return value==500;}),"receiver paces twelve chunks including a gap after the last chunk");
        var reconstructed=new byte[480];for(int n=0;n<12;n++){byte[] p=device.Reports[n];OledShortProtocol.ValidateWriteChunk(p);Check(p[7]==n/3&&p[8]==(n%3)*40,"ordered receiver chunk "+n);Buffer.BlockCopy(p,12,reconstructed,n*40,40);}
        Check(Convert.ToBase64String(frame)==Convert.ToBase64String(reconstructed),"receiver transport preserves all480 bytes");
        var guardedDevice=new Session();
        var guardedDelays=new List<int>();
        var guarded=new OledSender(new NativeOledTransport(guardedDevice),guardedDelays.Add);
        guarded.Discover();
        int gateCalls=0;
        var deferred=guarded.WriteFrame(frame,delegate{return ++gateCalls<=2;});
        Check(deferred.Deferred&&guardedDevice.Writes==2&&gateCalls==3,"radio input defers before the next fragment");
        guardedDelays.Clear();
        var sent=guarded.WriteFrame(frame,delegate{return true;});
        Check(!sent.Deferred&&sent.FeatureReportsSent==12&&guardedDelays.Count==12&&
            guardedDelays.TrueForAll(delegate(int value){return value==250;}),
            "guarded radio sends a complete frame with a 250 ms gap after every fragment");
        device.Product=0x2148;Reject(delegate{upload.WriteFrame(frame);},"stale receiver sender cannot become wired sender");Check(device.Writes==12,"changed identity sends no further chunk");
        Reject(delegate{adapter.ReadRuntimeStatus();},"changed native receiver identity cannot read status");Check(device.StatusReads==1,"status identity guard precedes native read");
        var changing=new Session();changing.DuringVersion=delegate{changing.Product=0x2148;};var changed=new OledSender(new NativeOledTransport(changing));Reject(delegate{changed.Discover();},"identity change during discovery");Check(changing.Writes==0&&changing.StatusReads==0,"changing discovery produces no custom traffic");
        var failure=new Session{FailWrite=4};var broken=new OledSender(new NativeOledTransport(failure),delegate(int ms){});broken.Discover();Reject(delegate{broken.WriteFrame(frame);},"radio failure stops at failed chunk");Check(failure.Writes==4&&failure.Reports.Count==3,"no automatic radio retry or continuation");Reject(delegate{broken.WriteFrame(frame);},"failed session requires fresh discovery");Check(failure.Writes==4,"repeat call after failure performs no write");
        var corrupt=new Session();corrupt.Status[8]^=1;var invalid=new OledSender(new NativeOledTransport(corrupt));invalid.Discover();Reject(delegate{invalid.ReadRuntimeStatusFrame();},"bad radio status CRC invalidates discovery");Reject(delegate{invalid.WriteFrame(frame);},"bad status forbids later image traffic");Check(corrupt.Writes==0,"bad status sends no image");
        var boot=new Session{Vendor=0xA745,Product=0x0033};Reject(delegate{new OledSender(new NativeOledTransport(boot)).Discover();},"bootloader rejected before version query");Check(boot.Versions==0,"no bootloader traffic");
        var wired=new Session{Product=0x2148,Revision='4'};var wiredDelays=new List<int>();var legacy=new OledSender(new NativeOledTransport(wired),wiredDelays.Add);legacy.Discover();legacy.WriteFrame(frame);Check(wired.Writes==12&&wiredDelays.TrueForAll(delegate(int value){return value==25;}),"OL4 wired path retains25ms pacing");
        Console.WriteLine("PASS "+checks+" OL9 receiver capability, identity, frame, status and failure checks; fake sessions only.");return 0;
    } catch(Exception error){Console.Error.WriteLine(error);return 1;} }
}
