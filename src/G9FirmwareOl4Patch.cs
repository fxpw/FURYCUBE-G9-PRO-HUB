using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;

// Offline artifact preparation only. This class has no HID, process-start or
// firmware-install API. The official updater is mapped as data by G9FirmwarePatch.
internal static class G9FirmwareOl4Patch {
    internal const string OriginalUpdaterHash = "558031de581d5ba2317abd367c793776def812f4ec7eff6cff86850658748be4";
    internal const string OriginalResourceHash = "f7c4db8ffae37d033cbde4d670d70f32bd9ea62b36d0e6d7ba9cba5ce7d9efcc";
    internal const string PayloadHash = "88f710307311e213c1c11420ed0a68b47aee2ab38ffd44b18034465367fc2f0b";
    internal const string CandidateResourceHash = "96ec97d871d884fe5fdeca1e8b37c19a0b70868e8065325fab05a9f7048b69a7";
    internal const string CandidateUpdaterHash = "7412ecb3432f8d237c6c26fc63d24d40237b3c921d88cfc494fc9a70c56b251c";
    internal const int OriginalSize = 134140, CandidateSize = 134724, PayloadSize = 708;
    internal const int OriginalBlocks = 3939, NewBlocks = 23;
    internal const int CandidateTailPadding = 0;
    const int RoundedTransferSize = 0x21000;
    const string EmbeddedPayload = "Furycube.G9.Ol4Payload";
    static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

    sealed class Patch {
        internal int Offset; internal byte[] Before, After; internal string Reason;
        internal Patch(int offset, string before, string after, string reason) {
            Offset = offset; Before = Hex(before); After = Hex(after); Reason = reason;
        }
    }
    static readonly Patch[] Patches = {
        new Patch(0x988C, "0d70", "c046", "Disable OLED hold gesture"),
        new Patch(0xB504, "11704078", "13f050fc", "Version-report OL4 marker hook"),
        new Patch(0xB544, "00f010ff", "13f094fb", "Bounded64B GO4 chunk hook"),
        new Patch(0xBB14, "c27204d0", "13f051f9", "Claim fresh IRQ request metadata and skip OL4 profile preloads"),
        new Patch(0xBB78, "fff73cfc", "13f036f9", "Guarded OL4 runtime-status MAIN snapshot hook"),
        new Patch(0xF106, "06702a78", "0ff00dff", "Keep mouse EP2 readiness on custom display OUT"),
        new Patch(0x1C19A, "83", "3f", "Report22 descriptor payload63 plusID=64B")
    };

    static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
    static string Hash(byte[] data) { return G9FirmwarePatch.Hash(data); }
    static byte[] Hex(string text) {
        var compact = new StringBuilder();
        foreach (char value in text) if (!char.IsWhiteSpace(value)) compact.Append(value);
        Require((compact.Length & 1) == 0, "Нечётная длина HEX-кода OLED");
        byte[] data = new byte[compact.Length / 2];
        for (int i = 0; i < data.Length; i++) {
            try { data[i] = Convert.ToByte(compact.ToString(i * 2, 2), 16); }
            catch (Exception error) { throw new InvalidDataException("Недопустимый HEX-код OLED", error); }
        }
        return data;
    }
    internal static byte[] LoadPayload(string root) {
        string text;
        using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(EmbeddedPayload)) {
            if (stream != null) using (var reader = new StreamReader(stream, Encoding.UTF8, true)) text = reader.ReadToEnd();
            else text = File.ReadAllText(Path.Combine(root, "firmware", "g9_ol4.payload.hex"), Encoding.UTF8);
        }
        byte[] data = Hex(text);
        ValidatePayload(data);
        return data;
    }
    static void ValidatePayload(byte[] payload) {
        Require(payload != null && payload.Length == PayloadSize && Hash(payload) == PayloadHash, "OL4 ARM payload SHA256/length mismatch");
    }
    static ushort Cms(byte[] data, int offset, int length) {
        ushort value = 0xFFFF;
        for (int i = offset; i < offset + length; i++) {
            value ^= (ushort)(data[i] << 8);
            for (int bit = 0; bit < 8; bit++) value = (ushort)((value & 0x8000) != 0 ? (value << 1) ^ 0x8005 : value << 1);
        }
        return value;
    }
    static bool Equal(byte[] a, int at, byte[] b, int bt, int length) {
        for (int i = 0; i < length; i++) if (a[at + i] != b[bt + i]) return false;
        return true;
    }
    static byte[] Decode(byte[] raw, int blocks, int tail) {
        Require(raw.Length == 16 + blocks * 34 + tail, "Неверная длина framed BIN");
        byte[] result = new byte[16 + blocks * 32]; Buffer.BlockCopy(raw, 0, result, 0, 16);
        for (int block = 0; block < blocks; block++) {
            int at = 16 + block * 34;
            ushort crc = (ushort)((raw[at + 32] << 8) | raw[at + 33]);
            Require(Cms(raw, at, 32) == crc, "CRC16/CMS не совпадает в блоке " + block);
            Buffer.BlockCopy(raw, at, result, 16 + block * 32, 32);
        }
        for (int i = 16 + blocks * 34; i < raw.Length; i++) Require(raw[i] == 255, "Исходный FF-tail изменён");
        return result;
    }
    internal static byte[] BuildResource(byte[] original, byte[] payload) {
        Require(original != null && original.Length == OriginalSize && Hash(original) == OriginalResourceHash, "Неверный исходный BIN: SHA256/длина");
        G9FirmwarePatch.ValidateImageHeader(original);
        ValidatePayload(payload);
        byte[] image = Decode(original, OriginalBlocks, 198);
        var changed = new HashSet<int>();
        foreach (Patch patch in Patches) {
            Require(Equal(image, patch.Offset, patch.Before, 0, patch.Before.Length), "Исходные инструкции не совпадают: " + patch.Reason);
            Buffer.BlockCopy(patch.After, 0, image, patch.Offset, patch.After.Length);
            for (int i = 0; i < patch.After.Length; i++) changed.Add((patch.Offset + i - 16) / 32);
        }
        Require(changed.Count == 7, "Ожидались ровно семь изменённых исходных блока");
        byte[] candidate = new byte[CandidateSize];
        for (int i = 16 + (OriginalBlocks + NewBlocks) * 34; i < candidate.Length; i++) candidate[i] = 255;
        Buffer.BlockCopy(original, 0, candidate, 0, 16 + OriginalBlocks * 34);
        foreach (int block in changed) {
            int at = 16 + block * 34;
            Buffer.BlockCopy(image, 16 + block * 32, candidate, at, 32);
            ushort crc = Cms(candidate, at, 32); candidate[at + 32] = (byte)(crc >> 8); candidate[at + 33] = (byte)crc;
        }
        byte[] append = new byte[NewBlocks * 32];
        for (int i = 0; i < append.Length; i++) append[i] = 255;
        Buffer.BlockCopy(payload, 0, append, 0, payload.Length);
        for (int block = 0; block < NewBlocks; block++) {
            int at = 16 + (OriginalBlocks + block) * 34;
            Buffer.BlockCopy(append, block * 32, candidate, at, 32);
            ushort crc = Cms(candidate, at, 32); candidate[at + 32] = (byte)(crc >> 8); candidate[at + 33] = (byte)crc;
        }
        G9FirmwarePatch.SealImageHeader(candidate);
        G9FirmwarePatch.VerifyPreservedHeaderFields(original, candidate);
        byte[] verified = Decode(candidate, OriginalBlocks + NewBlocks, CandidateTailPadding);
        Require(Equal(image, 16, verified, 16, image.Length - 16) && Equal(append, 0, verified, image.Length, append.Length), "Проверка декодированного кандидата не прошла");
        for (int block = 0; block < OriginalBlocks; block++)
            Require(Equal(original, 16 + block * 34, candidate, 16 + block * 34, 34) == !changed.Contains(block), "Неожиданное изменение исходного блока " + block);
        Require(((OriginalSize + 4095) & ~4095) == RoundedTransferSize && ((CandidateSize + 4095) & ~4095) == RoundedTransferSize, "Диапазон передачи увеличился");
        Require(Hash(candidate) == CandidateResourceHash, "Итоговый BIN не совпадает с проверенным офлайн-кандидатом");
        return candidate;
    }

    sealed class ResourceRegion {
        internal int Offset, Length;
        internal uint Rva, VirtualSize, DirectoryRva, DirectorySize;
    }
    static ResourceRegion FindResourceRegion(byte[] pe) {
        Require(pe != null && pe.Length >= 64 && pe[0] == 'M' && pe[1] == 'Z', "Invalid updater PE");
        int header = BitConverter.ToInt32(pe, 60);
        Require(header >= 64 && header <= pe.Length - 24 && BitConverter.ToUInt32(pe, header) == 0x4550, "Invalid updater PE header");
        int count = BitConverter.ToUInt16(pe, header + 6), optional = BitConverter.ToUInt16(pe, header + 20);
        long table = (long)header + 24 + optional;
        Require(count > 0 && count <= 96 && optional >= 120 && table + count * 40L <= pe.Length &&
            BitConverter.ToUInt16(pe, header + 24) == 0x10B, "Unexpected updater PE32 layout");
        ResourceRegion found = null;
        for (int index = 0; index < count; index++) {
            int at = checked((int)table + index * 40);
            if (Encoding.ASCII.GetString(pe, at, 8).TrimEnd('\0') != ".rsrc") continue;
            Require(found == null, "Multiple updater resource sections");
            uint length = BitConverter.ToUInt32(pe, at + 16), offset = BitConverter.ToUInt32(pe, at + 20);
            Require(length <= int.MaxValue && offset <= int.MaxValue && (long)offset + length <= pe.Length, "Resource section outside updater file");
            found = new ResourceRegion { Offset = (int)offset, Length = (int)length,
                VirtualSize = BitConverter.ToUInt32(pe, at + 8), Rva = BitConverter.ToUInt32(pe, at + 12),
                DirectoryRva = BitConverter.ToUInt32(pe, header + 24 + 112), DirectorySize = BitConverter.ToUInt32(pe, header + 24 + 116) };
        }
        Require(found != null, "Updater resource section missing"); return found;
    }
    static byte[] PreserveOriginalPeLayout(byte[] original, byte[] repacked) {
        // UpdateResource canonicalizes resources and can move the following
        // .reloc file offset even when its RVA/bytes are unchanged. The pinned
        // new resource packing fits inside the original .rsrc capacity. Copy
        // that packing back into the original PE so no other byte/header moves.
        ResourceRegion before = FindResourceRegion(original), after = FindResourceRegion(repacked);
        Require(before.Rva == after.Rva && before.DirectoryRva == after.DirectoryRva &&
            after.Length <= before.Length && after.VirtualSize <= before.VirtualSize && after.DirectorySize <= before.DirectorySize,
            "New resources do not fit the original PE layout; preparation stopped");
        byte[] result = (byte[])original.Clone();
        Array.Clear(result, before.Offset, before.Length);
        Buffer.BlockCopy(repacked, after.Offset, result, before.Offset, after.Length);
        VerifyResourceOnlyChange(original, result); return result;
    }
    internal static void VerifyResourceOnlyChange(byte[] original, byte[] candidate) {
        ResourceRegion region = FindResourceRegion(original);
        Require(candidate != null && candidate.Length == original.Length, "Updater file size changed outside the resource-only layout");
        for (int index = 0; index < original.Length; index++)
            if (index < region.Offset || index >= region.Offset + region.Length)
                Require(original[index] == candidate[index], "Updater byte changed outside .rsrc at 0x" + index.ToString("X"));
    }

    internal static string Prepare(string root) {
        root=Path.GetFullPath(root);
        string[] matches=Directory.GetFiles(Path.Combine(root,"evidence","firmware-readonly"),"Update.exe",SearchOption.AllDirectories);
        Require(matches.Length==1,"Exactly one original OEM updater required under evidence/firmware-readonly");
        string originalPath=matches[0];byte[] updater=File.ReadAllBytes(originalPath);
        Require(Hash(updater)==OriginalUpdaterHash,"Original OEM updater SHA256 mismatch");
        byte[] original=G9FirmwarePatch.ReadBinResource(originalPath,OriginalSize);
        byte[] candidate=BuildResource(original,LoadPayload(root));
        string output=Path.Combine(root,"patch-output",DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmssfffffff'Z'")+"-OL4-"+Guid.NewGuid().ToString("N").Substring(0,8));
        Directory.CreateDirectory(output);
        try {
            string target=Path.Combine(output,"FURYCUBE-G9-PRO-OL4-candidate.exe.disabled");
            File.Copy(originalPath,target,false);File.SetAttributes(target,FileAttributes.Normal);
            G9FirmwarePatch.ReplaceBinResource(target,candidate);
            byte[] packed=File.ReadAllBytes(target);
            G9FirmwarePatch.VerifyUnchangedSections(updater,packed);
            packed=PreserveOriginalPeLayout(updater,packed);File.WriteAllBytes(target,packed);
            Require(Hash(G9FirmwarePatch.ReadBinResource(target,CandidateSize))==CandidateResourceHash,"Repacked BIN differs from reviewed OL4 candidate");
            List<object> sections=G9FirmwarePatch.VerifyUnchangedSections(updater,packed);
            VerifyResourceOnlyChange(updater,packed);
            Require(Hash(packed)==CandidateUpdaterHash,"Repacked updater differs from reviewed OL4 resource-only candidate");
            Require(Hash(File.ReadAllBytes(originalPath))==OriginalUpdaterHash,"Original updater changed during preparation");
            string rawPath=Path.Combine(output,"g9-pro-1.08-zero-oled-status-OL4.CANDIDATE.bin");File.WriteAllBytes(rawPath,candidate);
            var patchList=new List<object>();
            foreach(Patch patch in Patches)patchList.Add(new {normalizedOffset="0x"+patch.Offset.ToString("X"),reason=patch.Reason,oldHex=BitConverter.ToString(patch.Before).Replace("-",""),newHex=BitConverter.ToString(patch.After).Replace("-","")});
            var report=new {
                schemaVersion=4,createdUtc=DateTime.UtcNow.ToString("o"),offlineValidation="PASS",updaterExecuted=false,physicalValidation="NOT PERFORMED BY THIS BUILDER",
                originalUpdater=new {path=originalPath,sha256=OriginalUpdaterHash,unchanged=true},
                candidateUpdater=new {path=target,sha256=Hash(packed),executableExtensionDisabled=true,allPeHeadersUnchanged=true,allBytesOutsideResourceUnchanged=true,originalPeFileLengthPreserved=true},
                candidateResource=new {path=rawPath,length=CandidateSize,sha256=CandidateResourceHash,validCrcBlocks=OriginalBlocks+NewBlocks,headerCrc32AndWordLengthVerified=true,otherHeaderFieldsPreserved=true,finalAlignmentFfBytes=CandidateTailPadding,originalChangedBlocks=7},
                append=new {cpuAddress="0x47400",payloadBytes=PayloadSize,paddedBytes=736,newCrcBlocks=NewBlocks,sha256=PayloadHash},patches=patchList,unchangedPeSections=sections,
                transferBounds=new {alignment=4096,originalRoundedBytes=RoundedTransferSize,candidateRoundedBytes=RoundedTransferSize,rangeNotExpanded=true,officialEraseRange="[0x2B000,0x51000)",officialWriteRange="[0x2B00A,0x4C00A)"},
                features=new {versionMarker="OL4",discovery="Existing GET0B8 bytes",ordinaryButtonNoOledHoldWait=true,upload="GO4 fixed64B/40px chunks,12reports per120x28 frame",status="A0 22 40 00 00 00 00 00; GS4fixed64B CRC62LE",stockUsbCallbacksByteIdentical=false,usbCallbackPatchAddress="0x37896",report22DoesNotForceMouseReady=true,claimedIrqRequestMetadataReload=true,persistentGlobalRamBytes=0,nativeRendererMayOverwrite=true,pictureFlashPersistence=false},
                limits=new {installedFullBackup="NOT AVAILABLE",rollback="NOT VERIFIED",publicGeneralHardwareCompatibility="NOT VERIFIED",privateExperimentRiskAcceptedByOwner=true,livePcWidgetsRequireHubUpdates=true,flashAction="NONE; offline artifact preparation only"}
            };
            File.WriteAllText(Path.Combine(output,"ol4-patch-report.json"),new JavaScriptSerializer().Serialize(report),Utf8);
            File.WriteAllText(Path.Combine(output,"OL4-EXPERIMENT.txt"),
                "OL4 EXPERIMENTAL CANDIDATE; OFFLINE PREPARATION ONLY. No updater was run.\r\n"+
                "Preserves NoWait button behavior. OLED uses64-byte USB reports with40 pixels each; the stock132-byte USB OUT path loses earlier chunks.\r\n"+
                "GO4 uploads and GS4 runtime status use64 bytes includingID.12reports cover120x28 pixels. Report22 descriptor changed accordingly.\r\n"+
                "Checked SHA256,3962 CRC16/CMS blocks, headerCRC32/word length, seven original block changes and one four-byte EP0 ready-flag guard; other USB callback bytes preserved. Transfer round-size remains0x21000.\r\n"+
                "Owner accepted private experimental flash risk, including loss of this unit. Full installed backup and independent rollback remain unverified. Public hardware compatibility and recovery are not qualified.\r\n"+
                "The prepare hook reloads claimed report/length/selector after the pending flag is cleared, fixing stale metadata across an EP0 IRQ.\r\n"+
                "Custom report22 OUT completion no longer forces mouse endpoint2 ready while a report may still be transmitting; real endpoint2 completion stays stock.\r\n"+
                "OLED display, native redraw/sleep behavior and retention require separate physical validation. Live PC widgets require running HUB updates.\r\n",Utf8);
            return output;
        } catch(Exception error) {File.WriteAllText(Path.Combine(output,"PREPARATION-FAILED.txt"),"OFFLINE VALIDATION FAILED\r\n"+error,Utf8);throw;}
    }
}
