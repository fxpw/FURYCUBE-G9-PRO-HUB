using System;

namespace Furycube.G9.Oled {
    public sealed class OledRuntimeStatus {
        public bool IsValid { get; private set; }
        public bool IsBusy { get; private set; }
        public byte? ProfileId { get; private set; }
        public byte? Stage { get; private set; }
        public ushort? ConfiguredDpi { get; private set; }
        public DateTimeOffset ReceivedAtUtc { get; private set; }
        internal OledRuntimeStatus(byte status, byte profile, byte stage, ushort dpi, DateTimeOffset receivedAtUtc) {
            IsValid = status == 1; IsBusy = status == 2; ReceivedAtUtc = receivedAtUtc.ToUniversalTime();
            if (IsValid) { ProfileId = profile; Stage = stage; ConfiguredDpi = dpi; }
        }
        public bool IsFresh(DateTimeOffset nowUtc) { return IsFresh(nowUtc, TimeSpan.FromSeconds(1)); }
        public bool IsFresh(DateTimeOffset nowUtc, TimeSpan maximumAge) {
            if (maximumAge < TimeSpan.Zero || maximumAge > TimeSpan.FromSeconds(1)) throw new ArgumentOutOfRangeException("maximumAge");
            return nowUtc >= ReceivedAtUtc && nowUtc - ReceivedAtUtc <= maximumAge;
        }
    }

    // OL3 only. This pure codec neither opens a device nor authorizes GET 0x22.
    public static class OledRuntimeStatusCodec {
        public const int FeatureBytes = 132;
        public static byte[] BuildReadRequest() { return new byte[] { 0xA0, 0x22, 0x84, 0, 0, 0, 0, 0 }; }
        static void Require(bool condition, string message) { if (!condition) throw new FormatException(message); }
        public static OledRuntimeStatus Decode(byte[] frame, DateTimeOffset receivedAtUtc) {
            Require(frame != null && frame.Length == FeatureBytes, "Invalid OL3 status length");
            byte[] data = (byte[])frame.Clone();
            Require(data[0] == 0x22 && data[1] == 0x84 && data[2] == (byte)'G' && data[3] == (byte)'S' && data[4] == 3,
                "Invalid OL3 status header or version");
            Require(data[5] <= 2, "Unknown OL3 runtime status");
            ushort crc = OledProtocol.Crc16CcittFalse(data, 0, 130);
            Require(data[130] == (byte)crc && data[131] == (byte)(crc >> 8), "OL3 runtime status CRC mismatch");
            for (int index = 10; index < 130; index++) Require(data[index] == 0, "OL3 runtime status padding must be zero");
            ushort dpi = (ushort)(data[8] | (data[9] << 8));
            if (data[5] == 1) Require(data[6] >= 1 && data[6] <= 5 && data[7] >= 1 && data[7] <= 8 && dpi != 0,
                "Invalid OL3 profile, stage or configured DPI");
            else Require(data[6] == 0 && data[7] == 0 && dpi == 0, "Invalid/busy OL3 status must not carry stale values");
            return new OledRuntimeStatus(data[5], data[6], data[7], dpi, receivedAtUtc);
        }
    }

    // Optional capability: OL2 transports remain valid for v2 WRITE_PAGE only.
    public interface IOledRuntimeStatusTransport { byte[] ReadRuntimeStatus(); }
}
