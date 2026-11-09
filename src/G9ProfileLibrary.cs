using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Furycube.G9.Bindings;

// The library contains portable setting drafts only. No device handles,
// feature commands, process launches, or automatic application are used here.
internal sealed class G9ProfileLibraryEntry
{
    internal string Id, Name;
    internal bool IsReadable;
    public override string ToString() { return Name; }
}

internal sealed class G9ProfileStore
{
    readonly string root;
    const string Suffix = ".g9settings.json";
    const int MaxBytes = 65536;
    internal string DirectoryPath { get { return root; } }
    internal G9ProfileStore(string directory) { root = Path.GetFullPath(directory); }
    static string Name(string text)
    {
        if (text == null) throw new InvalidDataException("Укажите название профиля.");
        text = text.Trim();
        if (text.Length < 1 || text.Length > 100) throw new InvalidDataException("Название должно содержать от1 до100 символов.");
        foreach (char c in text) if (Char.IsControl(c)) throw new InvalidDataException("Название содержит управляющие символы.");
        return text;
    }
    static void ValidateId(string id)
    {
        Guid parsed;
        if (id == null || id.Length != 32 || !Guid.TryParseExact(id, "N", out parsed)) throw new InvalidDataException("Неверный идентификатор локального профиля.");
    }
    string FilePath(string id) { ValidateId(id); return Path.Combine(root, id + Suffix); }
    static G9SettingsPreset Copy(G9SettingsPreset preset, string name)
    {
        if (preset == null || preset.schemaVersion != 1 || preset.values == null || preset.values.Count < 1 || preset.values.Count > 64)
            throw new InvalidDataException("Файл не содержит поддерживаемый набор настроек G9 PRO.");
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in preset.values) {
            if (String.IsNullOrEmpty(pair.Key) || pair.Key.Length > 80 || pair.Value == null || pair.Value.Length > 100 ||
                pair.Key == "active-profile" || pair.Key == "max-profiles")
                throw new InvalidDataException("Переносимый набор содержит недопустимый параметр.");
            foreach (char c in pair.Key) if (!(c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z' || c >= '0' && c <= '9' || c == '.' || c == '-'))
                throw new InvalidDataException("Неверное имя параметра.");
            foreach (char c in pair.Value) if (Char.IsControl(c)) throw new InvalidDataException("Параметр содержит управляющие символы.");
            values.Add(pair.Key, pair.Value);
        }
        string keymap=null;
        if(!String.IsNullOrEmpty(preset.keymapHex)) {
            if(preset.keymapHex.Length!=118)throw new InvalidDataException("Неверный размер таблицы кнопок в наборе.");
            var bytes=new byte[59];
            for(int i=0;i<bytes.Length;i++) {
                byte value;if(!byte.TryParse(preset.keymapHex.Substring(i*2,2),NumberStyles.AllowHexSpecifier,CultureInfo.InvariantCulture,out value))throw new InvalidDataException("Неверная таблица кнопок в наборе.");
                bytes[i]=value;
            }
            try{bytes=BindingCatalog.RetargetKeymap(bytes,bytes[2]);}catch(ArgumentException error){throw new InvalidDataException("Неверный профиль таблицы кнопок.",error);}
            keymap=BitConverter.ToString(bytes).Replace("-","");
        }
        return new G9SettingsPreset { schemaVersion = 1, name = Name(name), values = values, keymapHex=keymap };
    }
    static byte[] Encode(G9SettingsPreset preset)
    {
        byte[] data = new UTF8Encoding(false).GetBytes(new JavaScriptSerializer().Serialize(preset));
        if (data.Length > MaxBytes) throw new InvalidDataException("Набор настроек слишком велик."); return data;
    }
    static G9SettingsPreset ReadFile(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length < 2 || info.Length > MaxBytes) throw new InvalidDataException("Неверный размер файла настроек.");
        // Read at most MaxBytes+1 even when another process changes the file
        // after FileInfo. A JSON archive never becomes an unbounded input.
        byte[] bytes = new byte[MaxBytes + 1]; int length = 0;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) {
            int read; while (length < bytes.Length && (read = stream.Read(bytes, length, bytes.Length - length)) > 0) length += read;
        }
        if (length > MaxBytes) throw new InvalidDataException("Файл настроек слишком велик.");
        string text = new UTF8Encoding(false, true).GetString(bytes, 0, length).TrimStart('\uFEFF');
        G9SettingsPreset preset;
        try { preset = new JavaScriptSerializer().Deserialize<G9SettingsPreset>(text); }
        catch (ArgumentException error) { throw new InvalidDataException("Повреждён файл настроек.", error); }
        return Copy(preset, preset == null ? null : preset.name);
    }
    void WriteAtomic(string path, G9SettingsPreset preset, bool replace)
    {
        Directory.CreateDirectory(root);
        string temporary = Path.Combine(root, ".pending-" + Guid.NewGuid().ToString("N") + ".tmp");
        try {
            byte[] bytes = Encode(preset);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
            if (replace) File.Replace(temporary, path, null); else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    internal G9ProfileLibraryEntry[] ListEntries()
    {
        var entries = new List<G9ProfileLibraryEntry>(); if (!Directory.Exists(root)) return entries.ToArray();
        foreach (string file in Directory.GetFiles(root, "*" + Suffix, SearchOption.TopDirectoryOnly)) {
            string name = Path.GetFileName(file), id = name.Substring(0, name.Length - Suffix.Length);
            try { ValidateId(id); } catch (InvalidDataException) { continue; }
            try { var preset = ReadFile(file); entries.Add(new G9ProfileLibraryEntry { Id = id, Name = preset.name, IsReadable = true }); }
            catch (Exception error) {
                if (!(error is IOException || error is InvalidDataException || error is UnauthorizedAccessException || error is ArgumentException || error is FormatException)) throw;
                entries.Add(new G9ProfileLibraryEntry { Id = id, Name = "[Повреждённый файл] " + id, IsReadable = false });
            }
        }
        entries.Sort(delegate(G9ProfileLibraryEntry a, G9ProfileLibraryEntry b) { return StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name); });
        return entries.ToArray();
    }
    internal string SaveNew(G9SettingsPreset preset, string name)
    {
        var copy = Copy(preset, name); string id = Guid.NewGuid().ToString("N"); WriteAtomic(FilePath(id), copy, false); return id;
    }
    internal G9SettingsPreset Read(string id) { return ReadFile(FilePath(id)); }
    internal void Rename(string id, string newName) { var preset = Read(id); WriteAtomic(FilePath(id), Copy(preset, newName), true); }
    internal string Archive(string id)
    {
        string source = FilePath(id), archiveDirectory = Path.Combine(root, ".deleted"); Directory.CreateDirectory(archiveDirectory);
        string destination = Path.Combine(archiveDirectory, DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffffffZ", CultureInfo.InvariantCulture) + "-" + id + "-" + Guid.NewGuid().ToString("N") + Suffix);
        File.Move(source, destination); return destination;
    }
    internal string ImportFile(string path, string name)
    { var preset = ReadFile(path); return SaveNew(preset, String.IsNullOrWhiteSpace(name) ? preset.name : name); }
    internal void ExportFile(string id, string path)
    {
        byte[] data = Encode(Read(id));
        using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None)) { stream.Write(data, 0, data.Length); stream.Flush(true); }
    }
}

internal sealed class G9ProfileLibrary : UserControl
{
    readonly Func<G9SettingsPreset> capture;
    readonly Action<G9SettingsPreset> open;
    readonly Action<string> logger;
    readonly G9ProfileStore store;
    readonly ListBox list = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };
    readonly TextBox name = new TextBox { Dock = DockStyle.Top, MaxLength = 100 };
    readonly Label status = new Label { Dock = DockStyle.Bottom, Height = 45, AutoEllipsis = true };
    readonly Button openButton, renameButton, archiveButton, exportButton;
    internal G9ProfileLibrary(Func<G9SettingsPreset> capture, Action<G9SettingsPreset> open, Action<string> log)
    {
        if (capture == null || open == null) throw new ArgumentNullException("capture/open");
        this.capture = capture; this.open = open; logger = log;
        store = new G9ProfileStore(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FURYCUBE-G9-PRO", "profiles"));
        Dock = DockStyle.Fill; Padding = new Padding(8);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, WrapContents = true };
        var save = Button("Сохранить правки как новый", Save);
        openButton = Button("Открыть как правки", Open);
        renameButton = Button("Переименовать", Rename);
        archiveButton = Button("Удалить в архив", Archive);
        var import = Button("Импорт…", Import);
        exportButton = Button("Экспорт…", Export);
        buttons.Controls.AddRange(new Control[] { save, openButton, renameButton, archiveButton, import, exportButton });
        var title = new Label { Dock = DockStyle.Top, Height = 44, Text = "Локальная библиотека настроек. Открытие создаёт правки для просмотра. Для записи в мышь нажмите «Применить изменения»." };
        var label = new Label { Dock = DockStyle.Top, Height = 24, Text = "Название набора настроек:" };
        Controls.Add(list); Controls.Add(buttons); Controls.Add(status); Controls.Add(name); Controls.Add(label); Controls.Add(title);
        list.SelectedIndexChanged += delegate { var entry = list.SelectedItem as G9ProfileLibraryEntry; if (entry != null && entry.IsReadable) name.Text = entry.Name; UpdateButtons(); };
        list.DoubleClick += delegate { if (openButton.Enabled) Run(Open); };
        status.Text = "Набор содержит параметры и назначения кнопок, если они сохранены. Прошивка и банк макросов в него не входят.";
        RefreshEntries(null);
    }
    Button Button(string text, Action action)
    { var button = new Button { Text = text, AutoSize = true }; button.Click += delegate { Run(action); }; return button; }
    void Run(Action action) { try { action(); } catch (Exception error) { status.Text = error.Message; if (logger != null) logger(error.Message); } }
    G9ProfileLibraryEntry Selected()
    { var entry = list.SelectedItem as G9ProfileLibraryEntry; if (entry == null) throw new InvalidOperationException("Выберите набор настроек."); return entry; }
    void RefreshEntries(string selectedId)
    {
        try {
            list.BeginUpdate(); list.Items.Clear(); foreach (var entry in store.ListEntries()) { list.Items.Add(entry); if (entry.Id == selectedId) list.SelectedItem = entry; }
        }
        catch (Exception error) { status.Text = error.Message; if (logger != null) logger(error.Message); }
        finally { list.EndUpdate(); UpdateButtons(); }
    }
    void UpdateButtons()
    {
        var entry = list.SelectedItem as G9ProfileLibraryEntry;
        archiveButton.Enabled = entry != null; openButton.Enabled = renameButton.Enabled = exportButton.Enabled = entry != null && entry.IsReadable;
    }
    void Save()
    {
        G9SettingsPreset preset = capture(); if (preset == null) throw new InvalidOperationException("Сначала прочитайте настройки мыши и подготовьте нужные правки.");
        string id = store.SaveNew(preset, name.Text); RefreshEntries(id); status.Text = "Новый набор сохранён локально. Запись в мышь не выполнялась.";
    }
    void Open()
    {
        var preset = store.Read(Selected().Id); open(preset);
        status.Text = "Набор открыт как правки. Проверьте их в разделах настроек перед применением.";
    }
    void Rename()
    { string id = Selected().Id; store.Rename(id, name.Text); RefreshEntries(id); status.Text = "Набор переименован."; }
    void Archive()
    {
        store.Archive(Selected().Id); RefreshEntries(null);
        status.Text = "Файл перемещён в profiles\\.deleted; содержимое сохранено для восстановления.";
    }
    void Import()
    {
        using (var dialog = new OpenFileDialog { Filter = "Настройки G9 PRO (*.g9settings.json)|*.g9settings.json|JSON (*.json)|*.json" }) {
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            string id = store.ImportFile(dialog.FileName, null); RefreshEntries(id);
            status.Text = "Файл добавлен в библиотеку. Чтобы просмотреть правки, нажмите «Открыть как правки».";
        }
    }
    void Export()
    {
        string id = Selected().Id;
        using (var dialog = new SaveFileDialog { Filter = "Настройки G9 PRO (*.g9settings.json)|*.g9settings.json", FileName = "G9-PRO.g9settings.json", OverwritePrompt = true }) {
            if (dialog.ShowDialog(this) != DialogResult.OK) return; store.ExportFile(id, dialog.FileName);
            status.Text = "Набор экспортирован без изменения мыши.";
        }
    }
}
