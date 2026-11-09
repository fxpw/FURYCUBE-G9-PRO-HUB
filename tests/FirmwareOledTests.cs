using System;
using System.IO;
using System.Reflection;

internal static class FirmwareOledTests {
    static int checks;
    static void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL " + name); checks++; }
    static void Reject(Action operation, string name) {
        try { operation(); } catch (InvalidDataException) { checks++; return; }
        throw new Exception("FAIL accepted " + name);
    }
    static int Main(string[] args) {
        try {
            string root = args.Length == 1 ? Path.GetFullPath(args[0]) : Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".."));
            using (Stream embedded = Assembly.GetExecutingAssembly().GetManifestResourceStream("Furycube.G9.OledPayload"))
                Check(embedded != null, "payload embedded in assembly");
            byte[] payload = G9FirmwareOledPatch.LoadPayload(root);
            Check(payload.Length == 280 && G9FirmwarePatch.Hash(payload) == G9FirmwareOledPatch.PayloadHash, "pinned own ARM payload");
            Reject(delegate { G9FirmwareOledPatch.BuildResource(new byte[134139], payload); }, "wrong original size");
            string fixture = Path.Combine(root, "evidence", "firmware-readonly");
            if (!Directory.Exists(fixture)) {
                Console.WriteLine("PASS " + checks + " own-payload checks; SKIP vendor-image integration: local fixture is not distributed");
                return 0;
            }
            string[] sources = Directory.GetFiles(fixture, "Update.exe", SearchOption.AllDirectories);
            Check(sources.Length == 1, "one original updater");
            byte[] before = File.ReadAllBytes(sources[0]);
            byte[] original = G9FirmwarePatch.ReadBinResource(sources[0]);
            byte[] candidate = G9FirmwareOledPatch.BuildResource(original, payload);
            Check(candidate.Length == 134248, "exact candidate size");
            Check(G9FirmwarePatch.Hash(candidate) == G9FirmwareOledPatch.CandidateResourceHash, "exact Python candidate SHA256");
            byte[] corrupt = (byte[])payload.Clone(); corrupt[0] ^= 1;
            Reject(delegate { G9FirmwareOledPatch.BuildResource(original, corrupt); }, "modified own payload");
            byte[] badSource = (byte[])original.Clone(); badSource[100] ^= 1;
            Reject(delegate { G9FirmwareOledPatch.BuildResource(badSource, payload); }, "modified original resource");
            string output = G9FirmwareOledPatch.Prepare(root);
            string packed = Path.Combine(output, "FURYCUBE-G9-PRO-OL2-candidate.exe.disabled");
            Check(File.Exists(packed) && packed.EndsWith(".exe.disabled", StringComparison.Ordinal), "disabled updater saved");
            Check(G9FirmwarePatch.Hash(G9FirmwarePatch.ReadBinResource(packed, 134248)) == G9FirmwareOledPatch.CandidateResourceHash, "repacked resource readback");
            Check(G9FirmwarePatch.VerifyUnchangedSections(before, File.ReadAllBytes(packed)).Count > 0, "non-resource PE sections preserved");
            Check(G9FirmwarePatch.Hash(File.ReadAllBytes(sources[0])) == G9FirmwareOledPatch.OriginalUpdaterHash, "original updater preserved");
            Check(File.Exists(Path.Combine(output, "oled-patch-report.json")) && File.Exists(Path.Combine(output, "ПРОВЕРКА-OL2.txt")), "manifest and Russian report saved");
            Console.WriteLine("PASS " + checks + " offline OLED builder checks; no USB/updater execution");
            Console.WriteLine(output);
            return 0;
        } catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
