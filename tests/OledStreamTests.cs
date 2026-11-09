using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Furycube.G9.Oled;

internal static class OledStreamTests {
    sealed class FakeTransport : IOledTransport, IOledRuntimeStatusTransport {
        internal int Reads, Attempts, FailAt;
        internal ushort Pid = 0x2148;
        internal byte[] Version = new byte[] { 11, 5, 1, 1, 8, (byte)'O', (byte)'L', (byte)'2' };
        internal readonly List<byte[]> Writes = new List<byte[]>();
        internal int StatusReads;
        internal byte[] StatusReply = Convert.FromBase64String("IoRHUwMBAgOADAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAADcn");
        public ushort VendorId { get { return 0x1D57; } }
        public ushort ProductId { get { return Pid; } }
        public byte[] ReadFirmwareVersion() { Reads++; return Version; }
        public byte[] ReadRuntimeStatus() { StatusReads++; return (byte[])StatusReply.Clone(); }
        public void SendFeature(byte[] report) {
            Attempts++;
            if (FailAt == Attempts) throw new IOException("Simulated disconnected original handle");
            Writes.Add((byte[])report.Clone());
        }
    }
    sealed class TrackedReader : StringReader {
        internal int Consumed;
        internal TrackedReader(string value) : base(value) { }
        public override int Read() { int value = base.Read(); if (value >= 0) Consumed++; return value; }
    }
    static int checks;
    static void Check(bool value, string name) { if (!value) throw new Exception("FAIL " + name); checks++; }
    static string Frame(byte first, byte last) {
        byte[] pixels = new byte[480]; pixels[0] = first; pixels[479] = last;
        return "FRAME " + Convert.ToBase64String(pixels);
    }
    static void Reject(string command, string name) {
        var fake = new FakeTransport(); var output = new StringWriter();
        string input = command + "\n" + Frame(2, 0) + "\n";
        var reader = new TrackedReader(input);
        try { OledStreaming.Run(reader, output, fake, delegate(int value) { }); }
        catch (InvalidDataException) {
            Check(fake.Reads == 1 && fake.Writes.Count == 0, name + " rejected before any SET");
            Check(output.ToString().Trim() == "OLED_STREAM_READY", name + " has no false frame ACK");
            Check(reader.Consumed <= command.Length + 1, name + " leaves later frame unread");
            return;
        }
        throw new Exception("FAIL accepted " + name);
    }
    static MemoryStream Utf8Bytes(string text, bool bom) {
        byte[] content = new UTF8Encoding(false, true).GetBytes(text);
        byte[] prefix = bom ? new byte[] { 0xEF, 0xBB, 0xBF } : new byte[0];
        var stream = new MemoryStream(); stream.Write(prefix, 0, prefix.Length); stream.Write(content, 0, content.Length); stream.Position = 0; return stream;
    }
    static void CheckByteTransport() {
        foreach (bool bom in new[] { false, true }) {
            var fake = new FakeTransport(); var output = new StringWriter();
            using (var bytes = Utf8Bytes(Frame(0x42, 0xF) + "\nSTOP\n", bom)) {
                int result = OledStreaming.RunUtf8(bytes, output, fake, delegate(int value) { });
                Check(result == 0 && fake.Reads == 1 && fake.Writes.Count == 4, "byte transport accepts " + (bom ? "one leading UTF-8 BOM" : "ASCII without BOM"));
                Check(fake.Writes[0][10] == 0x42 && fake.Writes[3][129] == 0xF, "byte transport preserves frame pixels");
                Check(output.ToString().Replace("\r\n", "\n") == "OLED_STREAM_READY\nOLED_FRAME_SENT 1\n", "byte transport confirms exactly one frame");
                Check(bytes.CanRead, "byte transport leaves caller's stream open");
            }
        }
        foreach (string text in new[] { "\uFEFF" + Frame(1, 0), Frame(1, 0) + "\n\uFEFF" + Frame(2, 0) }) {
            var fake = new FakeTransport(); var output = new StringWriter(); bool later = text[0] != '\uFEFF';
            using (var bytes = Utf8Bytes(text + "\n" + Frame(3, 0), true)) {
                try { OledStreaming.RunUtf8(bytes, output, fake, delegate(int value) { }); throw new Exception("FAIL accepted repeated/interior BOM"); }
                catch (InvalidDataException) { checks++; }
                Check(fake.Writes.Count == (later ? 4 : 0), "only the initial BOM is stripped; repeated/interior BOM stops before that frame");
                Check(!output.ToString().Contains("OLED_FRAME_SENT 2"), "interior BOM cannot produce a false second ACK");
            }
        }
        var invalid = new FakeTransport();
        using (var bytes = new MemoryStream(new byte[] { 0xC3, 0x28, 10 })) {
            try { OledStreaming.RunUtf8(bytes, new StringWriter(), invalid, delegate(int value) { }); throw new Exception("FAIL accepted malformed UTF-8"); }
            catch (DecoderFallbackException) { checks++; }
            Check(invalid.Writes.Count == 0, "malformed UTF-8 rejected before feature writes");
        }
    }
    static int Main() { try {
        CheckByteTransport();
        var oldStatus = new FakeTransport(); var oldStatusOutput = new StringWriter();
        OledStreaming.Run(new StringReader("STATUS\nSTATUS\nSTOP\n"),oldStatusOutput,oldStatus,delegate(int value){});
        Check(oldStatus.Reads==1&&oldStatus.StatusReads==0&&oldStatus.Writes.Count==0,"OL2 STATUS never invokes GET22 or any image report");
        Check(oldStatusOutput.ToString().Replace("\r\n","\n")=="OLED_STREAM_READY\nOLED_STATUS_UNSUPPORTED\nOLED_STATUS_SENT 1\nOLED_STATUS_UNSUPPORTED\nOLED_STATUS_SENT 2\n",
            "OL2 unsupported result precedes independent status ACKs");
        var modern=new FakeTransport();modern.Version[7]=(byte)'3';var modernOutput=new StringWriter();
        OledStreaming.Run(new StringReader("STATUS\n"+Frame(1,0)+"\nSTATUS\n"+Frame(2,0)+"\nSTOP\n"),modernOutput,modern,delegate(int value){});
        string expectedModern="OLED_STREAM_READY\nOLED_STATUS_DATA "+Convert.ToBase64String(modern.StatusReply)+"\nOLED_STATUS_SENT 1\nOLED_FRAME_SENT 1\nOLED_STATUS_DATA "+Convert.ToBase64String(modern.StatusReply)+"\nOLED_STATUS_SENT 2\nOLED_FRAME_SENT 2\n";
        Check(modernOutput.ToString().Replace("\r\n","\n")==expectedModern,"OL3 status DATA precedes ACK with separate frame/status counters");
        Check(modern.Reads==1&&modern.StatusReads==2&&modern.Writes.Count==8,"OL3 same transport and one discovery across STATUS/FRAME interleaving");
        Check(modern.Writes[0][9]==1&&modern.Writes[4][9]==0,"status queries do not reinitialize the next frame");
        var corruptStatus=new FakeTransport();corruptStatus.Version[7]=(byte)'3';corruptStatus.StatusReply[130]^=1;
        var corruptOutput=new StringWriter();var corruptInput=new TrackedReader("STATUS\n"+Frame(1,0)+"\n");
        try{OledStreaming.Run(corruptInput,corruptOutput,corruptStatus,delegate(int value){});throw new Exception("FAIL accepted corrupt status");}
        catch(FormatException){checks++;}
        Check(corruptStatus.StatusReads==1&&corruptStatus.Writes.Count==0&&corruptInput.Consumed==7,"malformed status stops without retry or consuming following frame");
        Check(corruptOutput.ToString().Replace("\r\n","\n")=="OLED_STREAM_READY\n","malformed status has no DATA or false ACK");
        Reject("STATUS ","status trailing whitespace");
        Reject("STATUS extra","status unexpected arguments");
        var transport = new FakeTransport(); var output = new StringWriter(); var delays = new List<int>();
        int result = OledStreaming.Run(new StringReader(Frame(0x81, 0xF) + "\r\n" + Frame(0x42, 1) + "\nSTOP\n" + Frame(3, 0)),
            output, transport, delegate(int value) { delays.Add(value); });
        Check(result == 0 && transport.Reads == 1 && transport.Writes.Count == 8, "same transport and one discovery for two complete frames");
        Check(output.ToString().Replace("\r\n", "\n") == "OLED_STREAM_READY\nOLED_FRAME_SENT 1\nOLED_FRAME_SENT 2\n", "strict ready and frame ACK protocol");
        Check(delays.Count == 6 && delays.TrueForAll(delegate(int value) { return value == 25; }), "25ms pacing between four pages for each frame");
        for (int i = 0; i < 8; i++) {
            OledPage page = OledProtocol.DecodeWritePage(transport.Writes[i], i % 4);
            Check(page.InitializeDisplay == (i == 0), "INIT only once in complete stream, report " + i);
        }
        Check(transport.Writes[0][10] == 0x81 && transport.Writes[4][10] == 0x42 && transport.Writes[3][129] == 0xF, "both frame contents preserved including visible last-page bits");
        Reject("FRAME !!!", "short/invalid Base64");
        string malformed = Frame(1, 0);
        Reject(malformed.Substring(0, 6) + "!" + malformed.Substring(7), "non-Base64 character");
        Reject(Frame(1, 0) + " ", "trailing whitespace");
        Reject("FRAME " + Convert.ToBase64String(new byte[479]), "wrong decoded byte count/padding");
        Reject(Frame(1, 0x10), "invisible last-page pixels");
        Reject("UNKNOWN", "unknown command");
        Reject("", "empty line");
        Reject(new string('X', 1025), "bounded oversized line");
        var stopTransport = new FakeTransport(); var stopReader = new TrackedReader("STOP\n" + Frame(1, 0));
        OledStreaming.Run(stopReader, new StringWriter(), stopTransport, delegate(int value) { });
        Check(stopTransport.Writes.Count == 0 && stopReader.Consumed == 5, "STOP consumes no following input");
        var eofTransport = new FakeTransport();
        OledStreaming.Run(new StringReader(Frame(1, 0)), new StringWriter(), eofTransport, delegate(int value) { });
        Check(eofTransport.Writes.Count == 4, "EOF after complete final line accepted");
        var broken = new FakeTransport { FailAt = 5 }; var failureOutput = new StringWriter();
        string one = Frame(1, 0) + "\n", two = Frame(2, 0) + "\n";
        var failureInput = new TrackedReader(one + two + Frame(3, 0));
        try { OledStreaming.Run(failureInput, failureOutput, broken, delegate(int value) { }); throw new Exception("FAIL missing disconnect"); }
        catch (IOException) { checks++; }
        Check(broken.Reads == 1 && broken.Attempts == 5 && broken.Writes.Count == 4, "disconnect stops immediately without reconnect/discovery/retry");
        Check(failureInput.Consumed == one.Length + two.Length, "disconnect leaves later frame unread");
        Check(failureOutput.ToString().Replace("\r\n", "\n") == "OLED_STREAM_READY\nOLED_FRAME_SENT 1\n", "failed second frame is not acknowledged");
        var stock = new FakeTransport { Version = new byte[] { 11, 5, 1, 1, 8, 0, 0, 0 } }; var stockOutput = new StringWriter();
        try { OledStreaming.Run(new StringReader(Frame(1, 0)), stockOutput, stock, delegate(int value) { }); throw new Exception("FAIL accepted stock"); }
        catch (InvalidDataException) { checks++; }
        Check(stock.Writes.Count == 0 && stockOutput.ToString() == "", "stock discovery has no READY or SET22");
        Console.WriteLine("PASS " + checks + " fake OLED stream checks; no USB, no physical display claim");
        return 0;
    } catch (Exception error) { Console.Error.WriteLine(error); return 1; } }
}
