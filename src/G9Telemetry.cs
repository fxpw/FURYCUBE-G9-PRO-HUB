using System;

namespace Furycube.G9.Telemetry
{
    public enum TelemetryReportKind { Battery, DpiStage }
    public enum BatteryState { Discharging = 1, Full = 2, Charging = 3 }

    public sealed class TelemetryUpdate
    {
        public TelemetryReportKind Kind { get; internal set; }
        public byte Command { get; internal set; }
        public int? BatteryPercent { get; internal set; }
        public BatteryState? BatteryState { get; internal set; }
        public int? DpiStage { get; internal set; }
    }

    // Pure decoding only: this module cannot open HID handles or issue USB requests.
    // See docs/TELEMETRY.md for captured fixtures and native/FW references.
    public static class TelemetryProtocol
    {
        public const int InputReportBytes = 5;

        public static bool IsSupportedInputCollection(ushort vendorId, ushort productId,
            ushort usagePage, ushort usage, int inputReportBytes)
        {
            return vendorId == 0x1D57 && (productId == 0x2148 || productId == 0xFA60) &&
                usagePage == 0x000A && usage == 0 && inputReportBytes == InputReportBytes;
        }

        public static bool TryDecodeInput(byte[] report, out TelemetryUpdate update)
        {
            update = null;
            if (report == null || report.Length != InputReportBytes || report[0] != 3 || report[1] != 0xC9)
                return false;

            if (report[2] == 0x40 || report[2] == 0x41)
            {
                // 0x40 is captured and proven in the G9 firmware. The old web HUB
                // also recognizes 0x41 with exactly the same status/percent fields.
                if (report[3] < 1 || report[3] > 3 || report[4] > 100) return false;
                update = new TelemetryUpdate {
                    Kind = TelemetryReportKind.Battery,
                    Command = report[2],
                    BatteryPercent = report[4],
                    BatteryState = (BatteryState)report[3]
                };
                return true;
            }

            if (report[2] == 0x10)
            {
                // G9 firmware 0x31A0C emits the actual runtime stage at 0x40104F.
                // Native HUB 0x41CF74 / 0x41D3F4 accepts one-based stages 1..8.
                if (report[3] < 1 || report[3] > 8 || report[4] != 0) return false;
                update = new TelemetryUpdate {
                    Kind = TelemetryReportKind.DpiStage,
                    Command = report[2],
                    DpiStage = report[3]
                };
                return true;
            }
            return false;
        }
    }

    public sealed class TelemetrySnapshot
    {
        public DateTimeOffset SampledAt { get; internal set; }
        public int? BatteryPercent { get; internal set; }
        public bool? IsCharging { get; internal set; }
        public bool? IsFull { get; internal set; }
        public DateTimeOffset? BatterySampledAt { get; internal set; }
        public int? DpiStage { get; internal set; }
        public DateTimeOffset? DpiStageSampledAt { get; internal set; }
        // A stage is not a DPI value. Do not infer a factory setting or poll GET10
        // to populate this: that read reloads a flash profile in stock firmware.
        public int? Dpi { get { return null; } }
        public string SourceName { get { return "G9 PRO passive HID input"; } }
    }

    // One instance per open physical HID collection/session. Caller must Reset
    // on disconnect, profile changes, or device selection changes. No timers,
    // hooks, feature SETs/GETs, polling or input injection are created here.
    public sealed class TelemetryCache
    {
        readonly string connectionKey;
        readonly object gate = new object();
        TelemetryUpdate battery, dpiStage;
        DateTimeOffset batteryAt, dpiAt;

        public TelemetryCache(string connectionKey)
        {
            if (String.IsNullOrWhiteSpace(connectionKey)) throw new ArgumentException("A HID connection key is required.");
            this.connectionKey = connectionKey;
        }

        public bool AcceptInput(string currentConnectionKey, ushort vendorId, ushort productId,
            ushort usagePage, ushort usage, byte[] report, DateTimeOffset receivedAt)
        {
            if (!String.Equals(connectionKey, currentConnectionKey, StringComparison.OrdinalIgnoreCase) ||
                !TelemetryProtocol.IsSupportedInputCollection(vendorId, productId, usagePage, usage,
                    report == null ? 0 : report.Length)) return false;
            TelemetryUpdate update;
            if (!TelemetryProtocol.TryDecodeInput(report, out update)) return false;
            lock (gate)
            {
                if (update.Kind == TelemetryReportKind.Battery)
                {
                    if (battery != null && receivedAt < batteryAt) return false;
                    battery = update;
                    batteryAt = receivedAt;
                }
                else
                {
                    if (dpiStage != null && receivedAt < dpiAt) return false;
                    dpiStage = update;
                    dpiAt = receivedAt;
                }
            }
            return true;
        }

        public TelemetrySnapshot Snapshot(DateTimeOffset now, TimeSpan maximumAge)
        {
            return Snapshot(now, maximumAge, maximumAge);
        }

        public TelemetrySnapshot Snapshot(DateTimeOffset now, TimeSpan batteryMaximumAge, TimeSpan dpiStageMaximumAge)
        {
            if (batteryMaximumAge < TimeSpan.Zero) throw new ArgumentOutOfRangeException("batteryMaximumAge");
            if (dpiStageMaximumAge < TimeSpan.Zero) throw new ArgumentOutOfRangeException("dpiStageMaximumAge");
            var result = new TelemetrySnapshot { SampledAt = now };
            lock (gate)
            {
                if (battery != null && IsFresh(batteryAt, now, batteryMaximumAge))
                {
                    result.BatteryPercent = battery.BatteryPercent;
                    result.IsCharging = battery.BatteryState == Telemetry.BatteryState.Charging;
                    result.IsFull = battery.BatteryState == Telemetry.BatteryState.Full;
                    result.BatterySampledAt = batteryAt;
                }
                if (dpiStage != null && IsFresh(dpiAt, now, dpiStageMaximumAge))
                {
                    result.DpiStage = dpiStage.DpiStage;
                    result.DpiStageSampledAt = dpiAt;
                }
            }
            return result;
        }

        public void Reset()
        {
            lock (gate) { battery = null; dpiStage = null; }
        }

        static bool IsFresh(DateTimeOffset sample, DateTimeOffset now, TimeSpan maximumAge)
        {
            // Do not let a future timestamp or clock rollback renew an old value.
            return sample <= now && now - sample <= maximumAge;
        }
    }
}
