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
            // Independent CRC32 literal for ASCII 12345678, reflected CRC32
            // with init FFFFFFFF and no final XOR. No vendor fixture needed.
            byte[] vector = new byte[24];
            for (int i=0;i<16;i++) vector[i]=(byte)(0x80+i);
            Buffer.BlockCopy(System.Text.Encoding.ASCII.GetBytes("12345678"),0,vector,16,8);
            byte[] initial = (byte[])vector.Clone();
            Check(G9FirmwarePatch.BodyCrc32(vector)==0x651F2550u,"independent whole-body CRC32 vector");
            Reject(delegate { G9FirmwarePatch.ValidateImageHeader(vector); },"unsealed header");
            G9FirmwarePatch.SealImageHeader(vector);
            Check(vector[0]==0x50 && vector[1]==0x25 && vector[2]==0x1F && vector[3]==0x65,"CRC32 little endian");
            Check(vector[6]==6 && vector[7]==0,"length includes16-byte header in4-byte words");
            for(int i=4;i<16;i++) if(i!=6 && i!=7) Check(vector[i]==initial[i],"identity/status preserved"+i);
            G9FirmwarePatch.ValidateImageHeader(vector); checks++;
            byte[] stale=(byte[])vector.Clone(); stale[16]^=1;
            Reject(delegate { G9FirmwarePatch.ValidateImageHeader(stale); },"body mutation with stale outerCRC");
            stale=(byte[])vector.Clone(); stale[6]++;
            Reject(delegate { G9FirmwarePatch.ValidateImageHeader(stale); },"stale length despite validbodyCRC");
            Reject(delegate { G9FirmwarePatch.SealImageHeader(new byte[25]); },"unaligned image");
            Reject(delegate { G9FirmwarePatch.ValidateImageHeader(null); },"missing image");
            Reject(delegate { G9FirmwarePatch.SealImageHeader(new byte[12]); },"truncated header");
            Reject(delegate { G9FirmwarePatch.SealImageHeader(new byte[0xFFFF*4]); },"reserved FFFF wordlength");
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
            byte[] noWait=G9FirmwarePatch.BuildNoWaitResource(original);
            Check(noWait.Length==original.Length && G9FirmwarePatch.Hash(noWait)==G9FirmwarePatch.PatchedResourceHash,"corrected NoWait resource hash and unchangedlength");
            G9FirmwarePatch.ValidateImageHeader(noWait); checks++;
            Check(BitConverter.ToUInt32(noWait,0)==0x1F831F4Eu,"independent NoWait bodyCRC");
            byte[] oldNoWait=(byte[])noWait.Clone(); Buffer.BlockCopy(original,0,oldNoWait,0,16);
            Reject(delegate { G9FirmwarePatch.ValidateImageHeader(oldNoWait); },"old NoWait staleheader regression");
            byte[] candidate = G9FirmwareOledPatch.BuildResource(original, payload);
            G9FirmwarePatch.ValidateImageHeader(candidate); checks++;
            byte[] oldHeader=(byte[])candidate.Clone(); Buffer.BlockCopy(original,0,oldHeader,0,16);
            Reject(delegate { G9FirmwarePatch.ValidateImageHeader(oldHeader); },"old OL2 staleheader regression");
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
