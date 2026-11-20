using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace Furycube.G9.Oled {
    public sealed class FirmwareCapabilities {
        public byte VersionMajor { get; private set; }
        public byte VersionMinor { get; private set; }
        public int Width { get { return 120; } }
        public int VisibleHeight { get { return 28; } }
        public int StorageHeight { get { return 32; } }
        public int Pages { get { return 4; } }
        public int BufferBytes { get { return 480; } }
        public string Extension { get; private set; }
        public bool SupportsRuntimeStatus { get { return Extension == "OL3" || SupportsShortWrites; } }
        public bool SupportsShortWrites { get { return Extension[2] >= '4'; } }
        public bool SupportsRadioWrites { get { return Extension == "OL9"; } }
        internal FirmwareCapabilities(byte major, byte minor, string extension) { VersionMajor = major; VersionMinor = minor; Extension = extension; }
    }

    public sealed class OledPage {
        public int Page { get; private set; }
        public bool InitializeDisplay { get; private set; }
        public byte[] Pixels { get; private set; }
        internal OledPage(int page, bool initialize, byte[] pixels) { Page = page; InitializeDisplay = initialize; Pixels = pixels; }
    }

    public static class OledProtocol {
        public const int Width = 120, VisibleHeight = 28, StorageHeight = 32, Pages = 4, BufferBytes = 480, FeatureBytes = 132;
        static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }

        // Discovery uses the existing version-report transaction. NEVER use GET 0x22 on stock firmware.
        public static FirmwareCapabilities DecodeFirmwareCapabilities(byte[] reply, byte? expectedMajor = 1, byte? expectedMinor = 8) {
            Require(reply != null && reply.Length == 8 && reply[0] == 0x0B && (reply[1] == 5 || reply[1] == 8) && reply[2] == 1,
                "Unexpected firmware-version response; OLED writes are disabled");
            Require(reply[5] == (byte)'O' && reply[6] == (byte)'L' && reply[7] >= (byte)'2' && reply[7] <= (byte)'9',
                "Firmware does not advertise a supported OLED extension; OLED operations are disabled");
            Require((!expectedMajor.HasValue || reply[3] == expectedMajor.Value) && (!expectedMinor.HasValue || reply[4] == expectedMinor.Value),
                "Firmware version does not match the supported OLED revision");
            return new FirmwareCapabilities(reply[3], reply[4], "OL" + (char)reply[7]);
        }
        public static byte[] BuildDiscoveryRequest() { return new byte[] { 0xA0, 0x0B, 8, 0, 1, 0, 0, 0 }; }
        internal static void ValidateDevice(ushort vendor, ushort product) {
            if (vendor != 0x1D57 || (product != 0x2148 && product != 0xFA60))
                throw new NotSupportedException("OLED discovery requires a normal G9 interface; bootloader operations are disabled");
        }
        internal static void ValidateDeviceFirmware(FirmwareCapabilities capabilities, ushort vendor, ushort product) {
            ValidateDevice(vendor, product);
            if (capabilities == null) throw new InvalidOperationException("Verified OLED discovery is required");
            if (product == 0xFA60 && !capabilities.SupportsRadioWrites)
                throw new NotSupportedException("OLED through the 2.4 GHz receiver requires OL9; no image or status report was sent");
        }

        public static ushort Crc16CcittFalse(byte[] bytes, int offset, int length) {
            if (bytes == null || offset < 0 || length < 0 || offset > bytes.Length - length) throw new ArgumentOutOfRangeException("length");
            ushort crc = 0xFFFF;
            for (int i = offset; i < offset + length; i++) {
                crc ^= (ushort)(bytes[i] << 8);
                for (int bit = 0; bit < 8; bit++) crc = (ushort)((crc & 0x8000) != 0 ? (crc << 1) ^ 0x1021 : crc << 1);
            }
            return crc;
        }
        static void ValidatePage(int page) { if (page < 0 || page >= Pages) throw new ArgumentOutOfRangeException("page"); }
        static void ValidatePixels(int page, byte[] pixels) {
            Require(pixels != null && pixels.Length == Width, "OLED page must contain exactly 120 bytes");
            if (page == 3) for (int i = 0; i < pixels.Length; i++) Require((pixels[i] & 0xF0) == 0, "Pixels outside the visible 120x28 canvas");
        }
        public static void ValidateFrame(byte[] pixels) {
            Require(pixels != null && pixels.Length == BufferBytes, "OLED frame must contain exactly 480 packed bytes");
            for (int i = Width * 3; i < BufferBytes; i++) Require((pixels[i] & 0xF0) == 0, "Pixels outside the visible 120x28 canvas");
        }
        public static byte[] BuildWritePage(int page, byte[] pixels, bool initializeDisplay = false) {
            ValidatePage(page); ValidatePixels(page, pixels);
            Require(!initializeDisplay || page == 0, "Display initialization belongs only to the first page of a frame");
            byte[] packet = new byte[FeatureBytes];
            packet[0] = 0x22; packet[1] = 0x84; packet[2] = (byte)'G'; packet[3] = (byte)'O'; packet[4] = 2;
            packet[5] = 1; packet[6] = 0; packet[7] = (byte)page; packet[8] = Width; packet[9] = initializeDisplay ? (byte)1 : (byte)0;
            Buffer.BlockCopy(pixels, 0, packet, 10, Width);
            ushort crc = Crc16CcittFalse(packet, 0, 130); packet[130] = (byte)crc; packet[131] = (byte)(crc >> 8); return packet;
        }
        public static OledPage DecodeWritePage(byte[] packet, int? expectedPage = null) {
            if (expectedPage.HasValue) ValidatePage(expectedPage.Value);
            Require(packet != null && packet.Length == FeatureBytes, "Invalid OLED transfer length");
            Require(packet[0] == 0x22 && packet[1] == 0x84 && packet[2] == (byte)'G' && packet[3] == (byte)'O' && packet[4] == 2,
                "Invalid OLED protocol header");
            Require(packet[5] == 1, "Only stateless WRITE_PAGE is supported; COMMIT/RESTORE are not implemented");
            Require(packet[6] == 0 && packet[7] < Pages && packet[8] == Width && packet[9] <= 1, "Invalid OLED reserved byte, page, payload length or flags");
            Require(!expectedPage.HasValue || packet[7] == expectedPage.Value, "OLED page differs from the requested frame");
            ushort crc = Crc16CcittFalse(packet, 0, 130);
            Require(packet[130] == (byte)crc && packet[131] == (byte)(crc >> 8), "OLED page CRC mismatch");
            byte[] pixels = new byte[Width]; Buffer.BlockCopy(packet, 10, pixels, 0, pixels.Length); ValidatePixels(packet[7], pixels);
            return new OledPage(packet[7], packet[9] == 1, pixels);
        }
        public static byte[][] BuildFrameReports(byte[] pixels, bool initializeDisplay = false) {
            ValidateFrame(pixels); byte[] snapshot = (byte[])pixels.Clone();
            var reports = new byte[Pages][];
            for (int page = 0; page < Pages; page++) {
                byte[] row = new byte[Width]; Buffer.BlockCopy(snapshot, page * Width, row, 0, Width); reports[page] = BuildWritePage(page, row, initializeDisplay && page == 0);
            }
            return reports;
        }
    }

    // OL4 fits one physical EP0 OUT packet. The stock multi-packet OUT
    // assembler loses the start of a 132-byte report; OL2/OL3 writes must
    // never be submitted by the native transport.
    public static class OledShortProtocol {
        public const int FeatureBytes = 64, PixelsPerChunk = 40, ChunksPerFrame = 12;
        static void Require(bool valid, string message) { if (!valid) throw new InvalidDataException(message); }
        public static byte[] BuildWriteChunk(int page, int x, byte[] pixels, bool initializeDisplay = false) {
            Require(page >= 0 && page < 4 && (x == 0 || x == 40 || x == 80), "Invalid OL4 page or column");
            Require(pixels != null && pixels.Length == PixelsPerChunk, "OL4 needs exactly 40 pixel bytes");
            Require(!initializeDisplay || (page == 0 && x == 0), "OL4 initialization belongs to the first chunk");
            if (page == 3) foreach (byte pixel in pixels) Require((pixel & 0xF0) == 0, "Pixels outside the visible OLED canvas");
            byte[] report = new byte[FeatureBytes];
            report[0] = 0x22; report[1] = 64; report[2] = (byte)'G'; report[3] = (byte)'O'; report[4] = 4;
            report[5] = 1; report[7] = (byte)page; report[8] = (byte)x; report[9] = PixelsPerChunk; report[10] = initializeDisplay ? (byte)1 : (byte)0;
            Buffer.BlockCopy(pixels, 0, report, 12, PixelsPerChunk);
            ushort crc = OledProtocol.Crc16CcittFalse(report, 0, 62); report[62] = (byte)crc; report[63] = (byte)(crc >> 8);
            return report;
        }
        public static void ValidateWriteChunk(byte[] report) {
            Require(report != null && report.Length == FeatureBytes, "Invalid OL4 transfer length");
            Require(report[0] == 0x22 && report[1] == 64 && report[2] == 'G' && report[3] == 'O' && report[4] == 4 && report[5] == 1,
                "Invalid OL4 command header");
            Require(report[6] == 0 && report[11] == 0 && report[7] < 4 && (report[8] == 0 || report[8] == 40 || report[8] == 80) && report[9] == PixelsPerChunk,
                "Invalid OL4 chunk coordinates or reserved bytes");
            Require(report[10] <= 1 && (report[10] == 0 || (report[7] == 0 && report[8] == 0)), "Invalid OL4 initialization flag");
            for (int index = 52; index < 62; index++) Require(report[index] == 0, "Invalid OL4 padding");
            if (report[7] == 3) for (int index = 12; index < 52; index++) Require((report[index] & 0xF0) == 0, "Pixels outside the visible OLED canvas");
            ushort crc = OledProtocol.Crc16CcittFalse(report, 0, 62);
            Require(report[62] == (byte)crc && report[63] == (byte)(crc >> 8), "OL4 CRC mismatch");
        }
        public static byte[][] BuildFrameReports(byte[] pixels, bool initializeDisplay = false) {
            OledProtocol.ValidateFrame(pixels); byte[] snapshot = (byte[])pixels.Clone();
            byte[][] reports = new byte[ChunksPerFrame][];
            for (int page = 0; page < 4; page++) for (int part = 0; part < 3; part++) {
                byte[] chunk = new byte[PixelsPerChunk]; int x = part * PixelsPerChunk;
                Buffer.BlockCopy(snapshot, page * 120 + x, chunk, 0, chunk.Length);
                reports[page * 3 + part] = BuildWriteChunk(page, x, chunk, initializeDisplay && page == 0 && x == 0);
            }
            return reports;
        }
    }

    // The caller owns the native HID connection, timeout and session lock. This assembly never opens HID.
    public interface IOledTransport {
        ushort VendorId { get; }
        ushort ProductId { get; }
        // Existing A0 0B 08 00 01 00 00 00 -> ACK -> GET 0B/8, once per granted read.
        byte[] ReadFirmwareVersion();
        void SendFeature(byte[] report);
    }
    public sealed class OledTransferResult {
        public int FeatureReportsSent { get; private set; }
        public bool DisplayVerified { get { return false; } }
        public string PhysicalValidation { get { return "NOT TESTED"; } }
        internal OledTransferResult(int sent) { FeatureReportsSent = sent; }
    }
    public sealed class OledSender {
        readonly object gate = new object(); readonly IOledTransport transport; readonly Action<int> wait; FirmwareCapabilities capabilities; bool displayInitialized; ushort discoveredProduct;
        public OledSender(IOledTransport connection, Action<int> delay = null) {
            if (connection == null) throw new ArgumentNullException("connection"); transport = connection; wait = delay ?? new Action<int>(Thread.Sleep);
        }
        public FirmwareCapabilities Capabilities { get { lock (gate) return capabilities; } }
        void ValidateIdentity() {
            OledProtocol.ValidateDevice(transport.VendorId, transport.ProductId);
            if (capabilities != null && transport.ProductId != discoveredProduct)
                throw new NotSupportedException("OLED session identity changed; reopen and discover before further traffic");
        }
        public FirmwareCapabilities Discover() {
            lock (gate) {
                capabilities = null; displayInitialized = false; ValidateIdentity();
                ushort target = transport.ProductId;
                var current = OledProtocol.DecodeFirmwareCapabilities(transport.ReadFirmwareVersion());
                ValidateIdentity();
                if (transport.ProductId != target) throw new NotSupportedException("Device changed during OLED discovery");
                OledProtocol.ValidateDeviceFirmware(current, transport.VendorId, target);
                discoveredProduct = target; capabilities = current; return capabilities;
            }
        }
        public byte[] ReadRuntimeStatusFrame() {
            lock (gate) {
                ValidateIdentity();
                if (capabilities == null) throw new InvalidOperationException("Verified OLED discovery is required before runtime status");
                if (!capabilities.SupportsRuntimeStatus) throw new NotSupportedException("OL2 firmware does not implement runtime status; GET 0x22 is disabled");
                var statusTransport = transport as IOledRuntimeStatusTransport;
                if (statusTransport == null) throw new NotSupportedException("This OLED transport does not implement guarded runtime status");
                try {
                    byte[] response = statusTransport.ReadRuntimeStatus();
                    if (response == null || response.Length != (capabilities.SupportsShortWrites ? 64 : 132))
                        throw new InvalidDataException("OLED status length does not match the discovered firmware");
                    OledRuntimeStatusCodec.Decode(response, DateTimeOffset.UtcNow);
                    return (byte[])response.Clone();
                } catch { capabilities = null; displayInitialized = false; throw; }
            }
        }
        public OledTransferResult WriteFrame(byte[] pixels) {
            lock (gate) {
                ValidateIdentity();
                if (capabilities == null) throw new InvalidOperationException("Verified OLED discovery is required before any OLED feature report");
                // Validate and build ALL pages first: a bad final-page tail cannot cause a partial write.
                byte[][] reports = capabilities.SupportsShortWrites ? OledShortProtocol.BuildFrameReports(pixels, !displayInitialized) :
                    OledProtocol.BuildFrameReports(pixels, !displayInitialized); int sent = 0;
                bool radio = discoveredProduct == 0xFA60;
                try {
                    foreach (byte[] report in reports) {
                        ValidateIdentity(); transport.SendFeature(report); sent++;
                        // Keep the tested receiver gap after the last fragment
                        // before the next frame or settings read.
                        if (radio || sent < reports.Length) wait(radio ? 500 : 25);
                    }
                }
                catch { capabilities = null; displayInitialized = false; throw; }
                displayInitialized = true;
                // USB submission is not a firmware acknowledgement or proof of visible pixels.
                return new OledTransferResult(sent);
            }
        }
    }
}
