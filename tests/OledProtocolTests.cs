using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Furycube.G9.Oled;

internal static class OledProtocolTests {
    sealed class FakeTransport : IOledTransport {
        internal ushort Vid = 0x1D57, Pid = 0x2148; internal int Reads, FailAt;
        internal byte[] Reply = new byte[] { 0x0B, 8, 1, 1, 8, (byte)'O', (byte)'L', (byte)'2' };
        internal readonly List<byte[]> Writes = new List<byte[]>();
        public ushort VendorId { get { return Vid; } } public ushort ProductId { get { return Pid; } }
        public byte[] ReadFirmwareVersion() { Reads++; return Reply; }
        public void SendFeature(byte[] report) { if (FailAt != 0 && Writes.Count + 1 == FailAt) throw new IOException("Simulated transport failure"); Writes.Add((byte[])report.Clone()); }
    }
    sealed class FakeDeviceSession : IOledDeviceSession {
        internal ushort Vid = 0x1D57, Pid = 0x2148; internal int Reads, Writes;
        internal byte[] Firmware = new byte[] { 11, 8, 1, 1, 8, (byte)'O', (byte)'L', (byte)'2' };
        public ushort VendorId { get { return Vid; } } public ushort ProductId { get { return Pid; } }
        public byte[] ReadOledFirmware() { Reads++; return Firmware; }
        public void SendOledFeature(byte[] report) { Writes++; }
    }
    static int count;
    static void Check(bool value, string name) { if (!value) throw new Exception("FAIL " + name); count++; }
    static void Reject(Action action, string name) {
        try { action(); } catch (InvalidDataException) { count++; return; } catch (ArgumentException) { count++; return; }
        catch (InvalidOperationException) { count++; return; } catch (NotSupportedException) { count++; return; }
        throw new Exception("FAIL accepted " + name);
    }
    static byte[] Modified(byte[] source, int at, byte value, bool repairCrc) {
        byte[] copy = (byte[])source.Clone(); copy[at] = value;
        if (repairCrc) { ushort crc = OledProtocol.Crc16CcittFalse(copy, 0, 130); copy[130] = (byte)crc; copy[131] = (byte)(crc >> 8); } return copy;
    }
    static int Main() { try {
        Check(OledProtocol.Crc16CcittFalse(Encoding.ASCII.GetBytes("123456789"), 0, 9) == 0x29B1, "standard CCITT-FALSE check vector");
        var fake = new FakeTransport(); var caps = OledProtocol.DecodeFirmwareCapabilities(fake.Reply);
        Check(caps.Width == 120 && caps.VisibleHeight == 28 && caps.StorageHeight == 32 && caps.BufferBytes == 480 && caps.Extension == "OL2", "advertised fixed geometry");
        Check(!caps.SupportsRuntimeStatus, "OL2 does not authorize status GET22");
        var caps3 = OledProtocol.DecodeFirmwareCapabilities(Modified(fake.Reply,7,(byte)'3',false));
        Check(caps3.Extension == "OL3" && caps3.SupportsRuntimeStatus, "OL3 advertises runtime status with the same geometry");
        Reject(delegate { OledProtocol.DecodeFirmwareCapabilities(Modified(fake.Reply,7,(byte)'4',false)); }, "unknown OLED extension revision");
        Check(OledProtocol.DecodeFirmwareCapabilities(Modified(fake.Reply, 1, 5, false)).VersionMinor == 8, "ARM version reply logical length five with complete eight-byte transfer");
        Check(BitConverter.ToString(OledProtocol.BuildDiscoveryRequest()) == "A0-0B-08-00-01-00-00-00", "stock-safe existing version query");
        Reject(delegate { OledProtocol.DecodeFirmwareCapabilities(new byte[] { 11, 8, 1, 1, 8, 0, 0, 0 }); }, "stock firmware zeros");
        Reject(delegate { OledProtocol.DecodeFirmwareCapabilities(new byte[7]); }, "short discovery");
        Reject(delegate { OledProtocol.DecodeFirmwareCapabilities(Modified(fake.Reply, 2, 2, false)); }, "wrong discovery selector");
        Reject(delegate { OledProtocol.DecodeFirmwareCapabilities(Modified(fake.Reply, 0, 0x22, false)); }, "unsupported report-22 discovery");
        Reject(delegate { OledProtocol.DecodeFirmwareCapabilities(Modified(fake.Reply, 3, 2, false)); }, "unreviewed firmware version");
        byte[] page = new byte[120]; page[0] = 1; page[119] = 0x80;
        byte[] packet = OledProtocol.BuildWritePage(0, page, true);
        var decoded = OledProtocol.DecodeWritePage(packet, 0);
        Check(decoded.Page == 0 && decoded.InitializeDisplay && decoded.Pixels[0] == 1 && decoded.Pixels[119] == 0x80, "page bytes and initialize flag round-trip");
        Check(packet.Length == 132 && packet[4] == 2 && packet[5] == 1 && packet[6] == 0 && packet[8] == 120 && packet[9] == 1, "stateless v2 WRITE_PAGE layout");
        Reject(delegate { OledProtocol.DecodeWritePage(Modified(packet, 130, (byte)(packet[130] ^ 1), false)); }, "CRC corruption");
        Reject(delegate { OledProtocol.DecodeWritePage(Modified(packet, 9, 2, true)); }, "unknown flags with valid CRC");
        Reject(delegate { OledProtocol.DecodeWritePage(Modified(packet, 5, 2, true)); }, "COMMIT is unsupported");
        Reject(delegate { OledProtocol.DecodeWritePage(Modified(packet, 5, 3, true)); }, "RESTORE is unsupported");
        Reject(delegate { OledProtocol.DecodeWritePage(Modified(packet, 7, 4, true)); }, "page index outside geometry");
        Reject(delegate { OledProtocol.DecodeWritePage(Modified(packet, 8, 119, true)); }, "wrong payload length");
        Reject(delegate { OledProtocol.DecodeWritePage(packet, 1); }, "unexpected frame page");
        Reject(delegate { OledProtocol.DecodeWritePage(Modified(packet, 6, 1, true)); }, "v2 reserved byte cannot carry a sequence");
        Reject(delegate { OledProtocol.DecodeWritePage(Modified(packet, 4, 1, true)); }, "legacy packet version");
        Reject(delegate { OledProtocol.BuildWritePage(4, page); }, "invalid builder page");
        Reject(delegate { OledProtocol.BuildWritePage(1, page, true); }, "builder cannot initialize after page zero");
        byte[] tail = new byte[120]; tail[3] = 0x10;
        Reject(delegate { OledProtocol.BuildWritePage(3, tail); }, "page-three tail bits");
        byte[] validLastPage = OledProtocol.BuildWritePage(3, new byte[120]);
        Reject(delegate { OledProtocol.DecodeWritePage(Modified(validLastPage, 10, 0x80, true)); }, "tail bits rejected even with valid CRC");
        byte[] frame = new byte[480]; frame[0] = 0x81; frame[360] = 0x0F;
        var delays = new List<int>(); var sender = new OledSender(fake, delegate(int milliseconds) { delays.Add(milliseconds); });
        Reject(delegate { sender.WriteFrame(frame); }, "write before discovery");
        Check(fake.Reads == 0 && fake.Writes.Count == 0, "no implicit discovery or SET before explicit capability check");
        sender.Discover(); Check(fake.Reads == 1 && fake.Writes.Count == 0, "discovery sends no image report");
        var sent = sender.WriteFrame(frame);
        Check(sent.FeatureReportsSent == 4 && !sent.DisplayVerified && sent.PhysicalValidation == "NOT TESTED", "submission never claims a physical display result");
        for (int i = 0; i < 4; i++) Check(OledProtocol.DecodeWritePage(fake.Writes[i], i).InitializeDisplay == (i == 0), "only first page initializes " + i);
        Check(delays.Count == 3 && delays.TrueForAll(delegate(int value) { return value == 25; }), "25ms pacing between pages without a delay after the last page");
        sender.WriteFrame(frame);
        Check(fake.Writes.Count == 8 && fake.Writes[4][9] == 0 && fake.Writes[5][9] == 0 && fake.Writes[6][9] == 0 && fake.Writes[7][9] == 0,
            "later frames do not reinitialize or clear the display");
        var stock = new FakeTransport { Reply = new byte[] { 11, 8, 1, 1, 8, 0, 0, 0 } }; var unsupported = new OledSender(stock);
        Reject(delegate { unsupported.Discover(); }, "stock firmware discovery"); Reject(delegate { unsupported.WriteFrame(frame); }, "stock image write");
        Check(stock.Writes.Count == 0, "stock firmware receives no SET22");
        var receiver = new FakeTransport { Pid = 0xFA60 }; Reject(delegate { new OledSender(receiver).Discover(); }, "receiver unsupported");
        Check(receiver.Reads == 0 && receiver.Writes.Count == 0, "receiver fails before any transport call");
        var bootloader = new FakeTransport { Vid = 0xA745, Pid = 0x0033 }; Reject(delegate { new OledSender(bootloader).Discover(); }, "bootloader unsupported");
        Check(bootloader.Reads == 0 && bootloader.Writes.Count == 0, "bootloader receives no OLED traffic");
        var invalid = new FakeTransport(); var invalidSender = new OledSender(invalid, delegate(int milliseconds) { }); invalidSender.Discover(); frame[479] = 0x10;
        Reject(delegate { invalidSender.WriteFrame(frame); }, "bad final page rejects complete frame before transmission");
        Check(invalid.Writes.Count == 0, "invalid tail cannot cause partial frame write"); frame[479] = 0;
        invalid.Pid = 0xFA60; Reject(delegate { invalidSender.WriteFrame(frame); }, "identity rechecked before every frame");
        Check(invalid.Writes.Count == 0, "changed identity receives no image report");
        var fault = new FakeTransport { FailAt = 2 }; var faultSender = new OledSender(fault, delegate(int milliseconds) { }); faultSender.Discover();
        try { faultSender.WriteFrame(frame); throw new Exception("FAIL missing simulated fault"); } catch (IOException) { count++; }
        Check(fault.Writes.Count == 1 && faultSender.Capabilities == null, "partial failure stops without retry and invalidates discovery");
        var device = new FakeDeviceSession(); var adapter = new NativeOledTransport(device);
        Reject(delegate { adapter.SendFeature(packet); }, "native adapter blocks direct writes before discovery");
        Check(device.Reads == 0 && device.Writes == 0, "native adapter does not open an implicit transaction");
        adapter.ReadFirmwareVersion(); adapter.SendFeature(packet);
        Check(device.Reads == 1 && device.Writes == 1, "native adapter uses its original session for discovery and send");
        device.Pid = 0xFA60; Reject(delegate { adapter.SendFeature(packet); }, "native adapter catches stale device identity");
        Check(device.Writes == 1, "identity mismatch cannot reach session Send");
        var nativeStock = new FakeDeviceSession { Firmware = new byte[] { 11, 8, 1, 1, 8, 0, 0, 0 } }; var stockAdapter = new NativeOledTransport(nativeStock);
        Reject(delegate { stockAdapter.ReadFirmwareVersion(); }, "native stock capabilities unsupported");
        Reject(delegate { stockAdapter.SendFeature(packet); }, "native stock cannot send SET22");
        Check(nativeStock.Reads == 1 && nativeStock.Writes == 0, "stock native adapter has zero image writes");
        var invalidPageDevice = new FakeDeviceSession(); var validatingAdapter = new NativeOledTransport(invalidPageDevice); validatingAdapter.ReadFirmwareVersion();
        Reject(delegate { validatingAdapter.SendFeature(Modified(packet, 130, (byte)(packet[130] ^ 1), false)); }, "native adapter validates page CRC before session Send");
        Check(invalidPageDevice.Writes == 0, "bad native frame never reaches the handle");
        Console.WriteLine("PASS " + count + " v2 capability-gate, geometry, CRC, pacing and fault fixtures; fake transport only."); return 0;
    } catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; } }
}
