using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;

// Offline artifact preparation only. This class has no HID, process-start or
// firmware-install API. The official updater is mapped as data by G9FirmwarePatch.
internal static class G9FirmwareOledPatch {
    internal const string OriginalUpdaterHash = "558031de581d5ba2317abd367c793776def812f4ec7eff6cff86850658748be4";
    internal const string OriginalResourceHash = "f7c4db8ffae37d033cbde4d670d70f32bd9ea62b36d0e6d7ba9cba5ce7d9efcc";
    internal const string PayloadHash = "e7ed5939d60dff19b856092c57d4bef97e0e3ceb01ba8db34ddf0fcf2797320d";
    internal const string CandidateResourceHash = "f7a38b0a672f88767ad34a04bd3ac1e6f7f9cd62804d6aec6c24278d5e1a75fd";
    internal const int OriginalSize = 134140, CandidateSize = 134248;
    const int OriginalBlocks = 3939, NewBlocks = 9, RoundedTransferSize = 0x21000;
    const string EmbeddedPayload = "Furycube.G9.OledPayload";
    static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

    sealed class Patch {
        internal int Offset; internal byte[] Before, After; internal string Reason;
        internal Patch(int offset, string before, string after, string reason) {
            Offset = offset; Before = Hex(before); After = Hex(after); Reason = reason;
        }
    }
    static readonly Patch[] Patches = {
        new Patch(0x988C, "0d70", "c046", "Disable OLED hold gesture"),
        new Patch(0xB504, "11704078", "13f0b4fb", "Version-report OL2 marker hook"),
        new Patch(0xB544, "00f010ff", "13f09dfb", "Wired OLED WRITE_PAGE hook")
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
            else text = File.ReadAllText(Path.Combine(root, "firmware", "g9_oled.payload.hex"), Encoding.UTF8);
        }
        byte[] data = Hex(text);
        Require(data.Length == 280 && Hash(data) == PayloadHash, "SHA256 встроенного ARM-кода OLED не совпадает");
        return data;
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
        Require(payload != null && payload.Length == 280 && Hash(payload) == PayloadHash, "Неверный ARM payload: SHA256/длина");
        byte[] image = Decode(original, OriginalBlocks, 198);
        var changed = new HashSet<int>();
        foreach (Patch patch in Patches) {
            Require(Equal(image, patch.Offset, patch.Before, 0, patch.Before.Length), "Исходные инструкции не совпадают: " + patch.Reason);
            Buffer.BlockCopy(patch.After, 0, image, patch.Offset, patch.After.Length);
            for (int i = 0; i < patch.After.Length; i++) changed.Add((patch.Offset + i - 16) / 32);
        }
        Require(changed.Count == 3, "Ожидались ровно три изменённых исходных блока");
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

    internal static string Prepare(string root) {
        root = Path.GetFullPath(root);
        string[] matches = Directory.GetFiles(Path.Combine(root, "evidence", "firmware-readonly"), "Update.exe", SearchOption.AllDirectories);
        Require(matches.Length == 1, "Требуется ровно один официальный Update.exe в evidence/firmware-readonly");
        string originalPath = matches[0]; byte[] updater = File.ReadAllBytes(originalPath);
        Require(Hash(updater) == OriginalUpdaterHash, "SHA256 официального Update.exe не совпадает");
        byte[] original = G9FirmwarePatch.ReadBinResource(originalPath, OriginalSize);
        byte[] payload = LoadPayload(root), candidate = BuildResource(original, payload);
        string output = Path.Combine(root, "patch-output", DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmssfffffff'Z'") + "-OL2-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(output);
        try {
            string candidatePath = Path.Combine(output, "FURYCUBE-G9-PRO-OL2-candidate.exe.disabled");
            File.Copy(originalPath, candidatePath, false); File.SetAttributes(candidatePath, FileAttributes.Normal);
            G9FirmwarePatch.ReplaceBinResource(candidatePath, candidate);
            Require(Hash(G9FirmwarePatch.ReadBinResource(candidatePath, CandidateSize)) == CandidateResourceHash, "BIN в перепакованном updater не совпадает");
            byte[] packed = File.ReadAllBytes(candidatePath);
            List<object> sections = G9FirmwarePatch.VerifyUnchangedSections(updater, packed);
            Require(Hash(File.ReadAllBytes(originalPath)) == OriginalUpdaterHash, "Исходный updater изменён во время подготовки");
            string rawPath = Path.Combine(output, "g9-pro-1.08-zero-oled-OL2.CANDIDATE.bin");
            File.WriteAllBytes(rawPath, candidate);
            Require(Hash(File.ReadAllBytes(rawPath)) == CandidateResourceHash, "Сохранённый raw BIN повреждён");
            var patchList = new List<object>();
            foreach (Patch patch in Patches) patchList.Add(new { normalizedOffset = "0x" + patch.Offset.ToString("X"), reason = patch.Reason,
                oldHex = BitConverter.ToString(patch.Before).Replace("-", ""), newHex = BitConverter.ToString(patch.After).Replace("-", "") });
            var report = new {
                schemaVersion = 2, createdUtc = DateTime.UtcNow.ToString("o"), offlineValidation = "PASS", installation = "NOT INSTALLED", physicalValidation = "NOT TESTED",
                originalUpdater = new { path = originalPath, sha256 = OriginalUpdaterHash, unchanged = true, executed = false },
                originalResource = new { length = OriginalSize, sha256 = OriginalResourceHash, validCrcBlocks = OriginalBlocks },
                candidateResource = new { path = rawPath, length = CandidateSize, sha256 = CandidateResourceHash, validCrcBlocks = OriginalBlocks + NewBlocks, header16Unchanged = true,
                    originalChangedBlocks = new[] { (0x988C - 16) / 32, (0xB504 - 16) / 32, (0xB544 - 16) / 32 }, remainingOriginalBlocksByteIdentical = true },
                patches = patchList,
                append = new { cpuAddress = "0x47400", normalizedOffset = "0x1EC70", resourceOffset = "0x20B36", payloadBytes = 280, paddedBytes = 288, newCrcBlocks = NewBlocks, sha256 = PayloadHash },
                candidateUpdater = new { path = candidatePath, sha256 = Hash(packed), executableExtensionDisabled = true, executed = false },
                unchangedPeSections = sections,
                transferBounds = new { alignment = 4096, originalRoundedBytes = RoundedTransferSize, candidateRoundedBytes = RoundedTransferSize, rangeNotExpanded = true,
                    officialEraseRange = "[0x2B000,0x51000)", officialWriteRange = "[0x2B00A,0x4C00A)", physicalFlashMappingNotValidated = true },
                features = new { versionMarker = "OL2", discovery = "Existing GET 0B, 8 bytes only", ordinaryZeroNoOledHoldWait = true,
                    oled = "Wired-only GO2 WRITE_PAGE, 120x28 preview, 480 page bytes", persistentGlobalRamBytes = 0, ownership = false, nativeRendererMayOverwrite = true, pictureFlashPersistence = false },
                limits = new { currentDeviceFirmwareBackup = "NOT AVAILABLE", fullCurrentImageIdentity = "NOT CONFIRMED", rollback = "NOT VERIFIED", bootloaderCompatibility = "NOT TESTED",
                    markerIsNotBootCompatibilityProof = true, livePcWidgetsRequireHubUpdates = true, flashAction = "NONE; offline artifact preparation only" }
            };
            File.WriteAllText(Path.Combine(output, "oled-patch-report.json"), new JavaScriptSerializer().Serialize(report), Utf8);
            File.WriteAllText(Path.Combine(output, "ПРОВЕРКА-OL2.txt"),
                "ЛОКАЛЬНЫЙ КАНДИДАТ OL2: ОФЛАЙН-ПРОВЕРКИ ПРОШЛИ. ПРОШИВКА НЕ УСТАНАВЛИВАЛАСЬ.\r\n\r\n" +
                "Подготовлены raw BIN и копия официального обновлятора с окончанием .exe.disabled. Никакой updater не запускался; USB не использовался.\r\n" +
                "Обычная «0»: отключён OLED-жест удержания; глобальная задержка кнопок не увеличивается. Долгое удержание экранной кнопки больше не переключает OLED.\r\n" +
                "OLED OL2: настоящий Thumb-1 код, 280 байт, 9 новых CRC-блоков. Страничная запись 120x28 / 480 байт только через кабель. Маркер OL2 читается через штатный GET 0B длиной 8 байт; отсутствие маркера запрещает загрузку.\r\n\r\n" +
                "Проверены SHA256 оригинального EXE/BIN и ARM payload, все 3939 исходных и 3948 итоговых CRC-блоков, три точных изменения кода, сохранность остальных блоков, неизменность заголовка BIN и всех PE-секций кроме .rsrc. Итоговый raw BIN совпадает с проверенным Python/ARM-кандидатом.\r\n" +
                "Размер BIN 134140 -> 134248; округлённая передача остаётся 0x21000 байт. Официальный updater стирает [0x2B000,0x51000), записывает [0x2B00A,0x4C00A) и не делает резервную копию устройства.\r\n\r\n" +
                "ОГРАНИЧЕНИЯ: полный текущий образ мыши не считан, проверенного отката нет. PID/версия/маркер OL2 не доказывают совместимость загрузчика или фактическую работу OLED. На устройстве кандидат не проверен. Штатный renderer может перезаписывать картинки; сохранение после выключения/сна не реализовано. Живым виджетам ПК нужна работающая фоновая часть HUB; обычной «0» она не нужна.\r\n\r\n" +
                "Подробности: oled-patch-report.json. Это подготовка проверяемых файлов, не разрешение и не команда на установку.\r\n", Utf8);
            return output;
        } catch (Exception error) {
            File.WriteAllText(Path.Combine(output, "PREPARATION-FAILED.txt"), "OFFLINE VALIDATION FAILED\r\n" + error, Utf8); throw;
        }
    }
}
