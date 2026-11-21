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
    static string Partial(byte first) {return "LIVE_DELTA " + Frame(first,0).Substring(6) + "\n";}
    static FakeTransport RadioB() {return new FakeTransport { Pid=0xFA60,
        Version=new byte[] {11,5,1,1,8,(byte)'O',(byte)'L',(byte)'B'} };}
    static byte[] Pixels(byte first) { var frame = new byte[480]; frame[0] = first; return frame; }
    static byte[] Receipt(byte[] pixels) {
        ushort crc = OledProtocol.Crc16CcittFalse(pixels, 0, pixels.Length);
        return new byte[] {3, 0xC9, 0xD1, (byte)crc, (byte)(crc >> 8)};
    }
    static void CheckConfirmedRadioFrames() {
        var radio = RadioB(); radio.Version[7] = (byte)'D';
        var delays = new List<int>();
        var sender = new OledSender(radio, delays.Add); sender.Discover();
        var receipts = new RadioOledAcknowledgements();
        var stream = new ConfirmedRadioOledStream(receipts);
        var sent = new List<byte[]>();
        var first = Pixels(1); var next = Pixels(2); var newer = Pixels(3);
        long elapsed = 0;
        bool idle = true;
        int reports, confirmations; string transfer;
        Func<byte[],string> update = frame => stream.Send(sender, frame, true, () => idle, sent.Add,
            () => elapsed, out reports, out transfer, out confirmations);

        receipts.Observe(0xFA60, Receipt(first));
        Check(update(first) == OledStreamCommand.Deferred && radio.Writes.Count == 12 && delays.TrueForAll(v => v == 500),
            "full frame waits for a fresh receipt and uses the qualified radio pacing");
        receipts.Observe(0x2148, Receipt(first));
        receipts.Observe(0xFA60, Receipt(next));
        Check(update(next) == OledStreamCommand.Deferred && sent.Count == 0,
            "wrong receiver or CRC cannot advance the delta base");
        receipts.Observe(0xFA60, Receipt(first));
        Check(update(next) == OledStreamCommand.Deferred && sent.Count == 1 &&
            Convert.ToBase64String(sent[0]) == Convert.ToBase64String(OledRadioDelta.Encode(first, next)),
            "a later tick advances from confirmed pixels and holds the next delta");
        Check(update(newer) == OledStreamCommand.Deferred && sent.Count == 1,
            "a newer frame cannot overtake an in-flight delta");
        receipts.Observe(0xFA60, Receipt(next));
        Check(update(next) == OledStreamCommand.Sent && update(next) == OledStreamCommand.Unchanged,
            "a receipt completes the matching request and identical pixels do not resend");

        Check(update(newer) == OledStreamCommand.Deferred && sent.Count == 2, "second unconfirmed delta is sent once");
        elapsed = 2501; idle = false;
        Check(update(newer) == OledStreamCommand.Deferred && radio.Writes.Count == 12 && sent.Count == 2,
            "timeout clears the base and recovery yields to desktop input");
        idle = true;
        Check(update(newer) == OledStreamCommand.Deferred && radio.Writes.Count == 24 && sent.Count == 2,
            "recovery sends a guarded full frame and waits for its own receipt");
        receipts.Observe(0xFA60, Receipt(newer));
        Check(update(newer) == OledStreamCommand.Sent, "matching recovery completes");

        var large = Pixels(3);
        for (int index = 32; index < 90; index++) large[index] = 0x12;
        idle = false;
        byte[] intermediate;
        byte[] firstStep = OledRadioDelta.EncodeStep(newer, large, out intermediate);
        Check(OledRadioDelta.Encode(newer, large) == null && intermediate[60] == 0x12 && intermediate[61] == 0 &&
            Convert.ToBase64String(firstStep) == Convert.ToBase64String(OledRadioDelta.Encode(newer, intermediate)),
            "a large change has a valid bounded intermediate frame and matching wire CRC");
        Check(update(large) == OledStreamCommand.Deferred && sent.Count == 3 && radio.Writes.Count == 24,
            "a large change advances from the confirmed base while the mouse is in use");
        receipts.Observe(0xFA60, Receipt(large));
        Check(update(large) == OledStreamCommand.Deferred && sent.Count == 3,
            "the final CRC cannot acknowledge an intermediate frame");
        receipts.Observe(0xFA60, Receipt(intermediate));
        Check(update(large) == OledStreamCommand.Deferred && sent.Count == 4 && radio.Writes.Count == 24,
            "the intermediate CRC permits the next packet during movement");
        receipts.Observe(0xFA60, Receipt(large));
        Check(update(large) == OledStreamCommand.Sent, "the final patch advances the complete frame");
    }
    static void CheckRadioReplay() {
        var radio = RadioB(); radio.Version[7] = (byte)'E';
        var sender = new OledSender(radio, delay => {}); var capabilities = sender.Discover();
        Check(capabilities.SupportsRadioDeltaReplay, "OLE advertises exact delta replay");
        var receipts = new RadioOledAcknowledgements();
        var stream = new ConfirmedRadioOledStream(receipts, capabilities.SupportsRadioDeltaReplay);
        var sent = new List<byte[]>();
        long elapsed = 0;
        bool idle = true;
        int reports, confirmations; string transfer;
        Func<byte[],string> update = frame => stream.Send(sender, frame, true, () => idle, sent.Add,
            () => elapsed, out reports, out transfer, out confirmations);
        update(Pixels(1));
        receipts.Observe(0xFA60, Receipt(Pixels(1)));
        update(Pixels(2));
        idle = false;
        elapsed = 1199;
        Check(update(Pixels(3)) == OledStreamCommand.Deferred && sent.Count == 1,
            "no duplicate is sent before the retry interval");
        elapsed = 1200;
        Check(update(Pixels(3)) == OledStreamCommand.Deferred && sent.Count == 2 && sent[0] == sent[1] && radio.Writes.Count == 12,
            "a retry during input sends the exact pending patch without beginning a full frame");
        receipts.Observe(0xFA60, Receipt(Pixels(2)));
        Check(update(Pixels(3)) == OledStreamCommand.Deferred && sent.Count == 3 &&
            Convert.ToBase64String(sent[2]) == Convert.ToBase64String(OledRadioDelta.Encode(Pixels(2), Pixels(3))),
            "the retry receipt releases the newer frame against the recovered base");
        elapsed = 2400;
        update(Pixels(4));
        elapsed = 3600;
        update(Pixels(4));
        elapsed = 10000;
        Check(update(Pixels(4)) == OledStreamCommand.Deferred && sent.Count == 5 && radio.Writes.Count == 12 &&
            sent[2] == sent[3] && sent[2] == sent[4],
            "at most two retries precede guarded full recovery");
        idle = true;
        Check(update(Pixels(4)) == OledStreamCommand.Deferred && radio.Writes.Count == 24,
            "a pause opens the full recovery path after replay attempts are exhausted");
    }
    static void CheckHeldMouseButton() {
        var radio = RadioB(); radio.Version[7] = (byte)'E';
        var receipts = new RadioOledAcknowledgements();
        var writes = new List<byte[]>();
        var trace = new StringWriter();
        int tick = 0;
        string input = Partial(1) + Partial(1) + Partial(2) + Partial(3) + Partial(3) + Partial(3) + Partial(3);

        OledStreaming.Run(new StringReader(input), new StringWriter(), radio, delay => {}, () => true,
            packet => {
                writes.Add(packet);
                if (writes.Count == 2) receipts.Observe(0xFA60, Receipt(Pixels(2)));
            }, () => tick * 1000L, trace, capabilities => {
                Check(capabilities.SupportsRadioDeltaReplay && radio.Reads == 1,
                    "radio receipt listener starts after a single firmware discovery");
                return receipts;
            }, () => {
                if (++tick == 3) receipts.Observe(0xFA60, Receipt(Pixels(1)));
                return tick != 1 && tick != 4 && tick != 5;
            });

        Check(radio.Writes.Count == 12 && writes.Count == 3 && Object.ReferenceEquals(writes[0], writes[1]),
            "held button pauses baseline and retries without spending attempts or replacing the pending patch");
        Check(trace.ToString().Contains("frame=1 type=button-held reports=0") &&
            trace.ToString().Contains("frame=4 type=button-held reports=0") &&
            trace.ToString().Contains("frame=5 type=button-held reports=0") &&
            trace.ToString().Contains("frame=6 type=delta-retry reports=1") &&
            trace.ToString().Contains("frame=7 type=delta reports=1"),
            "release resumes the delayed retry and a confirmed base permits the next delta");

        var legacy = RadioB();
        OledStreaming.Run(new StringReader(Partial(1) + Partial(2)), new StringWriter(), legacy,
            delay => {}, () => true, packet => { throw new Exception("Legacy OLB delta must remain opt-in"); },
            startRadioAcknowledgements: capabilities => { throw new Exception("OLB has no receipts"); },
            allowLegacyPartial: false);
        Check(legacy.Writes.Count == 24, "a normal launch uses guarded full frames on OLB");
    }
    static void CheckPartialLiveFrames() {
        var radio=RadioB();var replies=new StringWriter();var wire=new List<byte[]>();
        var trace=new StringWriter();
        var input=new StringBuilder(Partial(0));
        for(byte next=1;next<=8;next++)input.Append(Partial(next));
        input.Append(Partial(8));
        for(int fragment=0;fragment<12;fragment++)input.Append(Partial(9));
        input.Append("STOP\n");
        int guardChecks=0;
        OledStreaming.Run(new StringReader(input.ToString()),replies,radio,delay=>{},
            ()=>++guardChecks!=13,wire.Add,()=>0,trace);
        Check(radio.Writes.Count==24&&wire.Count==8&&guardChecks==26&&radio.Writes[12][12]==9,
            "an initial frame is idle-guarded, eight deltas use one report each and a busy anchor is deferred");
        string[] acknowledgements=replies.ToString().Split(new[]{'\r','\n'},StringSplitOptions.RemoveEmptyEntries);
        Check(acknowledgements[10]=="OLED_FRAME_DEFERRED 10"&&acknowledgements[11]=="OLED_FRAME_DEFERRED 11"&&
            acknowledgements[22]=="OLED_FRAME_SENT 22","an interrupted refresh advances by one fragment per request and confirms the complete frame only");
        Check(radio.Reads==1&&radio.StatusReads==0,"partial live uses one discovery without periodic GET");
        Check(trace.ToString().Contains("frame=2 type=delta reports=1 result=OLED_FRAME_SENT ")&&
            trace.ToString().Contains("frame=10 type=refresh reports=0 result=OLED_FRAME_DEFERRED ")&&
            trace.ToString().Contains("frame=11 type=refresh reports=1 result=OLED_FRAME_DEFERRED "),
            "hardware trace distinguishes a single delta from a guarded full-frame deferral");

        radio=RadioB();replies=new StringWriter();wire.Clear();
        int tick=0;
        var repeated=new StringBuilder(Partial(0)+Partial(1)+Partial(1));
        for(int fragment=0;fragment<12;fragment++)repeated.Append(Partial(1));
        OledStreaming.Run(new StringReader(repeated.Append("STOP\n").ToString()),replies,radio,delay=>{},
            ()=>true,wire.Add,()=>tick<3?tick++:15001);
        Check(wire.Count==1&&radio.Writes.Count==24&&replies.ToString().Contains("OLED_FRAME_UNCHANGED 3"),
            "unchanged frame sends nothing until the expiry refresh repairs a silent loss");

        radio=RadioB();wire.Clear();replies=new StringWriter();guardChecks=0;
        var large=new byte[480];for(int index=0;index<30;index++)large[index]=1;
        OledStreaming.Run(new StringReader(Partial(0)+"LIVE_DELTA "+Convert.ToBase64String(large)+"\n"+Partial(1)+"STOP\n"),
            replies,radio,delay=>{},()=>++guardChecks<=14,wire.Add,()=>0);
        Check(radio.Writes.Count==13&&wire.Count==0&&replies.ToString().Contains("OLED_FRAME_DEFERRED 3"),
            "a full refresh interrupted after its first fragment pauses before another report");

        radio=RadioB();wire.Clear();replies=new StringWriter();
        large[200]=0x31;
        input=new StringBuilder(Partial(0)+"LIVE_DELTA "+Convert.ToBase64String(large)+"\n");
        for(int fragment=1;fragment<12;fragment++)input.Append(Partial(2));
        OledStreaming.Run(new StringReader(input.Append("STOP\n").ToString()),replies,radio,delay=>{},()=>true,wire.Add,()=>0);
        Check(radio.Writes.Count==24&&radio.Writes[17][12]==0x31&&wire.Count==0&&
            replies.ToString().Contains("OLED_FRAME_DEFERRED 13"),
            "a progressive refresh keeps its original pixels and never acknowledges a newer frame it did not send");

        radio=RadioB();radio.Version[7]=(byte)'A';wire.Clear();
        OledStreaming.Run(new StringReader(Partial(0)+Partial(1)+"STOP\n"),new StringWriter(),radio,delay=>{},
            ()=>true,wire.Add,()=>0);
        Check(radio.Writes.Count==24&&wire.Count==0,"OLA uses guarded full frames");

        var usb=RadioB();usb.Pid=0x2148;
        OledStreaming.Run(new StringReader(Partial(0)+Partial(1)+"STOP\n"),new StringWriter(),usb,delay=>{},
            ()=>{throw new Exception("USB cannot consult radio input gate");},wire.Add,()=>0);
        Check(usb.Writes.Count==24&&wire.Count==0,"USB uses ordinary full frames");
    }
    static void CheckRadioInputPriority() {
        var radio = new FakeTransport { Pid = 0xFA60, Version = new byte[] { 11, 5, 1, 1, 8, (byte)'O', (byte)'L', (byte)'9' } };
        var output = new StringWriter();
        string live = "LIVE " + Frame(1, 0).Substring(6);
        int checksAtFragment = 0;
        var delays = new List<int>();
        OledStreaming.Run(new StringReader(live + "\n" + live + "\n" + live + "\n" + Frame(2, 0) + "\nSTOP\n"),
            output, radio, delays.Add, delegate {
                int check = ++checksAtFragment;
                return check != 1 && check != 3;
            });
        Check(output.ToString().Replace("\r\n", "\n") ==
            "OLED_STREAM_READY\nOLED_FRAME_DEFERRED 1\nOLED_FRAME_DEFERRED 2\nOLED_FRAME_SENT 3\nOLED_FRAME_SENT 4\n",
            "radio live defers before first or next fragment while manual frames bypass input priority");
        Check(radio.Reads == 1 && radio.Writes.Count == 25 && checksAtFragment == 15 &&
            delays.Count == 25 && delays.GetRange(0, 13).TrueForAll(value => value == 250) &&
            delays.GetRange(13, 12).TrueForAll(value => value == 500),
            "radio stream reuses discovery and paces guarded and manual fragments separately");
        Check(radio.Writes[0][10] != 0 && radio.Writes[1][10] == radio.Writes[0][10] && radio.Writes[13][10] == 0,
            "a partial initial frame does not consume display initialization");

        var usb = new FakeTransport();
        OledStreaming.Run(new StringReader(live + "\nSTOP\n"), new StringWriter(), usb,
            delegate(int value) { }, delegate { throw new Exception("USB must not query desktop activity"); },
            canSendRadioLive: () => { throw new Exception("USB must not query held buttons"); });
        Check(usb.Writes.Count == 4, "USB live frame is fully transmitted without an input check");
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
        CheckPartialLiveFrames();
        CheckConfirmedRadioFrames();
        CheckRadioReplay();
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
        CheckRadioInputPriority();
        CheckHeldMouseButton();
        Console.WriteLine("PASS " + checks + " fake OLED stream checks; no USB, no physical display claim");
        return 0;
    } catch (Exception error) { Console.Error.WriteLine(error); return 1; } }
}
