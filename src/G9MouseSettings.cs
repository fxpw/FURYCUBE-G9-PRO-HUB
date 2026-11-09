using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;

namespace Furycube.G9.Settings
{
    public enum MouseSettingsSourceKind { FirmwareLogicalProfile = 1, CapturedOutgoingArchive = 2 }

    public sealed class MouseSettingOption
    {
        public string Value { get; private set; }
        public string Name { get; private set; }
        internal MouseSettingOption(string value, string name) { Value = value; Name = name; }
    }

    public sealed class MouseSettingField
    {
        public string Id { get; private set; }
        public string Name { get; private set; }
        public string Type { get; private set; }
        public string Value { get; private set; }
        public string Unit { get; private set; }
        public int Min { get; private set; }
        public int Max { get; private set; }
        public int Step { get; private set; }
        public ReadOnlyCollection<MouseSettingOption> Options { get; private set; }
        public bool CanWrite { get; private set; }
        public string UnsupportedReason { get; private set; }
        internal MouseSettingField(string id, string name, string type, string value, string unit,
            int min, int max, int step, MouseSettingOption[] options, bool writable, string reason)
        {
            Id = id; Name = name; Type = type; Value = value; Unit = unit; Min = min; Max = max; Step = step;
            Options = Array.AsReadOnly(options ?? new MouseSettingOption[0]); CanWrite = writable; UnsupportedReason = reason;
        }
    }

    // This is a logical RAM snapshot, NOT a flash/NVM image. GET 0x0A starts at
    // 0x401038: poll2, sensor47, alignment1, sensorCRC_LE2, light8,
    // lightCRC_LE2, keys54, and only the LOW byte of the key checksum.
    // A captured SET archive has no invented alignment/key bytes and cannot be
    // represented as a complete 117-byte profile.
    public sealed class MouseSettingsProfile
    {
        readonly byte[] body, sensor, light, polling;
        public int ProfileId { get; private set; }
        public MouseSettingsSourceKind SourceKind { get; private set; }
        public bool IsDeviceReadback { get { return SourceKind == MouseSettingsSourceKind.FirmwareLogicalProfile; } }
        public bool IsCompleteProfile { get { return body != null; } }
        public bool HasSensor { get { return sensor != null; } }
        public bool HasLight { get { return light != null; } }
        public bool HasPolling { get { return polling != null; } }
        public byte? EnabledStageMask { get { return sensor == null ? (byte?)null : sensor[2]; } }
        public byte? SelectedStage { get { return sensor == null ? (byte?)null : sensor[21]; } }
        public ReadOnlyCollection<ushort> DpiValues { get {
            if (sensor == null) return null; var result = new ushort[8];
            for (int i = 0; i < 8; i++) result[i] = (ushort)(sensor[5 + i] | (sensor[13 + i] << 8));
            return Array.AsReadOnly(result);
        } }
        internal MouseSettingsProfile(int profile, MouseSettingsSourceKind kind, byte[] all, byte[] s, byte[] l, byte[] p)
        { ProfileId = profile; SourceKind = kind; body = Clone(all); sensor = Clone(s); light = Clone(l); polling = Clone(p); }
        static byte[] Clone(byte[] value) { return value == null ? null : (byte[])value.Clone(); }
        public byte[] GetBodyCopy() { if (body == null) throw new InvalidOperationException("An outgoing archive is not a complete RAM profile."); return Clone(body); }
        public byte[] GetSensorBodyCopy() { return Clone(sensor); }
        public byte[] GetLightBodyCopy() { return Clone(light); }
        public byte[] GetPollingBodyCopy() { return Clone(polling); }
    }

    public sealed class MouseSettingsEdit
    {
        readonly byte[] packet;
        public byte ReportId { get; private set; }
        public byte[] Packet { get { return (byte[])packet.Clone(); } }
        public int ExpectedProfile { get; private set; }
        public MouseSettingsProfile Updated { get; private set; }
        public string FieldId { get; private set; }
        internal MouseSettingsEdit(string field, byte report, byte[] bytes, MouseSettingsProfile updated)
        { FieldId = field; ReportId = report; packet = (byte[])bytes.Clone(); Updated = updated; ExpectedProfile = updated.ProfileId; }
    }

    public static class MouseSettingsCodec
    {
        // Evidence boundaries (original G9/C9 MS_1 branch, not another sensor):
        // 0x41E200 SET04; UI angle/ripple cache0x41896A/0x4189C2 and
        // handlers0x41ADAF/0x41ADEF map +944=ripple->body1,+948=angle->body0.
        // DPI split-byte uint16 is confirmed against the complete captured
        // SET04 and firmware's OLED formatter0x3D328/sensorapply0x2FDCC.
        // OEM numeric edit0x41B510 clamps50..22000 (0x41B5D9,0x41B648),
        // slider1..320 and refresh0x41904A use50-step <=10000,100 above.
        // This is an OEM configured-value/UI range, NOT an independently
        // measured physical sensor resolution. Captures reach12000 only.
        // 0x41DF48 encodes Light8 preserving RGB/effect nibbles. Native UI
        // +930/light6 is displayed x0.5 MINUTES (0x419B53,float480460=0.5);
        // +92C/packedhighnibbles is displayed N MINUTES (0x419890).
        // Public level1Seconds=N*30 avoids lossy half-minute integer values.
        // Debounce UI0x4192F9/0x41B341 displays light7*2ms; slider2..25.
        // Native0x41E05B SET06 sets low nibble1..4; high nibble is preserved.
        // GET04/0A schema follows firmware0x33B84, not broken native0x420560.
        public const int AllDataReportBytes = 120;
        public const int LogicalProfileBodyBytes = 117;
        public const int SensorReadReportBytes = 52;
        public const int SensorSetReportBytes = 56;
        public const int LightSetReportBytes = 15;
        public const int PollingReportBytes = 9;

        static void Require(bool ok, string text) { if (!ok) throw new FormatException(text); }
        static void Profile(int profile) { if (profile < 1 || profile > 5) throw new ArgumentOutOfRangeException("expectedProfile"); }
        static byte[] Slice(byte[] data, int start, int length) { var result = new byte[length]; Buffer.BlockCopy(data, start, result, 0, length); return result; }
        static int Sum(byte[] data, int start, int length) { int sum = 0; for (int i = start; i < start + length; i++) sum += data[i]; return sum & 65535; }
        static void Header(byte[] report, int id, int length, int profile)
        {
            Profile(profile); if (report == null) throw new ArgumentNullException("report");
            Require(report.Length == length && report[0] == id && report[1] == length && report[2] == profile,
                "Wrong report ID, exact length or profile selector.");
        }
        static void ZeroTail(byte[] frame, int start) { for (int i = start; i < frame.Length; i++) Require(frame[i] == 0, "Nonzero report padding."); }
        static void ValidateSensorBody(byte[] sensor)
        {
            Require(sensor.Length == 47, "Sensor body must be 47 bytes.");
            Require(sensor[21] >= 1 && sensor[21] <= 8, "DPI stage must be 1..8.");
            Require(sensor[2] != 0, "At least one DPI stage must be enabled.");
            for (int i = 0; i < 8; i++) Require((sensor[2] & (1 << i)) == 0 || (sensor[5 + i] | sensor[13 + i]) != 0,
                "An enabled DPI stage cannot have zero DPI.");
            // Flags/reserved bytes not edited below are preserved. Do not impose
            // the native outgoing encoder's zero[3..4] on a RAM readback.
        }
        static void ValidatePollingBody(byte[] poll) { Require(poll.Length == 2 && (poll[0] ^ poll[1]) == 255, "Polling complement mismatch."); }
        public static MouseSettingsProfile DecodeAllData(byte[] report, int expectedProfile)
        {
            Header(report, 0x0A, AllDataReportBytes, expectedProfile);
            byte[] body = Slice(report, 3, 117), sensor = Slice(body, 2, 47), light = Slice(body, 52, 8), poll = Slice(body, 0, 2);
            ValidateSensorBody(sensor); ValidatePollingBody(poll);
            Require(Sum(sensor, 0, 47) == (body[50] | (body[51] << 8)), "Logical sensor checksum mismatch.");
            Require(Sum(light, 0, 8) == (body[60] | (body[61] << 8)), "Logical light checksum mismatch.");
            Require((Sum(body, 62, 54) & 255) == body[116], "Truncated logical key checksum low byte mismatch.");
            return new MouseSettingsProfile(expectedProfile, MouseSettingsSourceKind.FirmwareLogicalProfile, body, sensor, light, poll);
        }
        // This supports the statically proven firmware GET schema, separately
        // from the captured original HUB's 56-byte SET schema. Native 0x420560
        // incoming DPI arithmetic is not used: low/high are plain uint16 bytes.
        public static byte[] DecodeSensor(byte[] report, int expectedProfile)
        {
            Header(report, 4, SensorReadReportBytes, expectedProfile);
            byte[] body = Slice(report, 3, 47); ValidateSensorBody(body);
            Require(Sum(body, 0, 47) == ((report[50] << 8) | report[51]), "Sensor report checksum mismatch."); return body;
        }
        public static byte[] DecodePolling(byte[] report, int expectedProfile)
        {
            Header(report, 6, PollingReportBytes, expectedProfile); ZeroTail(report, 5);
            byte[] body = Slice(report, 3, 2); ValidatePollingBody(body); return body;
        }
        public static MouseSettingsProfile DecodeCapturedSetFrames(int expectedProfile, byte[] sensor56, byte[] light15, byte[] polling9)
        {
            Profile(expectedProfile); byte[] sensor = null, light = null, poll = null;
            if (sensor56 != null) {
                Header(sensor56, 4, SensorSetReportBytes, expectedProfile); ZeroTail(sensor56, 52);
                Require(sensor56[6] == 0 && sensor56[7] == 0, "Captured native sensor SET reserved format mismatch.");
                byte[] read = Slice(sensor56, 0, 52); read[1] = 52; sensor = DecodeSensor(read, expectedProfile);
            }
            if (light15 != null) {
                Header(light15, 5, LightSetReportBytes, expectedProfile); ZeroTail(light15, 13); light = Slice(light15, 3, 8);
                Require(Sum(light, 0, 8) == ((light15[11] << 8) | light15[12]), "Captured light checksum mismatch.");
            }
            if (polling9 != null) poll = DecodePolling(polling9, expectedProfile);
            Require(sensor != null || light != null || poll != null, "The archive contains no supported reports.");
            return new MouseSettingsProfile(expectedProfile, MouseSettingsSourceKind.CapturedOutgoingArchive, null, sensor, light, poll);
        }
        public static void ValidateSensorMatches(MouseSettingsProfile profile, byte[] report52) { Equal(profile.GetSensorBodyCopy(), DecodeSensor(report52, profile.ProfileId), "Sensor read differs from logical profile."); }
        public static void ValidatePollingMatches(MouseSettingsProfile profile, byte[] report9) { Equal(profile.GetPollingBodyCopy(), DecodePolling(report9, profile.ProfileId), "Polling read differs from logical profile."); }
        static void Equal(byte[] a, byte[] b, string reason) { Require(a != null && b != null && a.Length == b.Length, reason); for (int i = 0; i < a.Length; i++) Require(a[i] == b[i], reason); }

        public static byte[] BuildSetPacket(MouseSettingsProfile profile, byte reportId)
        {
            if (profile == null) throw new ArgumentNullException("profile"); byte[] data; int length, checksumAt;
            switch (reportId) {
                case 4: data = profile.GetSensorBodyCopy(); length = 56; checksumAt = 50; break;
                case 5: data = profile.GetLightBodyCopy(); length = 15; checksumAt = 11; break;
                case 6: data = profile.GetPollingBodyCopy(); length = 9; checksumAt = -1; break;
                case 7: throw new NotSupportedException("GET 0x07 does not expose the complete SET 0x07 baseline; RF power writes are disabled.");
                default: throw new NotSupportedException("Only proven SET 0x04, 0x05 and 0x06 are supported.");
            }
            if (data == null) throw new InvalidOperationException("This source has no complete body for the requested report.");
            var frame = new byte[length]; frame[0] = reportId; frame[1] = (byte)length; frame[2] = (byte)profile.ProfileId;
            Buffer.BlockCopy(data, 0, frame, 3, data.Length);
            if (checksumAt >= 0) { int sum = Sum(data, 0, data.Length); frame[checksumAt] = (byte)(sum >> 8); frame[checksumAt + 1] = (byte)sum; }
            return frame;
        }

        public static void ValidateSetPacket(byte[] packet, int expectedProfile)
        {
            if (packet == null || packet.Length == 0) throw new ArgumentNullException("packet");
            if (packet[0] == 4) {
                Header(packet, 4, SensorSetReportBytes, expectedProfile); ZeroTail(packet, 52);
                var read = Slice(packet, 0, 52); read[1] = 52; DecodeSensor(read, expectedProfile);
            }
            else if (packet[0] == 5) {
                Header(packet, 5, LightSetReportBytes, expectedProfile); ZeroTail(packet, 13);
                Require(Sum(packet, 3, 8) == ((packet[11] << 8) | packet[12]), "Light SET checksum mismatch.");
            }
            else if (packet[0] == 6) DecodePolling(packet, expectedProfile);
            else throw new NotSupportedException("Only SET 0x04, 0x05 and 0x06 pass the settings whitelist.");
        }

        static string Number(int value) { return value.ToString(CultureInfo.InvariantCulture); }
        static MouseSettingField Field(MouseSettingsProfile p, string id, string name, string type, string value, string unit,
            int min, int max, int step, bool available, MouseSettingOption[] options)
        {
            bool writable = available && p.IsDeviceReadback;
            return new MouseSettingField(id, name, type, value, unit, min, max, step, options, writable,
                available ? (writable ? null : "Исторический исходящий пакет; текущее состояние мыши не подтверждено.") :
                (value == null ? "Нет достоверного исходного значения." : "Исходное значение вне подтверждённого формата штатного G9; оно сохранено без подмены."));
        }
        static MouseSettingField Unsupported(string id, string name, string reason)
        { return new MouseSettingField(id, name, "unsupported", null, "", 0, 0, 0, null, false, reason); }
        public static MouseSettingField[] GetFields(MouseSettingsProfile p)
        {
            if (p == null) throw new ArgumentNullException("p"); var fields = new List<MouseSettingField>();
            byte[] s = p.GetSensorBodyCopy(), l = p.GetLightBodyCopy(), poll = p.GetPollingBodyCopy();
            fields.Add(Field(p, "dpi.stage", "Выбранный уровень DPI", "integer", s == null ? null : Number(s[21]), "", 1, 8, 1, s != null, null));
            fields.Add(Field(p, "dpi.enabledMask", "Включённые уровни DPI (битовая маска)", "integer", s == null ? null : Number(s[2]), "", 1, 255, 1, s != null, null));
            for (int i = 0; i < 8; i++) {
                fields.Add(Field(p, "dpi.value" + (i + 1), "DPI: уровень " + (i + 1), "dpi", s == null ? null : Number(s[5 + i] | (s[13 + i] << 8)), "DPI", 50, 22000, 50, s != null, null));
                fields.Add(Field(p, "dpi.color" + (i + 1), "Цвет уровня DPI " + (i + 1), "color", s == null ? null : BitConverter.ToString(s, 22 + i * 3, 3).Replace("-", ""), "RGB", 0, 0, 0, s != null, null));
            }
            var toggles = new[] { new MouseSettingOption("0", "Выкл."), new MouseSettingOption("1", "Вкл.") };
            fields.Add(Field(p, "sensor.ripple", "Контроль пульсаций", "choice", s == null ? null : Number(s[1]), "", 0, 1, 1, s != null && s[1] <= 1, toggles));
            fields.Add(Field(p, "sensor.angleSnap", "Коррекция углов", "choice", s == null ? null : Number(s[0]), "", 0, 1, 1, s != null && s[0] <= 1, toggles));
            int rateCode = poll == null ? 0 : (poll[0] & 15); int rate = RateHz(rateCode);
            fields.Add(Field(p, "polling.hz", "Частота опроса", "choice", rate == 0 ? null : Number(rate), "Гц", 125, 1000, 0, poll != null && rate != 0,
                new[] { new MouseSettingOption("125", "125 Гц"), new MouseSettingOption("250", "250 Гц"), new MouseSettingOption("500", "500 Гц"), new MouseSettingOption("1000", "1000 Гц") }));
            fields.Add(Field(p, "debounce.ms", "Время отклика кнопок", "integer", l == null ? null : Number(l[7] * 2), "мс", 4, 50, 2, l != null && l[7] >= 2 && l[7] <= 25, null));
            fields.Add(Field(p, "sleep.level1Seconds", "Переход в спящий режим первого уровня", "integer", l == null ? null : Number(l[6] * 30), "с", 30, 1800, 30, l != null && l[6] >= 1 && l[6] <= 60, null));
            int seconds = l == null ? 0 : ((l[1] & 0xF0) | (l[2] >> 4));
            fields.Add(Field(p, "sleep.idleMinutes", "Таймер режима ожидания", "integer", l == null ? null : Number(seconds), "мин", 1, 60, 1, l != null && seconds >= 1 && seconds <= 60, null));
            fields.Add(Unsupported("sensor.motionSync", "MotionSync", "Скрыто в штатном G9/C9; действие прошивки для этого поля не подтверждено."));
            fields.Add(Unsupported("sensor.lod", "Высота отрыва (LOD)", "Скрыто в штатном G9/C9; точное изменение для PAW3311 не подтверждено."));
            fields.Add(Unsupported("lighting.mode", "Подсветка корпуса", "Раздел скрыт в штатном G9/C9; байты подсветки сохраняются без изменения."));
            fields.Add(Unsupported("rf.power", "Мощность радиопередатчика", "GET 0x07 возвращает другой диапазон RAM, чем принимает SET 0x07. Полной исходной копии нет."));
            return fields.ToArray();
        }
        static int RateHz(int code) { return code == 1 ? 125 : code == 2 ? 250 : code == 3 ? 500 : code == 4 ? 1000 : 0; }
        static int Parse(string value, int min, int max)
        {
            int parsed; if (!Int32.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out parsed) || parsed < min || parsed > max)
                throw new ArgumentOutOfRangeException("value", "Setting value is outside its proven range."); return parsed;
        }
        public static MouseSettingsEdit BuildEdit(MouseSettingsProfile baseline, string field, string value)
        {
            if (baseline == null) throw new ArgumentNullException("baseline");
            if (!baseline.IsDeviceReadback || !baseline.IsCompleteProfile) throw new InvalidOperationException("An edit requires a complete fresh logical readback, not a saved outgoing archive.");
            MouseSettingField selected = null; foreach (var item in GetFields(baseline)) if (item.Id == field) selected = item;
            if (selected == null) throw new ArgumentException("Unknown G9 setting field.", "field");
            if (!selected.CanWrite) throw new NotSupportedException(selected.UnsupportedReason);
            byte[] s = baseline.GetSensorBodyCopy(), l = baseline.GetLightBodyCopy(), poll = baseline.GetPollingBodyCopy(); byte report;
            if (field == "dpi.stage") { int stage = Parse(value, 1, 8); if ((s[2] & (1 << (stage - 1))) == 0) throw new ArgumentException("The selected DPI stage is disabled."); s[21] = (byte)stage; report = 4; }
            else if (field == "dpi.enabledMask") {
                int mask = Parse(value, 1, 255); if ((mask & (1 << (s[21] - 1))) == 0) throw new ArgumentException("Cannot disable the currently selected DPI stage.");
                for (int i = 0; i < 8; i++) if ((mask & (1 << i)) != 0 && (s[5 + i] | s[13 + i]) == 0) throw new ArgumentException("Set a nonzero DPI value before enabling its stage.");
                s[2] = (byte)mask; report = 4;
            }
            else if (field.StartsWith("dpi.value", StringComparison.Ordinal)) {
                int index = Parse(field.Substring(9), 1, 8) - 1, dpi = Parse(value, 50, 22000);
                if ((dpi <= 10000 && dpi % 50 != 0) || (dpi > 10000 && dpi % 100 != 0)) throw new ArgumentException("Stock DPI step is50 through10000, then100.");
                s[5 + index] = (byte)dpi; s[13 + index] = (byte)(dpi >> 8); report = 4;
            }
            else if (field.StartsWith("dpi.color", StringComparison.Ordinal)) {
                int index = Parse(field.Substring(9), 1, 8) - 1;
                if (value == null || value.Length != 6) throw new ArgumentException("Color must contain exactly six RGB hexadecimal digits.");
                for (int i = 0; i < 3; i++) { byte part; if (!Byte.TryParse(value.Substring(i * 2, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out part)) throw new ArgumentException("Invalid RGB color."); s[22 + index * 3 + i] = part; }
                report = 4;
            }
            else if (field == "sensor.ripple") { s[1] = (byte)Parse(value, 0, 1); report = 4; }
            else if (field == "sensor.angleSnap") { s[0] = (byte)Parse(value, 0, 1); report = 4; }
            else if (field == "polling.hz") {
                int hz = Parse(value, 125, 1000), code = hz == 125 ? 1 : hz == 250 ? 2 : hz == 500 ? 3 : hz == 1000 ? 4 : 0;
                if (code == 0) throw new ArgumentException("Only125,250,500 or1000Hz are proven for the stock G9 UI.");
                poll[0] = (byte)((poll[0] & 0xF0) | code); poll[1] = (byte)~poll[0]; report = 6;
            }
            else if (field == "debounce.ms") { int ms = Parse(value, 4, 50); if (ms % 2 != 0) throw new ArgumentException("Debounce uses a2ms step."); l[7] = (byte)(ms / 2); report = 5; }
            else if (field == "sleep.level1Seconds") { int seconds = Parse(value, 30, 1800); if (seconds % 30 != 0) throw new ArgumentException("First-level sleep uses a30second UI step."); l[6] = (byte)(seconds / 30); report = 5; }
            else if (field == "sleep.idleMinutes") { int min = Parse(value, 1, 60); l[1] = (byte)((l[1] & 15) | (min & 0xF0)); l[2] = (byte)((l[2] & 15) | ((min & 15) << 4)); report = 5; }
            else throw new NotSupportedException("This field has no proven G9 serializer.");
            ValidateSensorBody(s); ValidatePollingBody(poll);
            byte[] all = baseline.GetBodyCopy(); Buffer.BlockCopy(s, 0, all, 2, 47); Buffer.BlockCopy(l, 0, all, 52, 8); Buffer.BlockCopy(poll, 0, all, 0, 2);
            int sum = Sum(s, 0, 47); all[50] = (byte)sum; all[51] = (byte)(sum >> 8);
            sum = Sum(l, 0, 8); all[60] = (byte)sum; all[61] = (byte)(sum >> 8);
            var updated = new MouseSettingsProfile(baseline.ProfileId, baseline.SourceKind, all, s, l, poll);
            return new MouseSettingsEdit(field, report, BuildSetPacket(updated, report), updated);
        }
    }
}
