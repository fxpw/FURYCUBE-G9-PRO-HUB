using System;
using System.IO;
using System.Reflection;

internal static class FirmwareOl3Tests {
    static int checks;
    static void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL " + name); checks++; }
    static void Reject(Action operation, string name) {
        try { operation(); } catch (InvalidDataException) { checks++; return; }
        throw new Exception("FAIL accepted " + name);
    }
    static int Main(string[] args) {
        try {
            string root = args.Length == 1 ? Path.GetFullPath(args[0]) : Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".."));
            using (Stream embedded = Assembly.GetExecutingAssembly().GetManifestResourceStream("Furycube.G9.Ol3Payload"))
                Check(embedded != null, "own OL3 payload is embedded in assembly");
            byte[] payload = G9FirmwareOl3Patch.LoadPayload(root);
            Check(payload.Length == 636 && G9FirmwarePatch.Hash(payload) == G9FirmwareOl3Patch.PayloadHash, "pinned own OL3 ARM payload");
            var oldPrefix = new byte[280]; Buffer.BlockCopy(payload,0,oldPrefix,0,oldPrefix.Length);
            Check(G9FirmwarePatch.Hash(oldPrefix) == G9FirmwareOl3Patch.Ol2PrefixHash, "original OL2 prefix remains byte-identical");
            Check(G9FirmwareOl3Patch.OriginalBlocks == 3939 && G9FirmwareOl3Patch.NewBlocks == 20 &&
                G9FirmwareOl3Patch.CandidateSize == 16 + 3959 * 34 + 2, "exact framed geometry,20 new blocks and2 FF word-alignment bytes");
            Reject(delegate { G9FirmwareOl3Patch.BuildResource(null,payload); }, "missing source resource");
            Reject(delegate { G9FirmwareOl3Patch.BuildResource(new byte[134139],payload); }, "wrong original resource size");
            string fixture = Path.Combine(root,"evidence","firmware-readonly");
            string[] sources = Directory.Exists(fixture) ? Directory.GetFiles(fixture,"Update.exe",SearchOption.AllDirectories) : new string[0];
            if (sources.Length == 0) {
                Console.WriteLine("PASS " + checks + " own-payload checks; SKIP vendor-image integration: local fixture is not distributed"); return 0;
            }
            Check(sources.Length == 1,"one local original updater");
            byte[] before = File.ReadAllBytes(sources[0]);
            Check(G9FirmwarePatch.Hash(before) == G9FirmwareOl3Patch.OriginalUpdaterHash,"pinned official updater");
            byte[] original = G9FirmwarePatch.ReadBinResource(sources[0],G9FirmwareOl3Patch.OriginalSize);
            byte[] candidate = G9FirmwareOl3Patch.BuildResource(original,payload);
            Check(candidate.Length == 134624,"exact candidate size");
            Check(G9FirmwarePatch.Hash(candidate) == G9FirmwareOl3Patch.CandidateResourceHash,"exact independent Python/ARM candidate hash");
            G9FirmwarePatch.ValidateImageHeader(original); checks++;
            G9FirmwarePatch.ValidateImageHeader(candidate); checks++;
            Check(BitConverter.ToUInt16(candidate,6)==33656,"exact corrected length in words");
            Check(BitConverter.ToUInt32(candidate,0)==0x30EB4770u,"exact independently computed body CRC32");
            Check(candidate[candidate.Length-2]==255 && candidate[candidate.Length-1]==255,"FF word alignment");
            for (int index=4;index<16;index++) if(index!=6 && index!=7) Check(candidate[index] == original[index],"preserved header identity/status byte"+index);
            byte[] staleHeader=(byte[])candidate.Clone(); Buffer.BlockCopy(original,0,staleHeader,0,16);
            Reject(delegate { G9FirmwarePatch.ValidateImageHeader(staleHeader); },"oldOL3 staleheader regression");
            byte[] corrupt = (byte[])payload.Clone(); corrupt[0] ^= 1;
            Reject(delegate { G9FirmwareOl3Patch.BuildResource(original,corrupt); },"changed own payload");
            Reject(delegate { G9FirmwareOl3Patch.BuildResource(original,null); },"missing own payload");
            Reject(delegate { G9FirmwareOl3Patch.BuildResource(original,new byte[635]); },"wrong payload length");
            byte[] badSource = (byte[])original.Clone(); badSource[100] ^= 1;
            Reject(delegate { G9FirmwareOl3Patch.BuildResource(badSource,payload); },"changed original resource");
            byte[] tail = (byte[])original.Clone(); tail[tail.Length-1] = 0;
            Reject(delegate { G9FirmwareOl3Patch.BuildResource(tail,payload); },"changed original FF tail");
            Check(((candidate.Length+4095)&~4095) == 0x21000 && ((original.Length+4095)&~4095) == 0x21000,"transfer round-size unchanged");
            string output = G9FirmwareOl3Patch.Prepare(root);
            string packed = Path.Combine(output,"FURYCUBE-G9-PRO-OL3-candidate.exe.disabled");
            Check(File.Exists(packed) && packed.EndsWith(".exe.disabled",StringComparison.Ordinal),"disabled updater saved without execution");
            Check(G9FirmwarePatch.Hash(G9FirmwarePatch.ReadBinResource(packed,134624)) == G9FirmwareOl3Patch.CandidateResourceHash,"repacked resource readback");
            Check(G9FirmwarePatch.VerifyUnchangedSections(before,File.ReadAllBytes(packed)).Count > 0,"non-resource PE sections preserved");
            byte[] packedBytes = File.ReadAllBytes(packed);
            G9FirmwareOl3Patch.VerifyResourceOnlyChange(before,packedBytes); checks++;
            Check(packedBytes.Length == before.Length,"original PE file size preserved");
            byte[] tamperedPe = (byte[])packedBytes.Clone(); tamperedPe[0x400] ^= 1;
            Reject(delegate { G9FirmwareOl3Patch.VerifyResourceOnlyChange(before,tamperedPe); },"changed non-resource byte");
            Check(G9FirmwarePatch.Hash(File.ReadAllBytes(sources[0])) == G9FirmwareOl3Patch.OriginalUpdaterHash,"original updater remains unchanged");
            string manifest = File.ReadAllText(Path.Combine(output,"ol3-patch-report.json"));
            Check(manifest.Contains("\"installation\":\"NOT INSTALLED\"") && manifest.Contains("\"physicalValidation\":\"NOT TESTED\""),"manifest cannot claim installation or physical success");
            Check(manifest.Contains("\"installationBlockedUntilRecoveryProven\":true") && manifest.Contains("\"originalA0TransportWakeRemains\":true"),"recovery gate and wake limit documented");
            Check(manifest.Contains("\"allPeHeadersUnchanged\":true") && manifest.Contains("\"allBytesOutsideResourceUnchanged\":true"),"strict resource-only PE checks documented");
            Check(File.Exists(Path.Combine(output,"ПРОВЕРКА-OL3.txt")),"Russian offline/recovery report saved");
            Console.WriteLine("PASS " + checks + " offline OL3 builder checks; no USB/updater execution");
            Console.WriteLine(output); return 0;
        } catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
