using System;
using System.Collections.ObjectModel;
using System.Security.Cryptography;

namespace Furycube.G9.Dpi
{
    public enum DpiSettingsSourceKind
    {
        // Wire layout of the captured original HUB's outgoing feature report.
        // This is not a successful device readback or a current-state guarantee.
        CapturedSet04 = 1
    }

    public sealed class DpiSettingsTable
    {
        readonly ReadOnlyCollection<ushort> values;
        readonly byte[] wireFrame;
        public byte ProfileId { get; private set; }
        public byte EnabledStageMask { get; private set; }
        public byte RecordedStage { get; private set; }
        public ushort Checksum { get; private set; }
        public DpiSettingsSourceKind SourceKind { get; private set; }
        public ReadOnlyCollection<ushort> DpiValues { get { return values; } }
        public string SourceSha256 { get; private set; }
        public bool IsDeviceReadback { get { return false; } }

        internal DpiSettingsTable(byte[] frame, ushort[] dpiValues, ushort checksum)
        {
            wireFrame = (byte[])frame.Clone();
            values = Array.AsReadOnly((ushort[])dpiValues.Clone());
            ProfileId = frame[2];
            EnabledStageMask = frame[5];
            RecordedStage = frame[24];
            Checksum = checksum;
            SourceKind = DpiSettingsSourceKind.CapturedSet04;
            using (SHA256 hash = SHA256.Create())
                SourceSha256 = BitConverter.ToString(hash.ComputeHash(wireFrame)).Replace("-", "").ToLowerInvariant();
        }

        public bool IsStageEnabled(int oneBasedStage)
        {
            ValidateStage(oneBasedStage);
            return (EnabledStageMask & (1 << (oneBasedStage - 1))) != 0;
        }

        public ushort? GetEnabledStageDpi(int oneBasedStage)
        {
            ValidateStage(oneBasedStage);
            return IsStageEnabled(oneBasedStage) ? (ushort?)values[oneBasedStage - 1] : null;
        }

        public ushort? RecordedDpi { get { return GetEnabledStageDpi(RecordedStage); } }
        public byte[] GetWireFrameCopy() { return (byte[])wireFrame.Clone(); }

        static void ValidateStage(int stage)
        {
            if (stage < 1 || stage > 8) throw new ArgumentOutOfRangeException("oneBasedStage");
        }
    }

    public static class DpiSettingsDecoder
    {
        public const int CapturedSetFrameBytes = 56;

        // Evidence: portable-hub/hidapi-trace.log contains identical outgoing
        // 0x04/0x38 frames. Original x86 encoder 0x41E200 reproduces all 56 bytes.
        // Sensor body is 47 bytes at 3..49, sum16 BE at 50..51, padding 52..55.
        // Original firmware Set4 copies this body unchanged to RAM 0x40103A.
        // OLED 0x3D328 and sensor apply 0x2FDCC read a single split-byte uint16
        // DPI array: low[8..15], high[16..23]. These are not separate X/Y axes.
        // PAW3311 register lookup occurs later inside firmware; it is not needed
        // to decode this wire format. Do not use the broken legacy native incoming
        // formula (0x420560) or the shared web 3311 lookup as a raw-wire decoder.
        //
        // Only the captured SET variant is implemented. Although firmware's
        // Get4 assembly is understood, no fresh captured GET4 frame has been
        // validated here; a 52-byte frame is deliberately not accepted.
        public static DpiSettingsTable DecodeCapturedSet(byte[] frame, int expectedProfile)
        {
            if (frame == null) throw new ArgumentNullException("frame");
            if (expectedProfile < 1 || expectedProfile > 5) throw new ArgumentOutOfRangeException("expectedProfile");
            if (frame.Length != CapturedSetFrameBytes) throw new FormatException("Only the captured 56-byte SET 0x04 layout is supported.");
            frame = (byte[])frame.Clone();
            if (frame[0] != 0x04 || frame[1] != CapturedSetFrameBytes)
                throw new FormatException("DPI report ID or encoded length does not match SET 0x04/0x38.");
            if (frame[2] != expectedProfile) throw new FormatException("DPI table belongs to a different profile.");

            // The original native outgoing encoder unconditionally zeros these
            // two bytes (0x41E2CB). Other variants, including nonzero flags in
            // firmware default/legacy formats, require their own proven source
            // kind rather than being silently interpreted as this capture type.
            if (frame[6] != 0 || frame[7] != 0)
                throw new FormatException("DPI report contains unsupported native SET format flags.");
            for (int index = 52; index < CapturedSetFrameBytes; index++)
                if (frame[index] != 0) throw new FormatException("DPI SET report padding must be zero.");

            int sum = 0;
            for (int index = 3; index <= 49; index++) sum += frame[index];
            ushort actual = (ushort)((frame[50] << 8) | frame[51]);
            if ((ushort)sum != actual) throw new FormatException("DPI report payload checksum does not match.");
            if (frame[24] < 1 || frame[24] > 8) throw new FormatException("Recorded DPI stage must be one-based 1..8.");

            var values = new ushort[8];
            for (int index = 0; index < values.Length; index++)
            {
                values[index] = (ushort)(frame[8 + index] | (frame[16 + index] << 8));
                if ((frame[5] & (1 << index)) != 0 && values[index] == 0)
                    throw new FormatException("An enabled DPI stage has a zero value.");
            }
            // Preserve all eight raw uint16 values, including disabled slots.
            // Do not invent an unproven DPI limit/step from another mouse model.
            return new DpiSettingsTable(frame, values, actual);
        }
    }
}
