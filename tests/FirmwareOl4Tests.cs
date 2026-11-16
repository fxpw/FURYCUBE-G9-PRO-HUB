using System;
using System.IO;
using System.Reflection;

internal static class FirmwareOl4Tests {
    static int checks;
    static void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL " + name); checks++; }
    static void Reject(Action operation, string name) {
        try { operation(); } catch (InvalidDataException) { checks++; return; }
        throw new Exception("FAIL accepted " + name);
    }
    static int Main(string[] args) {
        try {
            string root = args.Length == 1 ? Path.GetFullPath(args[0]) : Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".."));
            using (Stream embedded = Assembly.GetExecutingAssembly().GetManifestResourceStream("Furycube.G9.Ol4Payload"))
                Check(embedded != null, "own OL4 payload is embedded in assembly");
            byte[] payload = G9FirmwareOl4Patch.LoadPayload(root);
            Check(payload.Length == 708 && G9FirmwarePatch.Hash(payload) == G9FirmwareOl4Patch.PayloadHash, "pinned own OL4 ARM payload");
            byte[] priorPrefix=new byte[692];Buffer.BlockCopy(payload,0,priorPrefix,0,priorPrefix.Length);
            Check(G9FirmwarePatch.Hash(priorPrefix)=="c8967e1385c9afa71e749662a44f17f961a6388815a039eeba0db949eea261d8","EP0 guard keeps existing own692-byte payload prefix");
            Check(G9FirmwareOl4Patch.OriginalBlocks == 3939 && G9FirmwareOl4Patch.NewBlocks == 23 &&
                G9FirmwareOl4Patch.CandidateSize == 16 + 3962 * 34, "exact framed geometry,23 new blocks and no word-alignment tail");
            Reject(delegate { G9FirmwareOl4Patch.BuildResource(null,payload); }, "missing source resource");
            Reject(delegate { G9FirmwareOl4Patch.BuildResource(new byte[134139],payload); }, "wrong original resource size");
            string fixture = Path.Combine(root,"evidence","firmware-readonly");
            string[] sources = Directory.Exists(fixture) ? Directory.GetFiles(fixture,"Update.exe",SearchOption.AllDirectories) : new string[0];
            if (sources.Length == 0) {
                Console.WriteLine("PASS " + checks + " own-payload checks; SKIP vendor-image integration: local fixture is not distributed"); return 0;
            }
            Check(sources.Length == 1,"one local original updater");
            byte[] before = File.ReadAllBytes(sources[0]);
            Check(G9FirmwarePatch.Hash(before) == G9FirmwareOl4Patch.OriginalUpdaterHash,"pinned official updater");
            byte[] original = G9FirmwarePatch.ReadBinResource(sources[0],G9FirmwareOl4Patch.OriginalSize);
            byte[] candidate = G9FirmwareOl4Patch.BuildResource(original,payload);
            Check(candidate.Length == 134724,"exact candidate size");
            Check(G9FirmwarePatch.Hash(candidate) == G9FirmwareOl4Patch.CandidateResourceHash,"exact independent Python/ARM candidate hash");
            G9FirmwarePatch.ValidateImageHeader(original); checks++;
            G9FirmwarePatch.ValidateImageHeader(candidate); checks++;
            Check(BitConverter.ToUInt16(candidate,6)==33681,"exact corrected length in words");
            Check(BitConverter.ToUInt32(candidate,0)==0xC6207ED8u,"exact independently computed body CRC32");
            for (int index=4;index<16;index++) if(index!=6 && index!=7) Check(candidate[index] == original[index],"preserved header identity/status byte"+index);
            byte[] staleHeader=(byte[])candidate.Clone(); Buffer.BlockCopy(original,0,staleHeader,0,16);
            Reject(delegate { G9FirmwarePatch.ValidateImageHeader(staleHeader); },"oldOL4 staleheader regression");
            byte[] corrupt = (byte[])payload.Clone(); corrupt[0] ^= 1;
            Reject(delegate { G9FirmwareOl4Patch.BuildResource(original,corrupt); },"changed own payload");
            Reject(delegate { G9FirmwareOl4Patch.BuildResource(original,null); },"missing own payload");
            Reject(delegate { G9FirmwareOl4Patch.BuildResource(original,new byte[707]); },"wrong payload length");
            byte[] badSource = (byte[])original.Clone(); badSource[100] ^= 1;
            Reject(delegate { G9FirmwareOl4Patch.BuildResource(badSource,payload); },"changed original resource");
            byte[] tail = (byte[])original.Clone(); tail[tail.Length-1] = 0;
            Reject(delegate { G9FirmwareOl4Patch.BuildResource(tail,payload); },"changed original FF tail");
            Check(((candidate.Length+4095)&~4095) == 0x21000 && ((original.Length+4095)&~4095) == 0x21000,"transfer round-size unchanged");
            string output = G9FirmwareOl4Patch.Prepare(root);
            string packed = Path.Combine(output,"FURYCUBE-G9-PRO-OL4-candidate.exe.disabled");
            Check(File.Exists(packed) && packed.EndsWith(".exe.disabled",StringComparison.Ordinal),"disabled updater saved without execution");
            Check(G9FirmwarePatch.Hash(G9FirmwarePatch.ReadBinResource(packed,134724)) == G9FirmwareOl4Patch.CandidateResourceHash,"repacked resource readback");
            Check(G9FirmwarePatch.VerifyUnchangedSections(before,File.ReadAllBytes(packed)).Count > 0,"non-resource PE sections preserved");
            byte[] packedBytes = File.ReadAllBytes(packed);
            G9FirmwareOl4Patch.VerifyResourceOnlyChange(before,packedBytes); checks++;
            Check(packedBytes.Length == before.Length,"original PE file size preserved");
            byte[] tamperedPe = (byte[])packedBytes.Clone(); tamperedPe[0x400] ^= 1;
            Reject(delegate { G9FirmwareOl4Patch.VerifyResourceOnlyChange(before,tamperedPe); },"changed non-resource byte");
            Check(G9FirmwarePatch.Hash(File.ReadAllBytes(sources[0])) == G9FirmwareOl4Patch.OriginalUpdaterHash,"original updater remains unchanged");
            string manifest = File.ReadAllText(Path.Combine(output,"ol4-patch-report.json"));
            Check(manifest.Contains("\"updaterExecuted\":false") && manifest.Contains("NOT PERFORMED BY THIS BUILDER"),"offline builder cannot claim physical success");
            Check(manifest.Contains("\"rollback\":\"NOT VERIFIED\"") && manifest.Contains("stockUsbCallbacksByteIdentical"),"recovery limit and explicit USB callback scope documented");
            Check(manifest.Contains("\"allPeHeadersUnchanged\":true") && manifest.Contains("\"allBytesOutsideResourceUnchanged\":true"),"strict resource-only PE checks documented");
            Check(manifest.Contains("\"stockUsbCallbacksByteIdentical\":false") && manifest.Contains("\"report22DoesNotForceMouseReady\":true") && manifest.Contains("0x37896"),"one precise mouse-endpoint readiness exception documented");
            Check(G9FirmwarePatch.Hash(packedBytes)==G9FirmwareOl4Patch.CandidateUpdaterHash,"exact reviewed disabledEXE hash");
            Check(File.Exists(Path.Combine(output,"OL4-EXPERIMENT.txt")),"English offline experimental report saved");
            Console.WriteLine("PASS " + checks + " offline OL4 builder checks; no USB/updater execution");
            Console.WriteLine(output); return 0;
        } catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
