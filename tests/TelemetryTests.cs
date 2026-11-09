using System;
using Furycube.G9.Telemetry;

internal static class TelemetryTests
{
    static int count;
    static void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL " + name); count++; }
    static byte[] Battery(byte status, byte percent) { return new byte[] { 3, 0xC9, 0x40, status, percent }; }
    static byte[] Stage(byte stage) { return new byte[] { 3, 0xC9, 0x10, stage, 0 }; }
    static byte[] Profile(byte zeroBased) { return new byte[] { 3, 0xC9, 0x80, zeroBased, 0 }; }
    static bool Accept(TelemetryCache cache, byte[] report, DateTimeOffset at)
    { return cache.AcceptInput("wired-session", 0x1D57, 0x2148, 0xA, 0, report, at); }

    static void CheckProfiles(DateTimeOffset now)
    {
        TelemetryUpdate update;
        for (byte index = 0; index < 5; index++)
            Check(TelemetryProtocol.TryDecodeInput(Profile(index), out update) &&
                update.Kind == TelemetryReportKind.Profile && update.Command == 0x80 &&
                update.ReportedProfileId == index + 1 && update.DpiStage == null && update.BatteryPercent == null,
                "profile notification zero-based " + index + " becomes last-reported ID " + (index + 1));
        Check(!TelemetryProtocol.TryDecodeInput(Profile(5), out update) &&
            !TelemetryProtocol.TryDecodeInput(Profile(255), out update), "profile byte outside zero-based0..4 rejected");
        Check(!TelemetryProtocol.TryDecodeInput(new byte[] { 3, 0xC9, 0x80, 2, 1 }, out update), "profile tail must be zero");
        Check(!TelemetryProtocol.TryDecodeInput(new byte[] { 3, 0xC9, 0x80, 2 }, out update) &&
            !TelemetryProtocol.TryDecodeInput(new byte[] { 3, 0xC9, 0x80, 2, 0, 0 }, out update), "profile input is exactly five bytes");

        var cache = new TelemetryCache("wired-session");
        Check(Accept(cache, Battery(1, 86), now) && Accept(cache, Stage(3), now.AddSeconds(1)), "profile regression starts with independent battery and stage");
        Check(Accept(cache, Profile(1), now.AddSeconds(2)), "new profile notification accepted");
        var sample = cache.Snapshot(now.AddSeconds(2), TimeSpan.FromMinutes(5));
        Check(sample.DpiStage == null && sample.DpiStageSampledAt == null, "profile notification immediately clears stale stage and its published timestamp");
        Check(sample.BatteryPercent == 86 && sample.BatterySampledAt == now && sample.IsCharging == false,
            "profile invalidation preserves battery value, state and original freshness");
        Check(sample.ReportedProfileId == 2 && sample.ReportedProfileSampledAt == now.AddSeconds(2) && sample.Dpi == null,
            "snapshot exposes last-reported profile without inventing numeric DPI");
        Check(Accept(cache, Stage(7), now.AddSeconds(3)), "new stage after profile notification accepted");
        Check(cache.Snapshot(now.AddSeconds(3), TimeSpan.FromMinutes(5)).DpiStage == 7, "fresh later stage becomes available");
        Check(Accept(cache, Profile(1), now.AddSeconds(4)), "same profile notification is accepted again");
        Check(cache.Snapshot(now.AddSeconds(4), TimeSpan.FromMinutes(5)).DpiStage == null,
            "same-profile reload invalidates stage too");
        Check(!Accept(cache, Stage(8), now.AddMilliseconds(3500)), "delayed stage cannot resurrect value after invalidation");
        Check(!Accept(cache, Profile(0), now.AddMilliseconds(3500)), "out-of-order profile cannot replace last notification");
        Check(Accept(cache, Stage(5), now.AddSeconds(5)), "current stage after duplicate profile accepted");
        Check(!Accept(cache, Profile(3), now.AddMilliseconds(4500)), "delayed profile cannot invalidate a newer stage");
        Check(!Accept(cache, Stage(6), now.AddMilliseconds(4500)), "delayed stage cannot replace a newer stage");
        Check(!Accept(cache, Profile(5), now.AddSeconds(6)) &&
            !Accept(cache, new byte[] { 3, 0xC9, 0x80, 2, 1 }, now.AddSeconds(6)), "malformed profile packets do not invalidate or refresh observations");
        Check(!cache.AcceptInput("other-session", 0x1D57, 0x2148, 0xA, 0, Profile(0), now.AddSeconds(6)) &&
            !cache.AcceptInput("wired-session", 0x1D57, 0x2148, 0xB, 0, Profile(0), now.AddSeconds(6)),
            "wrong-session and feature-interface profiles cannot invalidate current telemetry");
        sample = cache.Snapshot(now.AddSeconds(6), TimeSpan.FromMinutes(5));
        Check(sample.DpiStage == 5 && sample.ReportedProfileId == 2 && sample.ReportedProfileSampledAt == now.AddSeconds(4),
            "rejected packets retain newer stage and original profile timestamp");
        Check(sample.Dpi == null, "profile plus a valid stage still cannot infer numeric DPI");

        Check(Accept(cache, Profile(1), now.AddSeconds(5)), "equal-time profile follows the accepted arrival order");
        Check(cache.Snapshot(now.AddSeconds(5), TimeSpan.FromMinutes(5)).DpiStage == null,
            "equal-time repeated profile still clears stage");
        Check(Accept(cache, Stage(2), now.AddSeconds(5)) &&
            cache.Snapshot(now.AddSeconds(5), TimeSpan.FromMinutes(5)).DpiStage == 2,
            "equal-time stage arriving afterward is usable");
        sample = cache.Snapshot(now.AddSeconds(6), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(1));
        Check(sample.ReportedProfileId == 2, "profile age uses inclusive freshness boundary");
        sample = cache.Snapshot(now.AddMilliseconds(6001), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(1));
        Check(sample.ReportedProfileId == null && sample.ReportedProfileSampledAt == null && sample.DpiStage == 2 && sample.BatteryPercent == 86,
            "last-reported profile expires independently from valid battery and stage");
        Check(cache.Snapshot(now.AddMilliseconds(4999), TimeSpan.FromMinutes(5)).ReportedProfileId == null,
            "clock rollback cannot expose a future profile sample");
        Check(Accept(cache, Battery(3, 70), now.AddMinutes(1)), "new battery remains independent of profile");
        Check(cache.Snapshot(now.AddMinutes(5).AddSeconds(5), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5)).ReportedProfileId == 2,
            "existing snapshot overload gives profile same maximum age as stage");
        sample = cache.Snapshot(now.AddMinutes(5).AddSeconds(5).AddMilliseconds(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
        Check(sample.ReportedProfileId == null && sample.DpiStage == null && sample.BatteryPercent == 70,
            "battery update cannot renew expired profile or stage");
        Check(Accept(cache, Profile(2), now.AddMinutes(6)) && !Accept(cache, Stage(4), now.AddMinutes(6).AddTicks(-1)),
            "expired display fields retain ordering watermark until reset");
        try { cache.Snapshot(now, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.FromTicks(-1)); throw new Exception("FAIL accepted negative profile age"); }
        catch (ArgumentOutOfRangeException) { count++; }
        cache.Reset(); sample = cache.Snapshot(now.AddMinutes(6), TimeSpan.FromDays(1));
        Check(sample.BatteryPercent == null && sample.DpiStage == null && sample.ReportedProfileId == null && sample.ReportedProfileSampledAt == null,
            "session reset removes battery, stage and reported-profile state");
        Check(Accept(cache, Stage(1), now) && Accept(cache, Profile(0), now), "reset also removes old ordering watermarks");
        var isolated = new TelemetryCache("receiver-session");
        Check(isolated.AcceptInput("receiver-session", 0x1D57, 0xFA60, 0xA, 0, Profile(4), now) &&
            isolated.Snapshot(now, TimeSpan.FromSeconds(1)).ReportedProfileId == 5 &&
            cache.Snapshot(now, TimeSpan.FromSeconds(1)).ReportedProfileId == 1,
            "receiver and wired sessions keep profile observations isolated");
    }

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
            CheckProfiles(now);
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
            Console.WriteLine("PASS " + count + " telemetry identity, battery, stage, profile invalidation, order, bounds, expiry and reset fixtures; no HID APIs called.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
    }
}
