using System;
using System.Reflection;
using System.Runtime.InteropServices;

internal static class TelemetryReaderTests
{
    static int count;
    static void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL " + name); count++; }
    static string Path(string identity, string instance) { return @"\\?\hid#" + identity + "#" + instance + "#{guid}"; }
    static bool Reject(string[] paths)
    { try { G9TelemetryReader.SelectPath(paths); return false; } catch (InvalidOperationException) { return true; } }
    static int Main()
    {
        try
        {
            string wire = Path("vid_1d57&pid_2148&mi_02&col03", "wired");
            string radio = Path("vid_1d57&pid_fa60&mi_02&col03", "radio");
            Check(G9TelemetryReader.SelectPath(new string[0]) == null, "no candidate remains unavailable");
            Check(G9TelemetryReader.SelectPath(new[] { radio, wire }) == wire, "wired preferred over receiver");
            Check(G9TelemetryReader.SelectPath(new[] { radio }) == radio, "single receiver fallback");
            Check(G9TelemetryReader.SelectPath(new[] { wire, wire.ToUpperInvariant() }) == wire, "duplicate path de-duplicated");
            Check(Reject(new[] { wire, Path("vid_1d57&pid_2148&mi_02&col03", "other") }), "multiple wired devices ambiguous");
            Check(Reject(new[] { radio, Path("vid_1d57&pid_fa60&mi_02&col03", "other") }), "multiple receivers ambiguous");
            Check(G9TelemetryReader.SelectPath(new[] { radio, Path("vid_1d57&pid_fa60&mi_02&col03", "other"), wire }) == wire,
                "receiver ambiguity cannot override one exact wired candidate");
            Check(G9TelemetryReader.SelectPath(new[] {
                Path("vid_1d57&pid_2148&mi_00", "keyboard"),
                Path("vid_1d57&pid_2148&mi_02&col02", "consumer"),
                Path("vid_1d57&pid_2148&mi_02&col04", "feature"),
                Path("vid_1d57&pid_2148&mi_02&col030", "wrongcollection"),
                Path("vid_a745&pid_0033&mi_02&col03", "loader"),
                Path("vid_1d57&pid_2142&mi_02&col03", "othermouse") }) == null,
                "filter excludes keyboard consumer feature loader and other model paths before open");
            var reader = new G9TelemetryReader();
            Check(reader.Snapshot.BatteryPercent == null && reader.CurrentSnapshot.Dpi == null && reader.LastError == null,
                "construction and snapshot are lazy unavailable state");
            reader.Dispose(); reader.Dispose();
            Check(reader.Snapshot.BatteryPercent == null && reader.Snapshot.DpiStage == null, "Dispose idempotent without native calls");
            bool disposed = false;
            try { reader.Start(); } catch (ObjectDisposedException) { disposed = true; }
            Check(disposed, "Start after Dispose rejected before queuing any native work");
            Type native = typeof(G9TelemetryReader).GetNestedType("Native", BindingFlags.NonPublic);
            Check(Marshal.SizeOf(native.GetNestedType("Caps", BindingFlags.NonPublic)) == 64, "native HIDP_CAPS layout64bytes");
            Check(Marshal.SizeOf(native.GetNestedType("Attributes", BindingFlags.NonPublic)) == 12, "native HIDD_ATTRIBUTES layout12bytes");
            Check(Marshal.SizeOf(native.GetNestedType("Overlapped", BindingFlags.NonPublic)) == (IntPtr.Size == 8 ? 32 : 20),
                "native OVERLAPPED pointer-size layout");
            Console.WriteLine("PASS " + count + " passive reader selection/lifecycle fixtures; Start was never run on a live reader.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
    }
}
