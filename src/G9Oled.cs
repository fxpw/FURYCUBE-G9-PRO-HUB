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
        public string Extension { get { return "OL2"; } }
        internal FirmwareCapabilities(byte major, byte minor) { VersionMajor = major; VersionMinor = minor; }
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
            Require(reply[5] == (byte)'O' && reply[6] == (byte)'L' && reply[7] == (byte)'2',
                "Firmware does not advertise OL2; OLED writes are disabled");
            Require((!expectedMajor.HasValue || reply[3] == expectedMajor.Value) && (!expectedMinor.HasValue || reply[4] == expectedMinor.Value),
                "Firmware version does not match the supported OL2 revision");
            return new FirmwareCapabilities(reply[3], reply[4]);
        }
        public static byte[] BuildDiscoveryRequest() { return new byte[] { 0xA0, 0x0B, 8, 0, 1, 0, 0, 0 }; }

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
        readonly object gate = new object(); readonly IOledTransport transport; readonly Action<int> wait; FirmwareCapabilities capabilities; bool displayInitialized;
        public OledSender(IOledTransport connection, Action<int> delay = null) {
            if (connection == null) throw new ArgumentNullException("connection"); transport = connection; wait = delay ?? new Action<int>(Thread.Sleep);
        }
        public FirmwareCapabilities Capabilities { get { lock (gate) return capabilities; } }
        void ValidateIdentity() {
            if (transport.VendorId != 0x1D57 || transport.ProductId != 0x2148)
                throw new NotSupportedException("OL2 is currently restricted to the wired G9 1D57:2148; receiver/bootloader writes are disabled");
        }
        public FirmwareCapabilities Discover() {
            lock (gate) {
                capabilities = null; displayInitialized = false; ValidateIdentity();
                capabilities = OledProtocol.DecodeFirmwareCapabilities(transport.ReadFirmwareVersion()); return capabilities;
            }
        }
        public OledTransferResult WriteFrame(byte[] pixels) {
            lock (gate) {
                ValidateIdentity();
                if (capabilities == null) throw new InvalidOperationException("A valid OL2 discovery is required before any OLED feature report");
                // Validate and build ALL pages first: a bad final-page tail cannot cause a partial write.
                byte[][] reports = OledProtocol.BuildFrameReports(pixels, !displayInitialized); int sent = 0;
                try { foreach (byte[] report in reports) { transport.SendFeature(report); sent++; if (sent < reports.Length) wait(25); } }
                catch { capabilities = null; displayInitialized = false; throw; }
                displayInitialized = true;
                // USB submission is not a firmware acknowledgement or proof of visible pixels.
                return new OledTransferResult(sent);
            }
        }
    }
}
