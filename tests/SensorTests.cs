using System;
using Furycube.G9.Sensors;

internal static class SensorTests {
    static int count;
    static void Check(bool value, string name) { if (!value) throw new Exception("FAIL " + name); count++; }
    static bool Near(double? value, double expected) { return value.HasValue && Math.Abs(value.Value - expected) < 0.0001; }
    static string Metrics(DateTimeOffset sampledAt, string temperature) {
        return "{\"sampledAtUnixms\":" + SensorMath.UnixMilliseconds(sampledAt) + ",\"cpu\":{\"temperature\":" + temperature +
            ",\"source\":\"/amdcpu/0/temperature/2\",\"name\":\"Core (Tctl/Tdie)\",\"provider\":\"LibreHardwareMonitor0.9.6\"}}";
    }
    static int Main() { try {
        Check(Near(SensorMath.CpuPercent(10, 20, 30, 50, 80, 70), 60), "kernel includes idle, 60 percent busy");
        Check(Near(SensorMath.CpuPercent(0, 0, 0, 100, 100, 0), 0), "fully idle CPU");
        Check(Near(SensorMath.CpuPercent(0, 0, 0, 0, 60, 40), 100), "fully busy CPU");
        Check(!SensorMath.CpuPercent(1, 2, 3, 1, 2, 3).HasValue, "zero elapsed interval unavailable");
        Check(!SensorMath.CpuPercent(10, 20, 30, 5, 25, 35).HasValue, "idle counter reset");
        Check(!SensorMath.CpuPercent(10, 20, 30, 15, 10, 35).HasValue, "kernel counter reset");
        Check(!SensorMath.CpuPercent(10, 20, 30, 15, 25, 10).HasValue, "user counter reset");
        Check(!SensorMath.CpuPercent(0, 0, 0, 101, 50, 50).HasValue, "impossible idle delta rejected");
        Check(Near(SensorMath.RamPercent(64, 16), 75), "physical RAM ratio");
        Check(Near(SensorMath.RamPercent(64, 64), 0), "all RAM available");
        Check(!SensorMath.RamPercent(0, 0).HasValue && !SensorMath.RamPercent(64, 65).HasValue, "invalid RAM counters");
        Check(!SensorMath.Percent(-1).HasValue && !SensorMath.Percent(101).HasValue && !SensorMath.Percent(Double.NaN).HasValue, "percent bounds");
        Check(!SensorMath.Temperature(Double.PositiveInfinity).HasValue && !SensorMath.Temperature(151).HasValue, "temperature bounds");
        var now = new DateTimeOffset(2026, 11, 9, 0, 0, 0, TimeSpan.Zero);
        Check(SensorMath.Fresh(SensorMath.UnixMilliseconds(now.AddSeconds(-20)), now), "20-second freshness boundary");
        Check(!SensorMath.Fresh(SensorMath.UnixMilliseconds(now.AddMilliseconds(-20001)), now), "stale CPU sample");
        Check(!SensorMath.Fresh(SensorMath.UnixMilliseconds(now.AddSeconds(6)), now), "implausible future timestamp");
        var cpu = SensorMath.ParseCpuMetrics(Metrics(now.AddSeconds(-1), "58"), now);
        Check(cpu != null && cpu.Temperature == 58 && cpu.Name == "Core (Tctl/Tdie)" && cpu.SourceId == "/amdcpu/0/temperature/2", "CPU source label and identity preserved");
        Check(cpu.Provider == "LibreHardwareMonitor0.9.6", "provider preserved");
        var actualSchema=SensorMath.ParseCpuMetrics(Metrics(now.AddSeconds(-1),"58").Replace("sampledAtUnixms","sampledAt"),now);
        Check(actualSchema!=null&&actualSchema.Temperature==58,"existing bridge sampledAt schema");
        string cached="{\"sampledAt\":"+SensorMath.UnixMilliseconds(now)+",\"cpu\":{\"temperature\":58,\"sampledAt\":"+SensorMath.UnixMilliseconds(now.AddSeconds(-21))+"}}";
        Check(SensorMath.ParseCpuMetrics(cached,now)==null,"fresh response cannot renew stale nested CPU reading");
        Check(SensorMath.ParseCpuMetrics(Metrics(now.AddSeconds(-21), "58"), now) == null, "stale metric unavailable");
        Check(SensorMath.ParseCpuMetrics(Metrics(now, "999"), now) == null, "out-of-range metric unavailable");
        Check(SensorMath.ParseCpuMetrics("{\"cpu\":{\"temperature\":58}}", now) == null, "missing timestamp unavailable");
        Check(SensorMath.ParseCpuMetrics("{\"gpu\":{\"temperature\":58}}", now) == null, "GPU is not a CPU temperature substitute");
        Check(SensorMath.ParseCpuMetrics("not json", now) == null, "malformed metrics unavailable");
        Check(new SensorSnapshot().RamTemperatureCelsius == null, "no invented RAM temperature");
        Console.WriteLine("PASS " + count + " CPU/RAM delta, reset, staleness and source fixtures; no hardware or network APIs called."); return 0;
    } catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; } }
}
