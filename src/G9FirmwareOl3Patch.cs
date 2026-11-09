using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;

// Offline artifact preparation only. This class has no HID, process-start or
// firmware-install API. The official updater is mapped as data by G9FirmwarePatch.
internal static class G9FirmwareOl3Patch {
    internal const string OriginalUpdaterHash = "558031de581d5ba2317abd367c793776def812f4ec7eff6cff86850658748be4";
    internal const string OriginalResourceHash = "f7c4db8ffae37d033cbde4d670d70f32bd9ea62b36d0e6d7ba9cba5ce7d9efcc";
    internal const string PayloadHash = "f9938ccff010dd680f1df37c0b0a97a3a6af6c8aeb12859475ca4dc19eba85ed";
    internal const string CandidateResourceHash = "cf4048c67be8e922bf4a129bfec80c988e448867d57cb44e186a9c01ac7af03a";
    internal const string Ol2PrefixHash = "e7ed5939d60dff19b856092c57d4bef97e0e3ceb01ba8db34ddf0fcf2797320d";
    internal const int OriginalSize = 134140, CandidateSize = 134622, PayloadSize = 636;
    internal const int OriginalBlocks = 3939, NewBlocks = 20;
    const int RoundedTransferSize = 0x21000;
    const string EmbeddedPayload = "Furycube.G9.Ol3Payload";
    static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

    sealed class Patch {
        internal int Offset; internal byte[] Before, After; internal string Reason;
        internal Patch(int offset, string before, string after, string reason) {
            Offset = offset; Before = Hex(before); After = Hex(after); Reason = reason;
        }
    }
    static readonly Patch[] Patches = {
        new Patch(0x988C, "0d70", "c046", "Disable OLED hold gesture"),
        new Patch(0xB504, "11704078", "13f040fc", "Version-report OL3 marker hook"),
        new Patch(0xB544, "00f010ff", "13f09dfb", "Wired OLED WRITE_PAGE hook"),
        new Patch(0xBB14, "c27204d0", "13f041f9", "Bypass profile/macro preloads for OL3 status"),
        new Patch(0xBB78, "fff73cfc", "13f01ff9", "Guarded OL3 runtime-status MAIN snapshot hook")
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
            else text = File.ReadAllText(Path.Combine(root, "firmware", "g9_ol3.payload.hex"), Encoding.UTF8);
        }
        byte[] data = Hex(text);
        ValidatePayload(data);
        return data;
    }
    static void ValidatePayload(byte[] payload) {
        Require(payload != null && payload.Length == PayloadSize && Hash(payload) == PayloadHash, "Неверный ARM payload OL3: SHA256/длина");
        var prefix = new byte[280]; Buffer.BlockCopy(payload, 0, prefix, 0, prefix.Length);
        Require(Hash(prefix) == Ol2PrefixHash, "Исходная реализация WRITE_PAGE OL2 в payload изменилась");
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
        ValidatePayload(payload);
        byte[] image = Decode(original, OriginalBlocks, 198);
        var changed = new HashSet<int>();
        foreach (Patch patch in Patches) {
            Require(Equal(image, patch.Offset, patch.Before, 0, patch.Before.Length), "Исходные инструкции не совпадают: " + patch.Reason);
            Buffer.BlockCopy(patch.After, 0, image, patch.Offset, patch.After.Length);
            for (int i = 0; i < patch.After.Length; i++) changed.Add((patch.Offset + i - 16) / 32);
        }
        Require(changed.Count == 5, "Ожидались ровно пять изменённых исходных блока");
        byte[] candidate = new byte[CandidateSize];
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
        Require(Equal(original, 0, candidate, 0, 16), "Заголовок BIN изменён");
        byte[] verified = Decode(candidate, OriginalBlocks + NewBlocks, 0);
        Require(Equal(image, 0, verified, 0, image.Length) && Equal(append, 0, verified, image.Length, append.Length), "Проверка декодированного кандидата не прошла");
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
        root = Path.GetFullPath(root);
        string[] matches = Directory.GetFiles(Path.Combine(root, "evidence", "firmware-readonly"), "Update.exe", SearchOption.AllDirectories);
        Require(matches.Length == 1, "Требуется ровно один официальный Update.exe в evidence/firmware-readonly");
        string originalPath = matches[0]; byte[] updater = File.ReadAllBytes(originalPath);
        Require(Hash(updater) == OriginalUpdaterHash, "SHA256 официального Update.exe не совпадает");
        byte[] original = G9FirmwarePatch.ReadBinResource(originalPath, OriginalSize);
        byte[] payload = LoadPayload(root), candidate = BuildResource(original, payload);
        string output = Path.Combine(root, "patch-output", DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmssfffffff'Z'") + "-OL3-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(output);
        try {
            string candidatePath = Path.Combine(output, "FURYCUBE-G9-PRO-OL3-candidate.exe.disabled");
            File.Copy(originalPath, candidatePath, false); File.SetAttributes(candidatePath, FileAttributes.Normal);
            G9FirmwarePatch.ReplaceBinResource(candidatePath, candidate);
            byte[] packed = File.ReadAllBytes(candidatePath);
            G9FirmwarePatch.VerifyUnchangedSections(updater, packed);
            packed = PreserveOriginalPeLayout(updater, packed);
            File.WriteAllBytes(candidatePath, packed);
            Require(Hash(G9FirmwarePatch.ReadBinResource(candidatePath, CandidateSize)) == CandidateResourceHash, "BIN в перепакованном updater не совпадает");
            List<object> sections = G9FirmwarePatch.VerifyUnchangedSections(updater, packed);
            VerifyResourceOnlyChange(updater, File.ReadAllBytes(candidatePath));
            Require(Hash(File.ReadAllBytes(originalPath)) == OriginalUpdaterHash, "Исходный updater изменён во время подготовки");
            string rawPath = Path.Combine(output, "g9-pro-1.08-zero-oled-OL3.CANDIDATE.bin");
            File.WriteAllBytes(rawPath, candidate);
            Require(Hash(File.ReadAllBytes(rawPath)) == CandidateResourceHash, "Сохранённый raw BIN повреждён");
            var patchList = new List<object>();
            foreach (Patch patch in Patches) patchList.Add(new { normalizedOffset = "0x" + patch.Offset.ToString("X"), reason = patch.Reason,
                oldHex = BitConverter.ToString(patch.Before).Replace("-", ""), newHex = BitConverter.ToString(patch.After).Replace("-", "") });
            var report = new {
                schemaVersion = 3, createdUtc = DateTime.UtcNow.ToString("o"), offlineValidation = "PASS", installation = "NOT INSTALLED", physicalValidation = "NOT TESTED",
                originalUpdater = new { path = originalPath, sha256 = OriginalUpdaterHash, unchanged = true, executed = false },
                originalResource = new { length = OriginalSize, sha256 = OriginalResourceHash, validCrcBlocks = OriginalBlocks },
                candidateResource = new { path = rawPath, length = CandidateSize, sha256 = CandidateResourceHash, validCrcBlocks = OriginalBlocks + NewBlocks, header16Unchanged = true,
                    originalChangedBlocks = new[] { (0x988C - 16) / 32, (0xB504 - 16) / 32, (0xB544 - 16) / 32, (0xBB14 - 16) / 32, (0xBB78 - 16) / 32 }, remainingOriginalBlocksByteIdentical = true },
                patches = patchList,
                append = new { cpuAddress = "0x47400", normalizedOffset = "0x1EC70", resourceOffset = "0x20B36", payloadBytes = PayloadSize, paddedBytes = 640, newCrcBlocks = NewBlocks, sha256 = PayloadHash,
                    ol2PrefixBytes = 280, ol2PrefixSha256 = Ol2PrefixHash, ol2PrefixByteIdentical = true },
                candidateUpdater = new { path = candidatePath, sha256 = Hash(packed), executableExtensionDisabled = true, executed = false,
                    allPeHeadersUnchanged = true, allBytesOutsideResourceUnchanged = true, originalPeFileLengthPreserved = true },
                unchangedPeSections = sections,
                transferBounds = new { alignment = 4096, originalRoundedBytes = RoundedTransferSize, candidateRoundedBytes = RoundedTransferSize, rangeNotExpanded = true,
                    officialEraseRange = "[0x2B000,0x51000)", officialWriteRange = "[0x2B00A,0x4C00A)", physicalFlashMappingNotValidated = true },
                features = new { versionMarker = "OL3", discovery = "Existing GET 0x0B, 8 bytes only", ordinaryZeroNoOledHoldWait = true,
                    oled = "Wired-only GO2 WRITE_PAGE, 120x28 preview, 480 page bytes", persistentGlobalRamBytes = 0, ownership = false, nativeRendererMayOverwrite = true, pictureFlashPersistence = false },
                runtimeStatus = new { query = "A0 22 84 00 00 00 00 00", response = "0x22/132 GS3, status invalid/valid/busy, profile/stage/configuredDpiLE, CRC16/CCITT-FALSE LE",
                    guardedMainLoopSnapshot = true, profilePreloadSkipped = true, profileRestoreSkipped = true, nvmWrites = false, originalA0TransportWakeRemains = true,
                    passiveProfileEventsNotUsedAsCurrentDpiSource = true, physicalValidation = "NOT TESTED" },
                limits = new { currentDeviceFirmwareBackup = "NOT AVAILABLE", fullCurrentImageIdentity = "NOT CONFIRMED", rollback = "NOT VERIFIED", bootloaderCompatibility = "NOT TESTED",
                    completeRecoveryRequiredBeforeInstallation = true, installationBlockedUntilRecoveryProven = true,
                    markerIsNotBootCompatibilityProof = true, livePcWidgetsRequireHubUpdates = true, flashAction = "NONE; offline artifact preparation only" }
            };
            File.WriteAllText(Path.Combine(output, "ol3-patch-report.json"), new JavaScriptSerializer().Serialize(report), Utf8);
            File.WriteAllText(Path.Combine(output, "ПРОВЕРКА-OL3.txt"),
                "ЛОКАЛЬНЫЙ КАНДИДАТ OL3: ОФЛАЙН-ПРОВЕРКИ ПРОШЛИ. ПРОШИВКА НЕ УСТАНАВЛИВАЛАСЬ.\r\n\r\n" +
                "УСТАНОВКА ЗАПРЕЩЕНА, ПОКА НЕ ПОДТВЕРЖДЕНО ПОЛНОЕ НЕЗАВИСИМОЕ ВОССТАНОВЛЕНИЕ УСТРОЙСТВА.\r\n\r\n" +
                "Подготовлены raw BIN и копия официального обновлятора с окончанием .exe.disabled. Никакой updater не запускался; USB не использовался.\r\n" +
                "Обычная «0»: отключён OLED-жест удержания; глобальная задержка кнопок не увеличивается. Долгое удержание экранной кнопки больше не переключает OLED.\r\n" +
                "OLED OL3: настоящий Thumb-1 код, 636 байт, 20 новых CRC-блоков. Первые 280 байт совпадают с проверенным OL2 WRITE_PAGE. Страничная запись 120x28 / 480 байт только через кабель. Маркер OL3 читается через штатный GET 0x0B длиной 8 байт.\r\n" +
                "Статус OL3: согласованный снимок текущего профиля, ступени и настроенного DPI из MAIN, отдельные ответы invalid/busy. Новые hooks пропускают загрузку/восстановление профиля для этого запроса; NVM не записывается. Сохраняется штатное пробуждение A0. Обычный OL2 и stock не поддерживают этот статус: GET 0x22 для них запрещён.\r\n\r\n" +
                "Проверены SHA256 оригинального EXE/BIN и ARM payload, все 3939 исходных и 3959 итоговых CRC-блоков, пять точных изменений кода, сохранность остальных блоков, неизменность заголовка BIN. Новые ресурсы размещены в исходной ёмкости .rsrc: все PE-заголовки, длина EXE и каждый байт вне .rsrc сохранены. Итоговый raw BIN совпадает с проверенным Python/ARM-кандидатом.\r\n" +
                "Размер BIN 134140 -> 134622; округлённая передача остаётся 0x21000 байт. Официальный updater стирает [0x2B000,0x51000), записывает [0x2B00A,0x4C00A) и не делает резервную копию устройства.\r\n\r\n" +
                "ОГРАНИЧЕНИЯ: полный текущий образ мыши не считан, проверенного отката нет. PID/версия/маркер OL3 не доказывают совместимость загрузчика или фактическую работу OLED. На устройстве кандидат не проверен. Штатный renderer может перезаписывать картинки; сохранение после выключения/сна не реализовано. Живым виджетам ПК нужна работающая фоновая часть HUB; обычной «0» она не нужна.\r\n\r\n" +
                "Подробности: ol3-patch-report.json. Это подготовка проверяемых файлов, не разрешение и не команда на установку.\r\n", Utf8);
            return output;
        } catch (Exception error) {
            File.WriteAllText(Path.Combine(output, "PREPARATION-FAILED.txt"), "OFFLINE VALIDATION FAILED\r\n" + error, Utf8); throw;
        }
    }
}
