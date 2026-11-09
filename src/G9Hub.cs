using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

internal static class G9FirmwarePatch {
    internal const string OriginalUpdaterHash = "558031de581d5ba2317abd367c793776def812f4ec7eff6cff86850658748be4";
    internal const string OriginalResourceHash = "f7c4db8ffae37d033cbde4d670d70f32bd9ea62b36d0e6d7ba9cba5ce7d9efcc";
    internal const string PatchedResourceHash = "01908b65a972ae04282392088e2581640365a0d0712c9f281d6f54691ea14865";
    const int ResourceSize = 134140, InstructionOffset = 0xA212, BlockOffset = 0xA1F6, CrcOffset = 0xA216;
    static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    static extern IntPtr FindResourceEx(IntPtr module, string type, IntPtr name, ushort language);
    [DllImport("kernel32.dll", SetLastError=true)] static extern uint SizeofResource(IntPtr module, IntPtr resource);
    [DllImport("kernel32.dll", SetLastError=true)] static extern IntPtr LoadResource(IntPtr module, IntPtr resource);
    [DllImport("kernel32.dll", SetLastError=true)] static extern IntPtr LockResource(IntPtr resource);
    [DllImport("kernel32.dll")] static extern bool FreeLibrary(IntPtr module);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    static extern IntPtr BeginUpdateResource(string path, bool deleteExisting);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    static extern bool UpdateResource(IntPtr update, string type, IntPtr name, ushort language, byte[] data, uint length);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool EndUpdateResource(IntPtr update, bool discard);

    static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
    internal static string Hash(byte[] data) {
        using (SHA256 sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "").ToLowerInvariant();
    }
    // img_hdr_t: CRC32 at0, length in 4-byte words at6. CRC covers the
    // framed body including CRC16 bytes and FF tail, with no final XOR.
    // Matching this file format does not prove installed-loader acceptance.
    internal static uint BodyCrc32(byte[] data) {
        uint crc = 0xFFFFFFFF;
        for (int i = 16; i < data.Length; i++) {
            crc ^= data[i];
            for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xEDB88320u : 0u);
        }
        return crc;
    }
    internal static void ValidateImageHeader(byte[] data) {
        Require(data != null && data.Length >= 16 && (data.Length & 3) == 0 && data.Length / 4 < 0xFFFF, "Invalid image word length");
        Require(BitConverter.ToUInt16(data, 6) * 4 == data.Length, "Image header length is stale");
        Require(BitConverter.ToUInt32(data, 0) == BodyCrc32(data), "Image header body CRC32 is stale");
    }
    internal static void SealImageHeader(byte[] data) {
        Require(data != null && data.Length >= 16 && (data.Length & 3) == 0 && data.Length / 4 < 0xFFFF, "Invalid image word length");
        ushort words = checked((ushort)(data.Length / 4));
        data[6] = (byte)words; data[7] = (byte)(words >> 8);
        uint crc = BodyCrc32(data);
        for (int i = 0; i < 4; i++) data[i] = (byte)(crc >> (i * 8));
        ValidateImageHeader(data);
    }
    internal static void VerifyPreservedHeaderFields(byte[] before, byte[] after) {
        for (int i = 4; i < 16; i++)
            if (i != 6 && i != 7) Require(before[i] == after[i], "Image identity/status field changed");
    }
    static ushort Crc16Cms(byte[] data, int offset, int length) {
        ushort crc = 0xFFFF;
        for (int i = offset; i < offset + length; i++) {
            crc ^= (ushort)(data[i] << 8);
            for (int bit = 0; bit < 8; bit++) crc = (ushort)((crc & 0x8000) != 0 ? (crc << 1) ^ 0x8005 : crc << 1);
        }
        return crc;
    }
    internal static byte[] ReadBinResource(string path, int expectedSize=ResourceSize) {
        // DATAFILE | IMAGE_RESOURCE maps resources without running the PE entrypoint or DllMain.
        IntPtr module = LoadLibraryEx(path, IntPtr.Zero, 0x22);
        if (module == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Cannot map updater resources as data");
        try {
            IntPtr resource = FindResourceEx(module, "BIN", new IntPtr(129), 2052);
            if (resource == IntPtr.Zero) throw new InvalidDataException("BIN/129/2052 resource not found");
            uint size = SizeofResource(module, resource);
            Require(size == expectedSize, "Unexpected firmware resource length");
            IntPtr loaded = LoadResource(module, resource);
            IntPtr address = loaded == IntPtr.Zero ? IntPtr.Zero : LockResource(loaded);
            if (address == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Cannot read firmware resource");
            byte[] data = new byte[(int)size]; Marshal.Copy(address, data, 0, data.Length); return data;
        } finally { FreeLibrary(module); }
    }
    sealed class Section {
        internal string Name; internal uint Rva, VirtualSize, RawSize, RawOffset, Flags; internal byte[] Bytes;
    }
    static List<Section> ReadSections(byte[] pe) {
        Require(pe.Length >= 64 && pe[0] == 'M' && pe[1] == 'Z', "Invalid DOS header");
        int header = BitConverter.ToInt32(pe, 0x3C);
        Require(header >= 64 && header <= pe.Length - 24 && BitConverter.ToUInt32(pe, header) == 0x4550, "Invalid PE header");
        int count = BitConverter.ToUInt16(pe, header + 6), optional = BitConverter.ToUInt16(pe, header + 20);
        long table = (long)header + 24 + optional;
        Require(count > 0 && count <= 96 && table + count * 40L <= pe.Length, "Invalid PE section table");
        var sections = new List<Section>();
        for (int i = 0; i < count; i++) {
            int at = checked((int)table + i * 40);
            var s = new Section { Name = Encoding.ASCII.GetString(pe, at, 8).TrimEnd('\0'),
                VirtualSize = BitConverter.ToUInt32(pe, at + 8), Rva = BitConverter.ToUInt32(pe, at + 12),
                RawSize = BitConverter.ToUInt32(pe, at + 16), RawOffset = BitConverter.ToUInt32(pe, at + 20),
                Flags = BitConverter.ToUInt32(pe, at + 36) };
            Require((long)s.RawOffset + s.RawSize <= pe.Length && s.RawSize <= int.MaxValue, "PE section outside file");
            s.Bytes = new byte[(int)s.RawSize]; Buffer.BlockCopy(pe, (int)s.RawOffset, s.Bytes, 0, s.Bytes.Length); sections.Add(s);
        }
        return sections;
    }
    internal static List<object> VerifyUnchangedSections(byte[] source, byte[] candidate) {
        var before = ReadSections(source); var after = ReadSections(candidate);
        Require(before.Count == after.Count, "Resource update changed PE section count");
        var checkedSections = new List<object>(); bool resourceFound = false;
        for (int i = 0; i < before.Count; i++) {
            Section a = before[i], b = after[i]; Require(a.Name == b.Name, "Resource update changed PE section names/order");
            if (a.Name == ".rsrc") { resourceFound = true; continue; }
            Require(a.Rva == b.Rva && a.VirtualSize == b.VirtualSize && a.RawSize == b.RawSize && a.Flags == b.Flags,
                "Resource update changed layout of section " + a.Name);
            Require(Hash(a.Bytes) == Hash(b.Bytes), "Resource update changed bytes in section " + a.Name);
            checkedSections.Add(new { name = a.Name, rva = "0x" + a.Rva.ToString("X"), size = a.RawSize, sha256 = Hash(a.Bytes) });
        }
        Require(resourceFound, "Resource section missing"); return checkedSections;
    }
    internal static void ReplaceBinResource(string candidate, byte[] resource) {
        IntPtr update = BeginUpdateResource(candidate, false);
        if (update == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Cannot begin resource update");
        bool completed = false;
        try {
            if (!UpdateResource(update, "BIN", new IntPtr(129), 2052, resource, (uint)resource.Length))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Cannot replace BIN resource");
            IntPtr commit = update; update = IntPtr.Zero;
            if (!EndUpdateResource(commit, false)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Cannot save resource update");
            completed = true;
        } finally { if (!completed && update != IntPtr.Zero) EndUpdateResource(update, true); }
    }
    internal static byte[] BuildNoWaitResource(byte[] original) {
        Require(original != null && original.Length == ResourceSize && Hash(original) == OriginalResourceHash, "Original BIN resource SHA256/length mismatch");
        ValidateImageHeader(original);
        Require(original[InstructionOffset] == 0x0D && original[InstructionOffset + 1] == 0x70, "Original instruction differs");
        Require(original[CrcOffset] == 0x53 && original[CrcOffset + 1] == 0xAA && Crc16Cms(original, BlockOffset, 32) == 0x53AA,
            "Original block CRC16/CMS mismatch");
        byte[] patched = (byte[])original.Clone(); patched[InstructionOffset] = 0xC0; patched[InstructionOffset + 1] = 0x46;
        ushort crc = Crc16Cms(patched, BlockOffset, 32); Require(crc == 0x3433, "Patched block CRC differs from reviewed value");
        patched[CrcOffset] = (byte)(crc >> 8); patched[CrcOffset + 1] = (byte)crc;
        SealImageHeader(patched);
        VerifyPreservedHeaderFields(original, patched);
        int changes = 0;
        for (int i = 0; i < original.Length; i++) if (original[i] != patched[i]) {
            Require(i < 4 || i == InstructionOffset || i == InstructionOffset + 1 || i == CrcOffset || i == CrcOffset + 1, "Unexpected firmware byte changed"); changes++;
        }
        Require(changes == 8 && Hash(patched) == PatchedResourceHash, "Patched resource hash or changed-byte count differs");
        return patched;
    }
    internal static string Prepare(string root) {
        root = Path.GetFullPath(root);
        string inputDirectory = Path.Combine(root, "evidence", "firmware-readonly");
        string[] matches = Directory.GetFiles(inputDirectory, "Update.exe", SearchOption.AllDirectories);
        Require(matches.Length == 1, "Expected exactly one official Update.exe in evidence/firmware-readonly");
        string sourcePath = matches[0]; byte[] source = File.ReadAllBytes(sourcePath);
        Require(Hash(source) == OriginalUpdaterHash, "Official updater SHA256 mismatch; preparation stopped");
        byte[] original = ReadBinResource(sourcePath), patched = BuildNoWaitResource(original);
        string output = Path.Combine(root, "patch-output", DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmssfffffff'Z'") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(output);
        try {
            string candidate = Path.Combine(output, "FURYCUBE-G9-PRO-OLED-NoWait-candidate.exe.disabled");
            File.Copy(sourcePath, candidate, false); File.SetAttributes(candidate, FileAttributes.Normal);
            ReplaceBinResource(candidate, patched);
            Require(Hash(ReadBinResource(candidate)) == PatchedResourceHash, "Candidate resource verification failed");
            var sections = VerifyUnchangedSections(source, File.ReadAllBytes(candidate));
            Require(Hash(File.ReadAllBytes(sourcePath)) == OriginalUpdaterHash, "Original updater changed during preparation");
            File.WriteAllBytes(Path.Combine(output, "firmware-original-resource.bin"), original);
            File.WriteAllBytes(Path.Combine(output, "firmware-no-oled-wait.bin"), patched);
            var report = new {
                schemaVersion = 1, createdUtc = DateTime.UtcNow.ToString("o"), offlineValidation = "PASS", installation = "NOT INSTALLED", physicalValidation = "NOT TESTED",
                originalUpdater = new { path = sourcePath, sha256 = OriginalUpdaterHash },
                resource = new { type = "BIN", id = 129, language = 2052, length = ResourceSize, originalSha256 = OriginalResourceHash, patchedSha256 = PatchedResourceHash },
                imageHeader = new { bodyCrc32Verified = true, wordLengthVerified = true, otherFieldsPreserved = true, installedLoaderAcceptanceVerified = false },
                patch = new { description = "Disable OLED long-hold toggle; no additional GPIO delay", instructionOffset = "0xA212", originalInstruction = "0D70", patchedInstruction = "C046",
                    crcAlgorithm = "CRC16/CMS poly=8005 init=FFFF MSB BE", blockOffset = "0xA1F6", blockLength = 32, crcOffset = "0xA216", originalCrc = "53AA", patchedCrc = "3433", changedByteCount = 8 },
                candidate = new { path = candidate, sha256 = Hash(File.ReadAllBytes(candidate)), executableExtensionDisabled = true },
                unchangedPeSections = sections,
                limits = new { officialEraseRange = "[0x2B000,0x51000)", officialWriteRange = "[0x2B00A,0x4C00A)",
                    currentDeviceFirmwareBackup = "NOT AVAILABLE", fullCurrentImageIdentity = "NOT CONFIRMED", compatibility = "PID and version alone do not prove compatibility",
                    flashAction = "DISABLED: this tool only prepares local artifacts", rollback = "NOT VERIFIED" }
            };
            string json = new JavaScriptSerializer().Serialize(report);
            File.WriteAllText(Path.Combine(output, "patch-report.json"), json, Utf8);
            File.WriteAllText(Path.Combine(output, "ПРОЧИТАЙ.txt"),
                "FURYCUBE G9 PRO — подготовленное исправление OLED\r\n\r\n" +
                "Патч подготовлен; в мышь не установлен. Физическая проверка: НЕ ПРОВЕДЕНА.\r\n" +
                "Изменение: двухбайтовая ARM Thumb NOP отключает переключение режима OLED долгим удержанием кнопки. Дополнительная задержка GPIO не добавлена.\r\n" +
                "Изменены инструкция 0D70 -> C046 на 0xA212, CRC16/CMS 53AA -> 3433 на 0xA216 и четыре байта CRC32 тела в заголовке. Длина образа сохранена.\r\n" +
                "Проверены SHA256 исходного EXE и BIN, CRC блока, итоговый BIN и неизменность всех секций PE кроме .rsrc. Исходный EXE сохранён без изменений.\r\n\r\n" +
                "ОГРАНИЧЕНИЯ\r\n" +
                "Полный текущий образ устройства не считан; резервной копии прошивки и проверенного отката нет. PID и версия 1.08 сами по себе не доказывают совместимость образа.\r\n" +
                "Официальный обновлятор стирает [0x2B000,0x51000), а записывает [0x2B00A,0x4C00A). Он не создаёт резервную копию текущей прошивки.\r\n" +
                "Файл кандидата имеет окончание .exe.disabled. HUB не запускает обновлятор и не устанавливает прошивку.\r\n\r\n" +
                "Подробные адреса, контрольные суммы и результаты проверок: patch-report.json.\r\n", Utf8);
            return output;
        } catch (Exception error) {
            File.WriteAllText(Path.Combine(output, "PREPARATION-FAILED.txt"), "OFFLINE VALIDATION FAILED\r\n" + error.ToString(), Utf8); throw;
        }
    }
}

internal sealed class G9Hub : Form {
    readonly string root;
    readonly Label status = new Label { AutoSize = false, Height = 55, Dock = DockStyle.Top, ForeColor = Color.DarkGoldenrod };
    readonly TextBox log = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Font = new Font("Consolas", 9) };
    readonly List<Button> actions = new List<Button>();
    readonly object deviceGate = new object();
    G9OledWorker oledWorker;
    readonly G9ScreenEditor screenEditor;
    readonly G9MouseSettingsEditor settingsEditor;
    readonly G9ButtonEditor buttonEditor;
    volatile bool closing;
    string preparedDirectory;
    internal G9Hub(string path) {
        root = path; Text = "FURYCUBE G9 PRO HUB"; Width = 1120; Height = 760; MinimumSize = new Size(960,650); StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 10); Padding = new Padding(16);
        var description = new Label { Text = "Проверка устройства и локальная подготовка исправлений прошивки.", Height = 35, Dock = DockStyle.Top };
        status.Text = "Исправление OLED в мышь не установлено. Программа готова.";
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 130, AutoSize = false };
        AddButton(buttons, "Проверить мышь", async delegate { await Execute("Читаю настройки мыши…", delegate {
            RunProtocol("inspect"); RunProtocol("profile"); return "Настройки прочитаны. Вывод «0» проверяется нажатием кнопки в поле ввода.";
        }); });
        AddButton(buttons, "Назначить обычную 0", async delegate { await Execute("Записываю обычную «0»…", delegate {
            int profile=G9MappingSnapshot.Parse(RunProtocol("mapping --active")).profile;
            RunProtocol("zero --profile "+profile); return "Назначение «0» записано и проверено чтением. Исправление OLED ещё не установлено.";
        }); });
        AddButton(buttons, "Подготовить исправление", async delegate { await Execute("Готовлю и проверяю локальный патч…", delegate {
            preparedDirectory = G9FirmwarePatch.Prepare(root); Append("Артефакты: " + preparedDirectory);
            return "Патч подготовлен; в мышь не установлен. Физическая работа пока не проверена.";
        }); });
        AddButton(buttons, "Подготовить OLED + кнопку", async delegate { await Execute("Готовлю прошивку OL2 и проверяю все CRC…", delegate {
            preparedDirectory = G9FirmwareOledPatch.Prepare(root); Append("Артефакты OL2: " + preparedDirectory);
            return "Кандидат OL2 подготовлен локально. Прошивка не установлена; восстановление и физическая проверка ещё нужны.";
        }); });
        AddButton(buttons, "Подготовить OLED + DPI", async delegate { await Execute("Готовлю кандидат OL3 и проверяю все CRC…", delegate {
            preparedDirectory = G9FirmwareOl3Patch.Prepare(root); Append("Артефакты OL3: " + preparedDirectory);
            return "Кандидат OL3 подготовлен локально. В мышь не установлен; восстановление и физическая проверка ещё нужны.";
        }); });
        AddButton(buttons, "Открыть папку", delegate { Process.Start(new ProcessStartInfo { FileName = preparedDirectory ?? root, UseShellExecute = true }); });
        AddButton(buttons, "Сохранить назначения", async delegate { await Execute("Сохраняю таблицу кнопок…", delegate {
            int profile=G9MappingSnapshot.Parse(RunProtocol("mapping --active")).profile;
            RunProtocol("backup --profile "+profile); return "Копия назначений сохранена. Это копия таблицы кнопок, а не всей прошивки.";
        }); });
        AddButton(buttons, "Восстановить назначения…", async delegate {
            using(var dialog=new OpenFileDialog { Filter="Резервная копия назначений|keymap-*.bin|Двоичный файл|*.bin",InitialDirectory=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"FURYCUBE-G9-PRO","backups") }) {
                if(dialog.ShowDialog(this)!=DialogResult.OK)return;
                string selected=dialog.FileName;
                await Execute("Восстанавливаю таблицу кнопок…",delegate {
                    byte[] bytes=File.ReadAllBytes(selected);if(bytes.Length!=59 || bytes[2]<1 || bytes[2]>5)throw new InvalidDataException("Неверный формат копии назначений.");
                    RunProtocol("restore \""+selected+"\" --profile "+bytes[2]);return "Назначения восстановлены и проверены чтением.";
                });
            }
        });
        AddButton(buttons, "Данные мыши", delegate { Append(screenEditor.DescribeMouseTelemetry()); });
        AddButton(buttons,"Восстановить выбор профиля…",async delegate {
            using(var dialog=new OpenFileDialog {Filter="Снимок настроек (snapshot.json)|snapshot.json|JSON (*.json)|*.json",InitialDirectory=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"FURYCUBE-G9-PRO","backups")}) {
                if(dialog.ShowDialog(this)!=DialogResult.OK)return;
                string selected=dialog.FileName;
                await Execute("Восстанавливаю активный профиль и число профилей…",delegate {RunProtocol("profile-restore --pid 2148 --file \""+selected+"\"");return "Выбор и число профилей восстановлены. Прочитайте настройки мыши заново; содержимое профиля не переписывалось.";});
                buttonEditor.InvalidateProfile();settingsEditor.InvalidateProfile();
            }
        });
        AddButton(buttons,"Папка резервных копий",delegate {
            string backupFolder=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"FURYCUBE-G9-PRO","backups");Directory.CreateDirectory(backupFolder);
            Process.Start(new ProcessStartInfo {FileName=backupFolder,UseShellExecute=true});
        });
        var tabs = new TabControl { Dock = DockStyle.Fill };
        var assignments = new TabPage("Мышь");
        var mousePages=new TabControl {Dock=DockStyle.Fill};
        buttonEditor=new G9ButtonEditor(RunProtocol,Append);
        settingsEditor=new G9MouseSettingsEditor(RunProtocol,Append);
        settingsEditor.ProfileChanged+=delegate { buttonEditor.InvalidateProfile(); };
        buttonEditor.MappingChanged+=delegate { settingsEditor.InvalidateProfile(); };
        var bindingPage=new TabPage("Назначения кнопок");bindingPage.Controls.Add(buttonEditor);
        var settingsPage=new TabPage("DPI, сенсор и питание");settingsPage.Controls.Add(settingsEditor);
        mousePages.TabPages.Add(bindingPage);mousePages.TabPages.Add(settingsPage);
        var macrosPage=new TabPage("Макросы");macrosPage.Controls.Add(new Furycube.G9.Macros.G9MacroEditor(Append));mousePages.TabPages.Add(macrosPage);
        assignments.Controls.Add(mousePages);
        var mouse = new TabPage("Устройство и прошивка") { Padding=new Padding(8) };
        mouse.Controls.Add(log); mouse.Controls.Add(buttons); mouse.Controls.Add(status); mouse.Controls.Add(description);
        screenEditor=new G9ScreenEditor(Append,SendFrame,ReadRuntimeStatus);
        var screen = new TabPage("Экран и виджеты"); screen.Controls.Add(screenEditor);
        tabs.TabPages.Add(assignments); tabs.TabPages.Add(screen); tabs.TabPages.Add(mouse); tabs.SelectedIndex=0; Controls.Add(tabs);
        Append("USB-команды выполняются только кнопками проверки и назначения. Подготовка исправления полностью локальная.");
    }
    void AddButton(FlowLayoutPanel panel, string text, EventHandler click) {
        var button = new Button { Text = text, AutoSize = true, Height = 34, Padding = new Padding(8, 2, 8, 2), Margin = new Padding(0, 0, 10, 8) };
        button.Click += click; actions.Add(button); panel.Controls.Add(button);
    }
    void Append(string value) {
        if (IsDisposed) return;
        if (InvokeRequired) { try { BeginInvoke(new Action<string>(Append), value); } catch(InvalidOperationException) {} return; }
        log.AppendText(DateTime.Now.ToString("HH:mm:ss") + " " + value + Environment.NewLine);
    }
    async Task Execute(string working, Func<string> operation) {
        foreach (var button in actions) button.Enabled = false; status.Text = working; status.ForeColor = Color.DarkGoldenrod;
        try { status.Text = await Task.Run(operation); }
        catch (Exception error) { status.Text = "Операция остановлена: " + error.Message; status.ForeColor = Color.DarkRed; Append(error.Message); }
        finally { foreach (var button in actions) button.Enabled = true; }
    }
    string RunProtocol(string action) {
        lock (deviceGate) {
            if(closing)throw new ObjectDisposedException("HUB");
            return RunDeviceProcess(action);
        }
    }
    Task<string> SendFrame(byte[] pixels) {
        Furycube.G9.Oled.OledProtocol.ValidateFrame(pixels);
        return Task.Run(delegate {
            lock(deviceGate) {
                if(closing)throw new ObjectDisposedException("HUB");
                G9OledWorker current=oledWorker;
                try {
                    if(current==null) {
                        current=new G9OledWorker(root,Append);
                        System.Threading.Interlocked.Exchange(ref oledWorker,current);
                        if(closing)throw new ObjectDisposedException("HUB");
                    }
                    current.Send(pixels);
                    return "Четыре страницы переданы. Видимое изображение на экране нужно проверить отдельно.";
                } catch {
                    if(current!=null) {
                        System.Threading.Interlocked.CompareExchange(ref oledWorker,null,current);
                        current.Dispose();
                    }
                    throw;
                }
            }
        });
    }
    Task<Furycube.G9.Oled.OledRuntimeStatus> ReadRuntimeStatus() {
        return Task.Run(delegate {
            lock(deviceGate) {
                if(closing)throw new ObjectDisposedException("HUB");
                G9OledWorker current=oledWorker;
                try {
                    if(current==null) {
                        current=new G9OledWorker(root,Append);
                        System.Threading.Interlocked.Exchange(ref oledWorker,current);
                        if(closing)throw new ObjectDisposedException("HUB");
                    }
                    return current.ReadRuntimeStatus();
                } catch {
                    if(current!=null) {
                        System.Threading.Interlocked.CompareExchange(ref oledWorker,null,current);
                        current.Dispose();
                    }
                    throw;
                }
            }
        });
    }
    protected override void Dispose(bool disposing) {
        if(disposing) {
            closing=true;
            var current=System.Threading.Interlocked.Exchange(ref oledWorker,null);
            if(current!=null)current.Dispose();
        }
        base.Dispose(disposing);
    }
    string RunDeviceProcess(string action) {
        string executable = Path.Combine(root, "bin", "G9Device.exe");
        if (!File.Exists(executable)) throw new FileNotFoundException("Не найдена утилита G9Device.exe", executable);
        var start = new ProcessStartInfo { FileName = executable, Arguments = action, WorkingDirectory = root, UseShellExecute = false,
            CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8 };
        using (var process = new Process { StartInfo = start }) {
            var output = new StringBuilder(); var errors = new StringBuilder();
            process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e) { if (e.Data != null) { lock (output) output.AppendLine(e.Data); Append(e.Data); } };
            process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e) { if (e.Data != null) { lock (errors) errors.AppendLine(e.Data); Append(e.Data); } };
            process.Start(); process.BeginOutputReadLine(); process.BeginErrorReadLine();
            int timeout=action.StartsWith("settings-",StringComparison.Ordinal)?45000:30000;
            if (!process.WaitForExit(timeout)) { process.Kill(); throw new TimeoutException("Мышь не завершила операцию за " + (timeout/1000) + " секунд. Перед следующей записью перечитайте её состояние."); }
            process.WaitForExit();
            if (process.ExitCode == 4) throw new InvalidOperationException("Штатная прошивка не поддерживает передачу кадров OL2. Команды изображения не отправлены; сначала нужен проверенный путь восстановления и установка совместимого расширения.");
            if (process.ExitCode != 0) throw new InvalidOperationException(errors.Length > 0 ? errors.ToString().Trim() : "G9Device завершил операцию с ошибкой; подробности в журнале.");
            return output.ToString();
        }
    }
    [STAThread] static int Main(string[] args) {
        if (args.Length == 2 && (args[0] == "--prepare" || args[0] == "--prepare-oled" || args[0] == "--prepare-ol3")) {
            try { Console.WriteLine(args[0] == "--prepare-ol3" ? G9FirmwareOl3Patch.Prepare(args[1]) : args[0] == "--prepare-oled" ? G9FirmwareOledPatch.Prepare(args[1]) : G9FirmwarePatch.Prepare(args[1])); return 0; }
            catch (Exception error) { Console.Error.WriteLine(error.ToString()); return 1; }
        }
        if (args.Length != 0) return 2;
        string root = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".."));
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false); Application.Run(new G9Hub(root)); return 0;
    }
}
