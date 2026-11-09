using System;
using System.Collections.Generic;
using Furycube.G9.Settings;

internal static class MouseSettingsTests
{
    const string SensorCapture = "04380101013f0000204080c040e0000003060c121f2e000002ff000000ff000000ffffff0000ffffff00ffff4000ffffff0110ab00000000";
    const string LightCapture = "050f010003a800ff00140401c20000";
    const string PollCapture = "06090144bb00000000";
    static int passed;
    static byte[] Hex(string text) { var bytes = new byte[text.Length / 2]; for (int i = 0; i < bytes.Length; i++) bytes[i] = Convert.ToByte(text.Substring(i * 2, 2), 16); return bytes; }
    static void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL " + name); passed++; }
    static void Reject(Action action, string name) { bool rejected = false; try { action(); } catch (ArgumentException) { rejected = true; } catch (FormatException) { rejected = true; } catch (InvalidOperationException) { rejected = true; } catch (NotSupportedException) { rejected = true; } Check(rejected, name); }
    static bool Equal(byte[] a, byte[] b) { if (a.Length != b.Length) return false; for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false; return true; }
    static MouseSettingField Field(MouseSettingsProfile p, string id) { foreach (var field in MouseSettingsCodec.GetFields(p)) if (field.Id == id) return field; throw new Exception("Missing field " + id); }
    // A synthetic firmware-schema fixture, assembled from the three real
    // outgoing captures plus deliberately distinctive unknown/key bytes. It
    // tests the statically proven RAM117 layout; it is not labelled a captured
    // GET0A response or a successful physical readback.
    static byte[] AllData()
    {
        byte[] s = Hex(SensorCapture), l = Hex(LightCapture), p = Hex(PollCapture), frame = new byte[120];
        frame[0] = 10; frame[1] = 120; frame[2] = 1;
        Buffer.BlockCopy(p, 3, frame, 3, 2); Buffer.BlockCopy(s, 3, frame, 5, 47);
        frame[52] = 0xA7; frame[53] = s[51]; frame[54] = s[50];
        Buffer.BlockCopy(l, 3, frame, 55, 8); frame[63] = l[12]; frame[64] = l[11];
        int sum = 0; for (int i = 65; i < 119; i++) { frame[i] = (byte)(i * 3); sum += frame[i]; } frame[119] = (byte)sum;
        return frame;
    }
    static void SensorCrc(byte[] frame)
    { int sum = 0; for (int i = 5; i < 52; i++) sum += frame[i]; frame[53] = (byte)sum; frame[54] = (byte)(sum >> 8); }
    static void Preserved(MouseSettingsProfile before, MouseSettingsEdit edit, params int[] allowed)
    {
        byte[] a = before.GetBodyCopy(), b = edit.Updated.GetBodyCopy(); var set = new HashSet<int>(allowed);
        for (int i = 0; i < 117; i++) if (!set.Contains(i)) Check(a[i] == b[i], "untouched logical byte " + i + " for " + edit.FieldId);
        Check(a[49] == 0xA7 && b[49] == 0xA7, "RAM alignment preserved");
        MouseSettingsCodec.ValidateSetPacket(edit.Packet, 1);
        byte[] roundTrip = new byte[120]; roundTrip[0] = 10; roundTrip[1] = 120; roundTrip[2] = 1;
        Buffer.BlockCopy(b, 0, roundTrip, 3, 117); Check(Equal(MouseSettingsCodec.DecodeAllData(roundTrip, 1).GetBodyCopy(), b), "edited logical profile validates");
    }
    static int Main()
    {
        try {
            var archive = MouseSettingsCodec.DecodeCapturedSetFrames(1, Hex(SensorCapture), Hex(LightCapture), Hex(PollCapture));
            Check(!archive.IsDeviceReadback && !archive.IsCompleteProfile && archive.SourceKind == MouseSettingsSourceKind.CapturedOutgoingArchive, "archive provenance");
            Check(Equal(MouseSettingsCodec.BuildSetPacket(archive, 4), Hex(SensorCapture)), "exact original sensor encoder roundtrip");
            Check(Equal(MouseSettingsCodec.BuildSetPacket(archive, 5), Hex(LightCapture)), "exact original light encoder roundtrip");
            Check(Equal(MouseSettingsCodec.BuildSetPacket(archive, 6), Hex(PollCapture)), "exact original polling encoder roundtrip");
            Check(!Field(archive, "debounce.ms").CanWrite, "archive cannot masquerade as current settings");
            Reject(delegate { archive.GetBodyCopy(); }, "no fabricated complete archive");
            Reject(delegate { MouseSettingsCodec.BuildEdit(archive, "debounce.ms", "16"); }, "archive cannot authorize edit");
            var partial = MouseSettingsCodec.DecodeCapturedSetFrames(1, null, Hex(LightCapture), null);
            Check(partial.HasLight && !partial.HasSensor && !partial.HasPolling, "honest partial archive");
            Reject(delegate { MouseSettingsCodec.BuildSetPacket(partial, 4); }, "missing sensor baseline rejected");
            byte[] data = AllData(); var baseline = MouseSettingsCodec.DecodeAllData(data, 1);
            Check(baseline.IsCompleteProfile && baseline.IsDeviceReadback && baseline.ProfileId == 1, "firmware logical source");
            Check(Field(baseline, "debounce.ms").Value == "8" && Field(baseline, "sleep.level1Seconds").Value == "600" && Field(baseline, "sleep.idleMinutes").Value == "10", "units and two distinct sleep timer locations");
            Check(Field(baseline, "sensor.ripple").Value == "1" && Field(baseline, "sensor.angleSnap").Value == "1" && Field(baseline, "polling.hz").Value == "1000", "native flag and poll meanings");
            ushort[] expected = { 800, 1600, 3200, 4800, 8000, 12000, 0, 0 };
            for (int i = 0; i < 8; i++) Check(baseline.DpiValues[i] == expected[i], "uint16 DPI slot " + (i + 1));
            data[5] = 99; var copy = baseline.GetBodyCopy(); copy[2] = 99;
            Check(baseline.GetSensorBodyCopy()[0] == 1, "immutable input and body copies");
            byte[] sensorRead = new byte[52]; Buffer.BlockCopy(Hex(SensorCapture), 0, sensorRead, 0, 52); sensorRead[1] = 52;
            MouseSettingsCodec.ValidateSensorMatches(baseline, sensorRead); MouseSettingsCodec.ValidatePollingMatches(baseline, Hex(PollCapture)); passed += 2;
            Check(MouseSettingsCodec.DecodeSensor(sensorRead, 1)[5] == 0x20, "GET52 has no incoming plus-one corruption");
            var edit = MouseSettingsCodec.BuildEdit(baseline, "debounce.ms", "16");
            Check(edit.ReportId == 5 && edit.ExpectedProfile == 1 && edit.Packet[10] == 8 && Field(edit.Updated, "debounce.ms").Value == "16", "debounce edit");
            Preserved(baseline, edit, 59, 60, 61);
            foreach (int value in new[] { 1, 15, 16, 31, 32, 60 }) {
                edit = MouseSettingsCodec.BuildEdit(baseline, "sleep.idleMinutes", value.ToString());
                Check(Field(edit.Updated, "sleep.idleMinutes").Value == value.ToString(), "packed seconds " + value);
                Check((edit.Packet[4] & 15) == 3 && (edit.Packet[5] & 15) == 8, "speed and brightness lower nibbles preserved");
                Preserved(baseline, edit, 53, 54, 60, 61);
            }
            edit = MouseSettingsCodec.BuildEdit(baseline, "sleep.level1Seconds", "1800"); Preserved(baseline, edit, 58, 60, 61);
            foreach (string seconds in new[] { "30", "60", "600", "1800" }) {
                edit = MouseSettingsCodec.BuildEdit(baseline, "sleep.level1Seconds", seconds);
                Check(edit.Packet[9] == int.Parse(seconds) / 30 && Field(edit.Updated, "sleep.level1Seconds").Value == seconds, "half-minute OEM UI scaling " + seconds);
            }
            foreach (string seconds in new[] { "1", "29", "31", "1801" })
                Reject(delegate { MouseSettingsCodec.BuildEdit(baseline, "sleep.level1Seconds", seconds); }, "first sleep range and step " + seconds);
            foreach (string minutes in new[] { "0", "61" })
                Reject(delegate { MouseSettingsCodec.BuildEdit(baseline, "sleep.idleMinutes", minutes); }, "standby range " + minutes);
            foreach (string oldId in new[] { "sleep.level1Minutes", "sleep.idleSeconds" })
                Reject(delegate { MouseSettingsCodec.BuildEdit(baseline, oldId, "10"); }, "reject obsolete incorrect units " + oldId);
            edit = MouseSettingsCodec.BuildEdit(baseline, "sensor.ripple", "0"); Preserved(baseline, edit, 3, 50, 51);
            Check(edit.Packet[3] == 1 && edit.Packet[4] == 0, "ripple does not change angle");
            edit = MouseSettingsCodec.BuildEdit(baseline, "sensor.angleSnap", "0"); Preserved(baseline, edit, 2, 50, 51);
            Check(edit.Packet[3] == 0 && edit.Packet[4] == 1, "angle does not change ripple");
            foreach (string hz in new[] { "125", "250", "500", "1000" }) {
                edit = MouseSettingsCodec.BuildEdit(baseline, "polling.hz", hz); Preserved(baseline, edit, 0, 1);
                Check(Field(edit.Updated, "polling.hz").Value == hz && (edit.Packet[3] & 0xF0) == 0x40 && (edit.Packet[3] ^ edit.Packet[4]) == 255, "polling nibble and complement " + hz);
            }
            foreach (string dpi in new[] { "50", "10000", "10100", "22000" }) {
                edit = MouseSettingsCodec.BuildEdit(baseline, "dpi.value2", dpi); Preserved(baseline, edit, 8, 16, 50, 51);
                Check(edit.Updated.DpiValues[1].ToString() == dpi, "piecewise DPI boundary " + dpi);
            }
            edit = MouseSettingsCodec.BuildEdit(baseline, "dpi.color8", "1234AB"); Preserved(baseline, edit, 45, 46, 47, 50, 51);
            Check(Field(edit.Updated, "dpi.color8").Value == "1234AB", "eighth RGB triplet");
            edit = MouseSettingsCodec.BuildEdit(baseline, "dpi.stage", "6"); Preserved(baseline, edit, 23, 50, 51);
            edit = MouseSettingsCodec.BuildEdit(baseline, "dpi.enabledMask", "3"); Preserved(baseline, edit, 4, 50, 51);
            Reject(delegate { MouseSettingsCodec.BuildEdit(baseline, "dpi.stage", "7"); }, "cannot select disabled stage");
            Reject(delegate { MouseSettingsCodec.BuildEdit(baseline, "dpi.enabledMask", "1"); }, "cannot disable selected stage");
            Reject(delegate { MouseSettingsCodec.BuildEdit(baseline, "dpi.enabledMask", "255"); }, "cannot enable zero stage");
            foreach (string invalid in new[] { "0", "49", "51", "10050", "22100", "65535", "-50", " 50" })
                Reject(delegate { MouseSettingsCodec.BuildEdit(baseline, "dpi.value1", invalid); }, "DPI range/step " + invalid);
            foreach (string invalid in new[] { "2", "5", "52" }) Reject(delegate { MouseSettingsCodec.BuildEdit(baseline, "debounce.ms", invalid); }, "debounce range/step " + invalid);
            foreach (string id in new[] { "rf.power", "sensor.motionSync", "sensor.lod", "lighting.mode", "polling.wiredHz", "unknown" })
                Reject(delegate { MouseSettingsCodec.BuildEdit(baseline, id, "1"); }, "unproven field " + id);
            Reject(delegate { MouseSettingsCodec.BuildSetPacket(baseline, 7); }, "RF SET gap fail closed");
            foreach (int index in new[] { 0, 1, 2, 3, 5, 53, 54, 55, 63, 64, 65, 119 }) {
                byte[] bad = AllData(); bad[index] ^= 1; Reject(delegate { MouseSettingsCodec.DecodeAllData(bad, 1); }, "logical corruption " + index);
            }
            byte[] unknown = AllData(); unknown[8] = 0xA5; unknown[9] = 0x5A; SensorCrc(unknown);
            var preservesUnknown = MouseSettingsCodec.DecodeAllData(unknown, 1);
            edit = MouseSettingsCodec.BuildEdit(preservesUnknown, "sensor.angleSnap", "0");
            Check(edit.Packet[6] == 0xA5 && edit.Packet[7] == 0x5A, "RAM unknown flags survive known edit; no outgoing-zero assumption");
            MouseSettingsCodec.ValidateSetPacket(edit.Packet, 1); passed++;
            Reject(delegate { MouseSettingsCodec.DecodeCapturedSetFrames(1, edit.Packet, null, null); }, "native outgoing archive has separate reserved schema");
            byte[] wrong = Hex(PollCapture); wrong[5] = 1; Reject(delegate { MouseSettingsCodec.DecodePolling(wrong, 1); }, "poll padding");
            byte[] invalidPacket = edit.Packet; invalidPacket[55] = 1; Reject(delegate { MouseSettingsCodec.ValidateSetPacket(invalidPacket, 1); }, "SET padding");
            invalidPacket = Hex(LightCapture); invalidPacket[11] ^= 1; Reject(delegate { MouseSettingsCodec.ValidateSetPacket(invalidPacket, 1); }, "light SET checksum");
            Reject(delegate { MouseSettingsCodec.ValidateSetPacket(new byte[] { 0x10 }, 1); }, "loader opcode rejected");
            foreach (int offset in new[] { 56, 57, 61, 62 }) {
                byte[] raw = AllData(); raw[offset] = 0xFF;
                if (offset == 57) raw[56] = 0xF3;
                int sum = 0; for (int i = 55; i < 63; i++) sum += raw[i]; raw[63] = (byte)sum; raw[64] = (byte)(sum >> 8);
                var unusual = MouseSettingsCodec.DecodeAllData(raw, 1);
                string id = offset == 61 ? "sleep.level1Seconds" : offset == 62 ? "debounce.ms" : "sleep.idleMinutes";
                Check(!Field(unusual, id).CanWrite && Field(unusual, id).Value != null, "unusual raw value retained and disabled " + id);
                Reject(delegate { MouseSettingsCodec.BuildEdit(unusual, id, "30"); }, "unusual source not silently replaced " + id);
                Check(Equal(unusual.GetBodyCopy(), Hex(BitConverter.ToString(raw, 3).Replace("-", ""))), "unusual complete source unchanged");
            }
            Console.WriteLine("PASS " + passed + " settings checks; pure fixtures only, no USB."); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error.ToString()); return 1; }
    }
}
