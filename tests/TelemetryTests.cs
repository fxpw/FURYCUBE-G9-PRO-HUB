using System;
using Furycube.G9.Telemetry;

internal static class TelemetryTests
{
    static int count;
    static void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL " + name); count++; }
    static byte[] Battery(byte status, byte percent) { return new byte[] { 3, 0xC9, 0x40, status, percent }; }
    static byte[] Stage(byte stage) { return new byte[] { 3, 0xC9, 0x10, stage, 0 }; }
    static bool Accept(TelemetryCache cache, byte[] report, DateTimeOffset at)
    { return cache.AcceptInput("wired-session", 0x1D57, 0x2148, 0xA, 0, report, at); }

    static int Main()
    {
        try
        {
            TelemetryUpdate update;
            Check(TelemetryProtocol.TryDecodeInput(Battery(1, 86), out update) &&
                update.Kind == TelemetryReportKind.Battery && update.BatteryPercent == 86 &&
                update.BatteryState == BatteryState.Discharging, "captured receiver 03c9400156");
            Check(TelemetryProtocol.TryDecodeInput(Battery(3, 100), out update) &&
                update.BatteryPercent == 100 && update.BatteryState == BatteryState.Charging,
                "captured wired 03c9400364");
            var legacy = Battery(1, 45); legacy[2] = 0x41;
            Check(TelemetryProtocol.TryDecodeInput(legacy, out update) && update.BatteryPercent == 45,
                "old web HUB command41 keeps same payload");
            Check(!TelemetryProtocol.TryDecodeInput(null, out update), "null report");
            Check(!TelemetryProtocol.TryDecodeInput(new byte[4], out update), "truncated report");
            Check(!TelemetryProtocol.TryDecodeInput(new byte[] { 3, 0xC9, 0x40, 1, 86, 0 }, out update), "overlong report");
            var invalid = Battery(1, 86); invalid[0] = 4;
            Check(!TelemetryProtocol.TryDecodeInput(invalid, out update), "wrong report ID");
            invalid = Battery(1, 86); invalid[1] = 0xC8;
            Check(!TelemetryProtocol.TryDecodeInput(invalid, out update), "wrong model byte");
            invalid = Battery(1, 86); invalid[2] = 0x50;
            Check(!TelemetryProtocol.TryDecodeInput(invalid, out update), "status ACK is not battery");
            Check(!TelemetryProtocol.TryDecodeInput(Battery(0, 10), out update) &&
                !TelemetryProtocol.TryDecodeInput(Battery(4, 10), out update), "unknown battery states");
            Check(!TelemetryProtocol.TryDecodeInput(Battery(1, 101), out update), "percent above100 rejected");
            Check(TelemetryProtocol.TryDecodeInput(Battery(1, 0), out update) && update.BatteryPercent == 0, "real empty battery is zero");
            for (byte stage = 1; stage <= 8; stage++)
                Check(TelemetryProtocol.TryDecodeInput(Stage(stage), out update) && update.DpiStage == stage &&
                    update.BatteryPercent == null, "runtime one-based stage " + stage);
            Check(!TelemetryProtocol.TryDecodeInput(Stage(0), out update) &&
                !TelemetryProtocol.TryDecodeInput(Stage(9), out update), "stage boundaries");
            invalid = Stage(3); invalid[4] = 1;
            Check(!TelemetryProtocol.TryDecodeInput(invalid, out update), "stage tail must be zero");
            Check(TelemetryProtocol.IsSupportedInputCollection(0x1D57, 0x2148, 0xA, 0, 5) &&
                TelemetryProtocol.IsSupportedInputCollection(0x1D57, 0xFA60, 0xA, 0, 5), "normal wired and receiver collection");
            Check(!TelemetryProtocol.IsSupportedInputCollection(0xA745, 0x0033, 0xA, 0, 5), "bootloader rejected");
            Check(!TelemetryProtocol.IsSupportedInputCollection(0x1D57, 0x2142, 0xA, 0, 5), "other vendor model rejected");
            Check(!TelemetryProtocol.IsSupportedInputCollection(0x1D57, 0x2148, 0xB, 0, 5), "feature collection rejected");
            Check(!TelemetryProtocol.IsSupportedInputCollection(0x1D57, 0x2148, 1, 6, 5), "keyboard collection rejected");
            Check(!TelemetryProtocol.IsSupportedInputCollection(0x1D57, 0x2148, 0xA, 0, 4) &&
                !TelemetryProtocol.IsSupportedInputCollection(0x1D57, 0x2148, 0xA, 0, 6), "caps input length must equal fixed five-byte native buffer");
            var now = new DateTimeOffset(2026, 11, 9, 0, 0, 0, TimeSpan.Zero);
            var cache = new TelemetryCache("wired-session");
            var sample = cache.Snapshot(now, TimeSpan.FromSeconds(20));
            Check(sample.BatteryPercent == null && sample.IsCharging == null && sample.DpiStage == null &&
                sample.Dpi == null, "unobserved values stay unavailable");
            Check(!cache.AcceptInput("other-session", 0x1D57, 0x2148, 0xA, 0, Battery(1, 86), now), "different device session rejected");
            Check(Accept(cache, Battery(1, 86), now) && Accept(cache, Stage(3), now), "independent passive updates");
            sample = cache.Snapshot(now.AddSeconds(20), TimeSpan.FromSeconds(20));
            Check(sample.BatteryPercent == 86 && sample.IsCharging == false && sample.IsFull == false &&
                sample.DpiStage == 3 && sample.Dpi == null, "inclusive freshness boundary, stage never invents DPI");
            Check(!Accept(cache, Battery(1, 50), now.AddSeconds(-1)), "out-of-order sample does not replace current value");
            Check(!Accept(cache, new byte[] { 3, 0xC9, 0x40, 1, 200 }, now.AddSeconds(1)), "invalid packet does not renew cache");
            sample = cache.Snapshot(now.AddMilliseconds(20001), TimeSpan.FromSeconds(20));
            Check(sample.BatteryPercent == null && sample.IsCharging == null && sample.DpiStage == null, "stale samples expire independently");
            sample = cache.Snapshot(now.AddSeconds(91), TimeSpan.FromSeconds(90), TimeSpan.FromMinutes(5));
            Check(sample.BatteryPercent == null && sample.DpiStage == 3, "reader battery90s and stage5min independent limits");
            sample = cache.Snapshot(now.AddMinutes(5), TimeSpan.FromSeconds(90), TimeSpan.FromMinutes(5));
            Check(sample.DpiStage == 3, "stage5min inclusive limit");
            Check(cache.Snapshot(now.AddMinutes(5).AddMilliseconds(1), TimeSpan.FromSeconds(90), TimeSpan.FromMinutes(5)).DpiStage == null,
                "stage expiry beyond5min");
            Check(Accept(cache, Battery(3, 68), now.AddSeconds(21)), "new charging update");
            sample = cache.Snapshot(now.AddSeconds(21), TimeSpan.FromSeconds(20));
            Check(sample.BatteryPercent == 68 && sample.IsCharging == true && sample.DpiStage == null, "battery update cannot renew stage");
            Check(Accept(cache, Battery(2, 95), now.AddSeconds(22)), "full state with explicit percent accepted");
            sample = cache.Snapshot(now.AddSeconds(22), TimeSpan.FromSeconds(20));
            Check(sample.IsFull == true && sample.BatteryPercent == 95, "full label cannot replace actual reported percent with100");
            sample = cache.Snapshot(now.AddSeconds(-1), TimeSpan.FromSeconds(20));
            Check(sample.BatteryPercent == null, "clock rollback cannot make future sample current");
            cache.Reset();
            sample = cache.Snapshot(now.AddSeconds(22), TimeSpan.FromDays(1));
            Check(sample.BatteryPercent == null && sample.DpiStage == null, "disconnect reset clears all observations");
            Console.WriteLine("PASS " + count + " telemetry identity, captured battery, stage, bounds, expiry and reset fixtures; no HID APIs called.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
    }
}
