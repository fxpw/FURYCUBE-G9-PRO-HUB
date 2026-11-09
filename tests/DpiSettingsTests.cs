using System;
using System.Collections.Generic;
using Furycube.G9.Dpi;

internal static class DpiSettingsTests
{
    const string Captured = "04380101013f0000204080c040e0000003060c121f2e000002ff000000ff000000ffffff0000ffffff00ffff4000ffffff0110ab00000000";
    static int count;
    static void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL " + name); count++; }
    static byte[] Fixture()
    {
        var result = new byte[Captured.Length / 2];
        for (int index = 0; index < result.Length; index++) result[index] = Convert.ToByte(Captured.Substring(index * 2, 2), 16);
        return result;
    }
    static void Checksum(byte[] frame)
    {
        int sum = 0;
        for (int index = 3; index <= 49; index++) sum += frame[index];
        frame[50] = (byte)(sum >> 8); frame[51] = (byte)sum;
    }
    static bool Reject(byte[] frame)
    { try { DpiSettingsDecoder.DecodeCapturedSet(frame, 1); return false; } catch (FormatException) { return true; } }
    static byte[] WithValues(ushort[] values)
    {
        var frame = Fixture();
        for (int index = 0; index < 8; index++)
        { frame[8 + index] = (byte)values[index]; frame[16 + index] = (byte)(values[index] >> 8); }
        Checksum(frame); return frame;
    }

    static int Main()
    {
        try
        {
            byte[] frame = Fixture();
            DpiSettingsTable table = DpiSettingsDecoder.DecodeCapturedSet(frame, 1);
            Check(table.SourceKind == DpiSettingsSourceKind.CapturedSet04 && !table.IsDeviceReadback, "explicit outgoing capture source, no readback claim");
            Check(table.ProfileId == 1 && table.EnabledStageMask == 0x3F && table.RecordedStage == 2, "captured profile, enabled mask and one-based stage");
            Check(table.Checksum == 0x10AB && table.DpiValues.Count == 8, "captured checksum and eight uint16 slots");
            ushort[] expected = { 800, 1600, 3200, 4800, 8000, 12000, 0, 0 };
            for (int index = 0; index < 8; index++) Check(table.DpiValues[index] == expected[index], "captured numeric DPI slot " + (index + 1));
            Check(table.RecordedDpi == 1600, "recorded stage2 is1600, no lookup or plus1");
            Check(table.GetEnabledStageDpi(7) == null && table.GetEnabledStageDpi(8) == null, "disabled zero slots unavailable");
            Check(table.SourceSha256.Length == 64 && table.SourceSha256 == DpiSettingsDecoder.DecodeCapturedSet(Fixture(), 1).SourceSha256,
                "stable exact-source fingerprint");
            frame[8] = 0;
            Check(table.DpiValues[0] == 800 && table.GetWireFrameCopy()[8] == 0x20, "caller input mutation cannot alter immutable table");
            var copy = table.GetWireFrameCopy(); copy[16] = 0;
            Check(table.GetWireFrameCopy()[16] == 3, "returned wire copy cannot alter saved source");
            bool immutable = false;
            try { ((IList<ushort>)table.DpiValues)[0] = 1234; } catch (NotSupportedException) { immutable = true; }
            Check(immutable && table.DpiValues[0] == 800, "read-only DPI array cannot be mutated");

            // These vectors were produced by the real original x86 encoder in
            // offline Unicorn, including its internal-unit200/201 boundary.
            ushort[][] encoderVectors = {
                new ushort[] { 50,100,800,1600,3200,4800,8000,10000 },
                new ushort[] { 10100,12000,13000,14000,16000,20000,24000,26000 }
            };
            foreach (ushort[] vector in encoderVectors)
            {
                frame = WithValues(vector); frame[5] = 0xFF; Checksum(frame);
                table = DpiSettingsDecoder.DecodeCapturedSet(frame, 1);
                for (int index = 0; index < 8; index++) Check(table.GetEnabledStageDpi(index + 1) == vector[index], "native encoder numeric vector " + vector[index]);
            }
            // The decoder preserves wire uint16 values instead of inventing
            // another model's upper DPI limit or a default for an unknown code.
            frame = WithValues(new ushort[] { 65535,1600,3200,4800,8000,12000,100,100 });
            table = DpiSettingsDecoder.DecodeCapturedSet(frame, 1);
            Check(table.DpiValues[0] == 65535 && table.DpiValues[7] == 100 && table.GetEnabledStageDpi(8) == null,
                "raw uint16 and disabled nonzero slots preserved, no hardware-support inference");
            frame = Fixture(); frame[5] = 0; Checksum(frame);
            table = DpiSettingsDecoder.DecodeCapturedSet(frame, 1);
            Check(table.RecordedDpi == null && table.EnabledStageMask == 0, "empty mask is data, never a fallback DPI");
            frame = Fixture(); frame[5] = 1; Checksum(frame);
            table = DpiSettingsDecoder.DecodeCapturedSet(frame, 1);
            Check(table.RecordedStage == 2 && table.RecordedDpi == null, "disabled recorded stage cannot invent an enabled value");
            for (int stage = 1; stage <= 6; stage++)
            {
                frame = Fixture(); frame[24] = (byte)stage; Checksum(frame);
                Check(DpiSettingsDecoder.DecodeCapturedSet(frame, 1).RecordedDpi == expected[stage - 1], "stage lookup agrees with original Thumb OLED fixture " + stage);
            }
            for (int profile = 1; profile <= 5; profile++)
            {
                frame = Fixture(); frame[2] = (byte)profile;
                Check(DpiSettingsDecoder.DecodeCapturedSet(frame, profile).ProfileId == profile, "bounded profile " + profile);
            }

            frame = Fixture(); frame[0] = 0x0A; Check(Reject(frame), "all-data report is not SET0x04");
            frame = Fixture(); frame[0] = 0x10; Check(Reject(frame), "update-entry report is never parsed");
            frame = Fixture(); frame[1] = 52; Check(Reject(frame), "encoded length mismatch");
            frame = Fixture(); frame[2] = 2; Check(Reject(frame), "wrong expected profile");
            foreach (byte profile in new byte[] { 0, 6, 255 }) { frame = Fixture(); frame[2] = profile; Check(Reject(frame), "out-of-range wire profile " + profile); }
            foreach (int position in new int[] { 6, 7, 52, 53, 54, 55 })
            { frame = Fixture(); frame[position] = 1; Checksum(frame); Check(Reject(frame), "unsupported flag/reserved padding " + position); }
            frame = Fixture(); frame[10] ^= 1; Check(Reject(frame), "changed payload rejected by captured checksum");
            frame = Fixture(); frame[50] ^= 1; Check(Reject(frame), "changed checksum rejected");
            frame = Fixture(); byte swap = frame[50]; frame[50] = frame[51]; frame[51] = swap; Check(Reject(frame), "checksum must be big-endian");
            foreach (byte stage in new byte[] { 0, 9, 255 }) { frame = Fixture(); frame[24] = stage; Checksum(frame); Check(Reject(frame), "out-of-range recorded stage " + stage); }
            frame = Fixture(); frame[8] = frame[16] = 0; Checksum(frame); Check(Reject(frame), "zero-valued enabled stage rejected");
            Check(Reject(new byte[0]) && Reject(new byte[55]) && Reject(new byte[57]), "invalid frame lengths");
            frame = new byte[52]; Array.Copy(Fixture(), frame, 52); frame[1] = 52;
            Check(Reject(frame), "computed GET52 layout is not silently accepted as a captured SET source");
            bool badArgument = false;
            try { DpiSettingsDecoder.DecodeCapturedSet(null, 1); } catch (ArgumentNullException) { badArgument = true; }
            Check(badArgument, "null input rejected");
            foreach (int profile in new int[] { 0, 6, 256 })
            { badArgument = false; try { DpiSettingsDecoder.DecodeCapturedSet(Fixture(), profile); } catch (ArgumentOutOfRangeException) { badArgument = true; } Check(badArgument, "invalid expected-profile argument " + profile); }
            foreach (int stage in new int[] { 0, 9 })
            { badArgument = false; try { table.GetEnabledStageDpi(stage); } catch (ArgumentOutOfRangeException) { badArgument = true; } Check(badArgument, "invalid stage lookup argument " + stage); }
            Console.WriteLine("PASS " + count + " captured SET0x04 DPI, source kind, immutable table, checksum, boundaries and malformed-frame fixtures; no HID APIs called.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
    }
}
