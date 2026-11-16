using System;
using System.Collections.Generic;
using System.IO;
using Furycube.G9.Oled;

internal static class OledShortProtocolTests {
    sealed class FakeTransport : IOledTransport, IOledRuntimeStatusTransport {
        internal ushort Vid = 0x1D57, Pid = 0x2148;
        internal int Reads, Attempts, FailAt, StatusReads;
        internal byte[] Version = VersionReply('4');
        internal byte[] Status = StatusFrame(64, 4, 1);
        internal Action BeforeFirstWrite;
        internal readonly List<byte[]> Writes = new List<byte[]>();
        public ushort VendorId { get { return Vid; } }
        public ushort ProductId { get { return Pid; } }
        public byte[] ReadFirmwareVersion() { Reads++; return (byte[])Version.Clone(); }
        public byte[] ReadRuntimeStatus() { StatusReads++; return (byte[])Status.Clone(); }
        public void SendFeature(byte[] report) {
            Attempts++;
            if (Attempts == 1 && BeforeFirstWrite != null) BeforeFirstWrite();
            if (Attempts == FailAt) throw new IOException("Injected chunk submission failure");
            Writes.Add((byte[])report.Clone());
        }
    }
    sealed class FakeSession : IOledDeviceSession, IOledRuntimeStatusDeviceSession {
        internal ushort Vid = 0x1D57, Pid = 0x2148;
        internal int Reads, Attempts, FailAt, StatusReads;
        internal byte[] Version = VersionReply('4');
        internal byte[] Status = StatusFrame(64, 4, 1);
        internal readonly List<byte[]> Writes = new List<byte[]>();
        public ushort VendorId { get { return Vid; } }
        public ushort ProductId { get { return Pid; } }
        public byte[] ReadOledFirmware() { Reads++; return (byte[])Version.Clone(); }
        public byte[] ReadOledRuntimeStatus() { StatusReads++; return (byte[])Status.Clone(); }
        public void SendOledFeature(byte[] report) {
            Attempts++;
            if (Attempts == FailAt) throw new IOException("Injected native-session failure");
            Writes.Add((byte[])report.Clone());
        }
    }
    static int checks;
    static void Check(bool valid, string name) { if (!valid) throw new Exception("FAIL " + name); checks++; }
    static void Reject<T>(Action action, string name) where T : Exception {
        try { action(); } catch (T) { checks++; return; }
        throw new Exception("FAIL accepted " + name);
    }
    static byte[] VersionReply(char revision) { return new byte[] { 11, 8, 1, 1, 8, (byte)'O', (byte)'L', (byte)revision }; }
    static void Seal(byte[] bytes) {
        int offset = bytes.Length - 2; ushort crc = OledProtocol.Crc16CcittFalse(bytes, 0, offset);
        bytes[offset] = (byte)crc; bytes[offset + 1] = (byte)(crc >> 8);
    }
    static byte[] Changed(byte[] original, int offset, byte value, bool seal = true) {
        byte[] copy = (byte[])original.Clone(); copy[offset] = value; if (seal) Seal(copy); return copy;
    }
    static byte[] StatusFrame(int length, byte version, byte status) {
        var bytes = new byte[length]; bytes[0] = 0x22; bytes[1] = (byte)length;
        bytes[2] = (byte)'G'; bytes[3] = (byte)'S'; bytes[4] = version; bytes[5] = status;
        if (status == 1) { bytes[6] = 2; bytes[7] = 6; bytes[8] = 0x40; bytes[9] = 0x1F; }
        Seal(bytes); return bytes;
    }
    static byte[] Frame(int seed) {
        var bytes = new byte[480];
        for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)((i * 37 + seed) & (i >= 360 ? 15 : 255));
        return bytes;
    }
    static bool Equal(byte[] first, byte[] second) {
        if (first.Length != second.Length) return false;
        for (int i = 0; i < first.Length; i++) if (first[i] != second[i]) return false;
        return true;
    }
    static string Hex(byte[] bytes) { return BitConverter.ToString(bytes).Replace("-", ""); }
    static void Reconstruct(IList<byte[]> reports, int start, byte[] expected, bool initialize) {
        byte[] rebuilt = new byte[480]; int[] coverage = new int[480];
        for (int i = 0; i < 12; i++) {
            byte[] report = reports[start + i]; OledShortProtocol.ValidateWriteChunk(report);
            Check(report.Length == 64 && report[7] == i / 3 && report[8] == (i % 3) * 40,
                "one EP0-sized packet in page/column order " + i);
            Check(report[10] == (initialize && i == 0 ? 1 : 0), "initialization belongs to first chunk only " + i);
            for (int x = 0; x < 40; x++) { int destination = report[7] * 120 + report[8] + x; rebuilt[destination] = report[12 + x]; coverage[destination]++; }
        }
        Check(Equal(rebuilt, expected), "all 480 source bytes reconstruct across page and chunk boundaries");
        foreach (int count in coverage) Check(count == 1, "each visible column byte covered once");
    }
    static void CodecChecks() {
        var caps = OledProtocol.DecodeFirmwareCapabilities(VersionReply('4'));
        Check(caps.Extension == "OL4" && caps.SupportsShortWrites && caps.SupportsRuntimeStatus, "OL4 advertises short writes and status");
        foreach (char old in new[] { '2', '3' }) Check(!OledProtocol.DecodeFirmwareCapabilities(VersionReply(old)).SupportsShortWrites, "old marker never authorizes short writes " + old);
        var pixels = new byte[40]; for (int i = 0; i < 40; i++) pixels[i] = (byte)i;
        byte[] originalPixels = (byte[])pixels.Clone(); byte[] packet = OledShortProtocol.BuildWriteChunk(0, 40, pixels);
        // CRC independently checked with binascii.crc_hqx(prefix, 0xFFFF) == 0x5810.
        const string golden = "2240474F0401000028280000000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F2021222324252627000000000000000000001058";
        Check(Hex(packet) == golden, "fixed GO4 header, pixel positions, zero padding and little-endian CRC fixture");
        Check(Equal(pixels, originalPixels), "chunk builder leaves caller pixels unchanged");
        pixels[0] ^= 0xFF; Check(packet[12] == 0, "chunk report owns its pixel snapshot");
        packet[13] ^= 1; Check(pixels[1] == 1, "report mutation cannot change caller pixels");
        packet = OledShortProtocol.BuildWriteChunk(0, 40, originalPixels);
        int[] fields = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 };
        byte[] values = { 0x23, 63, 0, 0, 3, 2, 1, 4, 41, 39, 2, 1 };
        for (int i = 0; i < fields.Length; i++) {
            byte[] changed = Changed(packet, fields[i], values[i]);
            Reject<InvalidDataException>(delegate { OledShortProtocol.ValidateWriteChunk(changed); }, "invalid header field with repaired CRC " + fields[i]);
        }
        for (int offset = 52; offset < 62; offset++) {
            byte[] changed = Changed(packet, offset, 1);
            Reject<InvalidDataException>(delegate { OledShortProtocol.ValidateWriteChunk(changed); }, "nonzero padding despite valid CRC " + offset);
        }
        Reject<InvalidDataException>(delegate { OledShortProtocol.ValidateWriteChunk(Changed(packet, 62, (byte)(packet[62] ^ 1), false)); }, "low CRC corruption");
        Reject<InvalidDataException>(delegate { OledShortProtocol.ValidateWriteChunk(Changed(packet, 63, (byte)(packet[63] ^ 1), false)); }, "high CRC corruption");
        Reject<InvalidDataException>(delegate { OledShortProtocol.ValidateWriteChunk(Changed(packet, 10, 1)); }, "initialization at later chunk");
        byte[] laterPage = OledShortProtocol.BuildWriteChunk(1, 0, originalPixels);
        Reject<InvalidDataException>(delegate { OledShortProtocol.ValidateWriteChunk(Changed(laterPage, 10, 1)); }, "initialization at later page");
        foreach (int page in new[] { -1, 4 }) Reject<InvalidDataException>(delegate { OledShortProtocol.BuildWriteChunk(page, 0, originalPixels); }, "builder page " + page);
        foreach (int x in new[] { -1, 1, 39, 41, 120 }) Reject<InvalidDataException>(delegate { OledShortProtocol.BuildWriteChunk(0, x, originalPixels); }, "builder column " + x);
        foreach (int length in new[] { 0, 39, 41, 120 }) Reject<InvalidDataException>(delegate { OledShortProtocol.BuildWriteChunk(0, 0, new byte[length]); }, "builder payload length " + length);
        Reject<InvalidDataException>(delegate { OledShortProtocol.BuildWriteChunk(0, 0, null); }, "null payload");
        Reject<InvalidDataException>(delegate { OledShortProtocol.ValidateWriteChunk(null); }, "null packet");
        foreach (int length in new[] { 63, 65, 132 }) Reject<InvalidDataException>(delegate { OledShortProtocol.ValidateWriteChunk(new byte[length]); }, "wrong packet length " + length);
        byte[] last = OledShortProtocol.BuildWriteChunk(3, 80, new byte[40]);
        for (int offset = 12; offset < 52; offset++) {
            byte[] changed = Changed(last, offset, 0x10);
            Reject<InvalidDataException>(delegate { OledShortProtocol.ValidateWriteChunk(changed); }, "invisible last-page pixel despite valid CRC " + offset);
        }
        Reject<InvalidDataException>(delegate { OledShortProtocol.BuildWriteChunk(3, 0, originalPixels); }, "builder rejects invisible last-page bits");
        foreach (int seed in new[] { 0, 1, 255 }) {
            byte[] frame = Frame(seed), before = (byte[])frame.Clone(); byte[][] reports = OledShortProtocol.BuildFrameReports(frame, true);
            Check(reports.Length == 12, "one complete frame has twelve chunks"); Reconstruct(reports, 0, frame, true);
            Check(Equal(frame, before), "whole-frame builder leaves source unchanged");
            reports[0][12] ^= 1; Check(Equal(frame, before), "frame packet cannot mutate source");
        }
    }
    static void SenderChecks() {
        byte[] frame = Frame(7), snapshot = (byte[])frame.Clone(); var wire = new FakeTransport(); var delays = new List<int>();
        var sender = new OledSender(wire, delegate(int milliseconds) { delays.Add(milliseconds); });
        Reject<InvalidOperationException>(delegate { sender.WriteFrame(frame); }, "OL4 write before discovery");
        Check(wire.Attempts == 0 && wire.Reads == 0, "write cannot implicitly discover or submit");
        sender.Discover(); wire.BeforeFirstWrite = delegate { Array.Clear(frame, 0, frame.Length); };
        var result = sender.WriteFrame(frame);
        Check(result.FeatureReportsSent == 12 && !result.DisplayVerified, "twelve submissions do not claim visible pixels");
        Reconstruct(wire.Writes, 0, snapshot, true);
        Check(delays.Count == 11 && delays.TrueForAll(delegate(int value) { return value == 25; }), "pacing between every short chunk, no trailing delay");
        sender.WriteFrame(snapshot); Reconstruct(wire.Writes, 12, snapshot, false);
        Check(wire.Reads == 1, "later frame reuses verified discovery without reinitialization");
        foreach (byte[] invalid in new[] { null, new byte[479], new byte[481] }) {
            var fake = new FakeTransport(); var invalidSender = new OledSender(fake, delegate(int value) { }); invalidSender.Discover();
            Reject<InvalidDataException>(delegate { invalidSender.WriteFrame(invalid); }, "invalid whole-frame length");
            Check(fake.Attempts == 0, "invalid whole frame cannot submit initial chunks");
        }
        foreach (int badPixel in new[] { 360, 399, 400, 439, 440, 479 }) {
            var fake = new FakeTransport(); var invalidSender = new OledSender(fake, delegate(int value) { }); invalidSender.Discover(); byte[] bad = Frame(8); bad[badPixel] |= 0x10;
            Reject<InvalidDataException>(delegate { invalidSender.WriteFrame(bad); }, "invisible pixel in final-page chunk " + badPixel);
            Check(fake.Attempts == 0, "all twelve chunks validated before first send " + badPixel);
        }
        foreach (int failAt in new[] { 1, 2, 7, 12 }) {
            var fake = new FakeTransport { FailAt = failAt }; var broken = new OledSender(fake, delegate(int value) { }); broken.Discover();
            Reject<IOException>(delegate { broken.WriteFrame(snapshot); }, "submission failure at chunk " + failAt);
            Check(fake.Attempts == failAt && fake.Writes.Count == failAt - 1 && broken.Capabilities == null, "failure stops without retries and clears discovery " + failAt);
            Reject<InvalidOperationException>(delegate { broken.WriteFrame(snapshot); }, "failed session cannot continue without discovery");
            Check(fake.Attempts == failAt, "blocked continuation sends no later chunk");
            fake.FailAt = 0; broken.Discover(); int start = fake.Writes.Count; broken.WriteFrame(snapshot);
            Reconstruct(fake.Writes, start, snapshot, true);
        }
        foreach (char revision in new[] { '2', '3' }) {
            var legacy = new FakeTransport { Version = VersionReply(revision) }; var legacySender = new OledSender(legacy, delegate(int value) { }); legacySender.Discover(); legacySender.WriteFrame(snapshot);
            Check(legacy.Writes.Count == 4 && legacy.Writes[0].Length == 132, "pure legacy codec fixtures remain four pages " + revision);
        }
    }
    static void NativeChecks() {
        byte[] frame = Frame(12), shortChunk = OledShortProtocol.BuildWriteChunk(0, 0, new byte[40], true);
        foreach (char revision in new[] { '2', '3' }) {
            var session = new FakeSession { Version = VersionReply(revision) }; var native = new NativeOledTransport(session); native.ReadFirmwareVersion();
            Reject<NotSupportedException>(delegate { native.SendFeature(OledProtocol.BuildWritePage(0, new byte[120])); }, "native blocks dangerous long write " + revision);
            Reject<NotSupportedException>(delegate { native.SendFeature(shortChunk); }, "old marker cannot authorize short format " + revision);
            Check(session.Attempts == 0, "old firmware never reaches native feature submission " + revision);
        }
        var device = new FakeSession(); var adapter = new NativeOledTransport(device);
        Reject<InvalidOperationException>(delegate { adapter.SendFeature(shortChunk); }, "native short write before discovery");
        adapter.ReadFirmwareVersion();
        Reject<InvalidDataException>(delegate { adapter.SendFeature(Changed(shortChunk, 62, 0, false)); }, "native validates short CRC before session call");
        Reject<InvalidDataException>(delegate { adapter.SendFeature(OledProtocol.BuildWritePage(0, new byte[120])); }, "OL4 session refuses old long-format packet");
        Check(device.Attempts == 0, "malformed or old-format native packet sends nothing");
        var sender = new OledSender(adapter, delegate(int value) { }); sender.Discover(); sender.WriteFrame(frame);
        Reconstruct(device.Writes, 0, frame, true); Check(device.Reads == 2 && device.Attempts == 12, "full sender-to-native path uses original verified session");
        device.Pid = 0xFA60; Reject<NotSupportedException>(delegate { adapter.SendFeature(shortChunk); }, "native detects changed receiver identity");
        Check(device.Attempts == 12, "changed device receives no additional chunk");
        var fault = new FakeSession { FailAt = 4 }; var broken = new NativeOledTransport(fault); broken.ReadFirmwareVersion();
        for (int i = 0; i < 3; i++) broken.SendFeature(shortChunk);
        Reject<IOException>(delegate { broken.SendFeature(shortChunk); }, "native submission failure");
        Reject<InvalidOperationException>(delegate { broken.SendFeature(shortChunk); }, "native failure clears discovery");
        Check(fault.Attempts == 4 && fault.Writes.Count == 3, "native never retries failed chunk or reaches later chunk");
    }
    static void RuntimeChecks() {
        Check(Hex(OledRuntimeStatusCodec.BuildReadRequest()) == "A022840000000000", "legacy status query remains 132 bytes");
        Check(Hex(OledRuntimeStatusCodec.BuildReadRequest(true)) == "A022400000000000", "OL4 status query requests one 64-byte packet");
        var now = DateTimeOffset.UtcNow;
        foreach (int length in new[] { 64, 132 }) {
            byte revision = length == 64 ? (byte)4 : (byte)3; byte[] status = StatusFrame(length, revision, 1), before = (byte[])status.Clone();
            var decoded = OledRuntimeStatusCodec.Decode(status, now);
            Check(decoded.IsValid && !decoded.IsBusy && decoded.ProfileId == 2 && decoded.Stage == 6 && decoded.ConfiguredDpi == 8000, "status payload decodes for exact version/length " + length);
            Check(Equal(before, status), "status decoder leaves source unchanged " + length);
            Reject<FormatException>(delegate { OledRuntimeStatusCodec.Decode(Changed(status, 4, revision == 4 ? (byte)3 : (byte)4), now); }, "status version cannot cross length boundary " + length);
            Reject<FormatException>(delegate { OledRuntimeStatusCodec.Decode(Changed(status, 1, length == 64 ? (byte)132 : (byte)64), now); }, "declared status length must match actual " + length);
            Reject<FormatException>(delegate { OledRuntimeStatusCodec.Decode(Changed(status, 3, (byte)'O'), now); }, "pixel header cannot masquerade as status " + length);
            Reject<FormatException>(delegate { OledRuntimeStatusCodec.Decode(Changed(status, 10, 1), now); }, "status nonzero padding with repaired CRC " + length);
            Reject<FormatException>(delegate { OledRuntimeStatusCodec.Decode(Changed(status, length - 1, (byte)(status[length - 1] ^ 1), false), now); }, "status CRC corruption " + length);
            foreach (byte state in new byte[] { 0, 2 }) {
                var noValue = OledRuntimeStatusCodec.Decode(StatusFrame(length, revision, state), now);
                Check(!noValue.IsValid && noValue.IsBusy == (state == 2) && !noValue.ConfiguredDpi.HasValue, "invalid/busy status has no stale DPI " + length);
            }
        }
        var wire = new FakeTransport(); var sender = new OledSender(wire, delegate(int value) { }); sender.Discover(); byte[] returned = sender.ReadRuntimeStatusFrame();
        Check(returned.Length == 64 && wire.StatusReads == 1 && wire.Attempts == 0, "OL4 status and image channels remain separate");
        sender.WriteFrame(Frame(1)); Check(wire.Writes[0][10] == 1, "status before first frame does not consume initialization");
        sender.ReadRuntimeStatusFrame(); sender.WriteFrame(Frame(2)); Check(wire.Writes[12][10] == 0, "status between frames does not reinitialize display");
        wire.Status[62] ^= 1;
        Reject<FormatException>(delegate { sender.ReadRuntimeStatusFrame(); }, "corrupt OL4 status rejected");
        Check(sender.Capabilities == null && wire.Attempts == 24, "bad status invalidates session without new pixel submissions");
        var session = new FakeSession(); var native = new NativeOledTransport(session); native.ReadFirmwareVersion(); byte[] nativeReply = native.ReadRuntimeStatus();
        Check(nativeReply.Length == 64 && session.StatusReads == 1 && session.Attempts == 0, "native OL4 runtime reply uses same session without image write");
        foreach (char negotiated in new[] { '3', '4' }) {
            int wrongLength = negotiated == '4' ? 132 : 64; byte wrongVersion = negotiated == '4' ? (byte)3 : (byte)4;
            byte[] mismatched = StatusFrame(wrongLength, wrongVersion, 1);
            // Both frames are individually valid. The negotiated marker must still
            // reject the other wire format before it can become live device data.
            Check(OledRuntimeStatusCodec.Decode(mismatched, now).IsValid, "cross-format fixture independently has valid header and CRC");
            var wrongWire = new FakeTransport { Version = VersionReply(negotiated), Status = mismatched };
            var wrongSender = new OledSender(wrongWire, delegate(int value) { }); wrongSender.Discover();
            Reject<InvalidDataException>(delegate { wrongSender.ReadRuntimeStatusFrame(); }, "sender rejects status from other negotiated revision " + negotiated);
            Check(wrongSender.Capabilities == null && wrongWire.StatusReads == 1 && wrongWire.Attempts == 0, "sender mismatch invalidates discovery and never sends pixels " + negotiated);
            Reject<InvalidOperationException>(delegate { wrongSender.ReadRuntimeStatusFrame(); }, "sender cannot retry mismatched status without discovery " + negotiated);
            Check(wrongWire.StatusReads == 1, "sender mismatch stops later status calls " + negotiated);
            var wrongSession = new FakeSession { Version = VersionReply(negotiated), Status = mismatched };
            var wrongNative = new NativeOledTransport(wrongSession); wrongNative.ReadFirmwareVersion();
            Reject<InvalidDataException>(delegate { wrongNative.ReadRuntimeStatus(); }, "native adapter rejects status from other negotiated revision " + negotiated);
            Check(wrongSession.StatusReads == 1 && wrongSession.Attempts == 0, "native status mismatch does not submit pixels " + negotiated);
            Reject<InvalidOperationException>(delegate { wrongNative.ReadRuntimeStatus(); }, "native mismatch clears discovery " + negotiated);
            Check(wrongSession.StatusReads == 1, "native mismatch stops later status calls " + negotiated);
        }
    }
    static int Main() {
        try { CodecChecks(); SenderChecks(); NativeChecks(); RuntimeChecks(); Console.WriteLine("PASS " + checks + " OL4 short-packet, full-frame reconstruction, native guard and runtime-status checks; offline fake transports only."); return 0; }
        catch (Exception error) { Console.Error.WriteLine(error.ToString()); return 1; }
    }
}
