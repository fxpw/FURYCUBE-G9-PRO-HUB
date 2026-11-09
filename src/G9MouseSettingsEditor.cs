using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Furycube.G9.Settings;
using Furycube.G9.Bindings;

internal sealed class G9SettingsField {
    public string id { get; set; }
    public string name { get; set; }
    public string type { get; set; }
    public string value { get; set; }
    public string unit { get; set; }
    public int? min { get; set; }
    public int? max { get; set; }
    public int? step { get; set; }
    public G9SettingsOption[] options { get; set; }
    public bool canWrite { get; set; }
    public string unsupportedReason { get; set; }
}
internal sealed class G9SettingsOption {
    public string value { get; set; }
    public string name { get; set; }
    public override string ToString() { return name; }
}
internal sealed class G9SettingsSnapshot {
    public int schemaVersion { get; set; }
    public string source { get; set; }
    public string devicePath { get; set; }
    public int productId { get; set; }
    public int profile { get; set; }
    public int activeProfile { get; set; }
    public int maxProfiles { get; set; }
    public string readAtUtc { get; set; }
    public string fullBodyHex { get; set; }
    public Dictionary<string,string> rawReports { get; set; }
    public G9SettingsField[] fields { get; set; }
    public string archivePath { get; set; }
    public string changedField { get; set; }
    public string backupPath { get; set; }
    public bool verified { get; set; }
    internal MouseSettingsProfile Model;
    internal byte[] Keys;

    internal static G9SettingsSnapshot Parse(string output, bool applied) {
        if (String.IsNullOrEmpty(output) || output.Length > 1048576) throw new InvalidDataException("Нет ответа с настройками.");
        string marker = applied ? "SETTINGS_APPLIED_JSON " : "SETTINGS_JSON ";
        string json = null;
        foreach (string line in output.Split(new[] {'\r','\n'}, StringSplitOptions.RemoveEmptyEntries)) {
            if (!line.StartsWith(marker, StringComparison.Ordinal)) continue;
            if (json != null) throw new InvalidDataException("Получено несколько снимков настроек.");
            json = line.Substring(marker.Length);
        }
        if (json == null || json.Length > 65536) throw new InvalidDataException("Не получен полный снимок настроек.");
        G9SettingsSnapshot result;
        try { result = new JavaScriptSerializer().Deserialize<G9SettingsSnapshot>(json); }
        catch (ArgumentException error) { throw new InvalidDataException("Повреждено описание настроек.", error); }
        if (result == null || result.schemaVersion != 1 || result.source != "FirmwareLogicalProfile" ||
            result.profile < 1 || result.profile > 5 || result.activeProfile != result.profile ||
            result.maxProfiles < result.activeProfile || result.maxProfiles > 5 ||
            result.rawReports == null || result.fields == null || result.fields.Length > 40 ||
            result.productId != 0x2148 && result.productId != 0xFA60)
            throw new InvalidDataException("Неизвестный формат или профиль настроек.");
        DateTime stamp;
        if (!DateTime.TryParse(result.readAtUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out stamp))
            throw new InvalidDataException("Отсутствует время чтения настроек.");
        string raw;
        if (!result.rawReports.TryGetValue("allData", out raw)) throw new InvalidDataException("Отсутствует исходный ответ мыши.");
        byte[] packet = Hex(raw, 120);
        result.Model = MouseSettingsCodec.DecodeAllData(packet, result.profile);
        byte[] body = Hex(result.fullBodyHex, 117);
        for (int i = 0; i < body.Length; i++) if (body[i] != packet[i + 3]) throw new InvalidDataException("Тело профиля не совпадает с исходным ответом.");
        string rawKeys;
        if(!result.rawReports.TryGetValue("keys",out rawKeys))throw new InvalidDataException("Отсутствует полная таблица кнопок.");
        result.Keys=Hex(rawKeys,59);
        if(result.Keys[0]!=8 || result.Keys[1]!=59 || result.Keys[2]!=result.profile)throw new InvalidDataException("Неверный профиль таблицы кнопок.");
        int keySum=0;for(int i=0;i<54;i++){keySum+=result.Keys[3+i];if(result.Keys[3+i]!=body[62+i])throw new InvalidDataException("Таблица кнопок расходится с профилем.");}
        if(keySum!=(result.Keys[57]<<8|result.Keys[58]) || result.Keys[58]!=body[116])throw new InvalidDataException("Неверная контрольная сумма кнопок.");
        var expected = MouseSettingsCodec.GetFields(result.Model);
        var actual = new Dictionary<string,G9SettingsField>(StringComparer.Ordinal);
        foreach (var field in result.fields) {
            if (field == null || String.IsNullOrEmpty(field.id) || field.id.Length > 80 || actual.ContainsKey(field.id) ||
                field.canWrite && field.value == null || field.value != null && field.value.Length > 100 || field.options != null && field.options.Length > 32)
                throw new InvalidDataException("Повреждён или повторяется параметр настроек.");
            actual.Add(field.id, field);
        }
        foreach (var field in expected) {
            G9SettingsField declared;
            int? minimum=field.Min==0 && field.Max==0?(int?)null:field.Min;
            int? maximum=field.Min==0 && field.Max==0?(int?)null:field.Max;
            int? increment=field.Step==0?(int?)null:field.Step;
            if (!actual.TryGetValue(field.Id, out declared) || declared.value != field.Value || declared.canWrite != field.CanWrite ||
                declared.type != field.Type || declared.min != minimum || declared.max != maximum || declared.step != increment)
                throw new InvalidDataException("Описание параметра не совпадает с прочитанными байтами: " + field.Id);
            // Own metadata, not arbitrary labels/options from a process response, drives the editor.
            declared.name = field.Name; declared.unit = field.Unit; declared.unsupportedReason = field.UnsupportedReason;
            var choices = new List<G9SettingsOption>();
            if (field.Options != null) foreach (var option in field.Options) choices.Add(new G9SettingsOption { value=option.Value, name=option.Name });
            declared.options = choices.ToArray();
            actual.Remove(field.Id);
        }
        foreach (var global in actual) {
            int value = global.Key == "active-profile" ? result.activeProfile : global.Key == "max-profiles" ? result.maxProfiles : -1;
            if (value < 0 || global.Value.value != value.ToString(CultureInfo.InvariantCulture)) throw new InvalidDataException("Неизвестный глобальный параметр.");
            global.Value.name=global.Key=="active-profile"?"Активный профиль":"Количество профилей в цикле";
            global.Value.type="integer"; global.Value.min=1; global.Value.max=5; global.Value.step=1; global.Value.unit="";
            var choices=new List<G9SettingsOption>();
            for(int i=1;i<=(global.Key=="active-profile"?result.maxProfiles:5);i++)choices.Add(new G9SettingsOption {value=i.ToString(CultureInfo.InvariantCulture),name=global.Key=="active-profile"?"Профиль "+i:i.ToString(CultureInfo.InvariantCulture)});
            global.Value.options=choices.ToArray(); global.Value.canWrite=true;
        }
        if (applied && (!result.verified || String.IsNullOrEmpty(result.changedField) || String.IsNullOrEmpty(result.backupPath)))
            throw new InvalidDataException("Запись не подтверждена чтением или не сохранена копия.");
        return result;
    }
    internal static byte[] Hex(string value, int length) {
        if (value == null || value.Length != length * 2) throw new InvalidDataException("Неверный размер исходного пакета.");
        var result = new byte[length];
        for (int i = 0; i < length; i++) {
            char a = value[i*2], b = value[i*2+1];
            if (!IsHex(a) || !IsHex(b)) throw new InvalidDataException("Неверный формат исходного пакета.");
            result[i] = byte.Parse(value.Substring(i*2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }
        return result;
    }
    static bool IsHex(char c) { return c >= '0' && c <= '9' || c >= 'a' && c <= 'f' || c >= 'A' && c <= 'F'; }
}

internal sealed class G9SettingsPreset {
    public int schemaVersion { get; set; }
    public string name { get; set; }
    public Dictionary<string,string> values { get; set; }
    public string keymapHex { get; set; }
}

internal sealed class G9MouseSettingsEditor : UserControl {
    readonly Func<string,string> device;
    readonly Action<string> logger;
    readonly Button read = new Button { Text="Прочитать из мыши", AutoSize=true };
    readonly Button apply = new Button { Text="Применить изменения", AutoSize=true };
    readonly Button revert = new Button { Text="Отменить правки", AutoSize=true };
    readonly Button export = new Button { Text="Экспорт настроек…", AutoSize=true };
    readonly Button import = new Button { Text="Импорт настроек…", AutoSize=true };
    readonly Button windowsMouse = new Button { Text="Свойства мыши Windows", AutoSize=true };
    readonly Label status = new Label { Dock=DockStyle.Bottom, Height=58, ForeColor=Color.DarkGoldenrod };
    readonly Label identity = new Label { Dock=DockStyle.Top, Height=52, Text="Параметры пока не прочитаны. Программа не применяет настройки при запуске." };
    readonly CheckBox includeButtons=new CheckBox {Dock=DockStyle.Top,Height=30,Text="Перенести назначения кнопок из открытого набора",Visible=false};
    readonly TabControl pages = new TabControl { Dock=DockStyle.Fill };
    readonly List<SettingRow> rows = new List<SettingRow>();
    readonly ToolTip tips = new ToolTip { AutoPopDelay=16000 };
    G9SettingsSnapshot snapshot;
    byte[] pendingKeys;
    bool busy;
    internal event EventHandler ProfileChanged;

    internal G9MouseSettingsEditor(Func<string,string> run, Action<string> log) {
        device=run; logger=log; Dock=DockStyle.Fill; Padding=new Padding(12);
        var actions = new FlowLayoutPanel { Dock=DockStyle.Top, Height=46, WrapContents=false, AutoScroll=true };
        actions.Controls.AddRange(new Control[] { read, apply, revert, import, export, windowsMouse });
        Controls.Add(pages);Controls.Add(includeButtons); Controls.Add(actions); Controls.Add(identity); Controls.Add(status);
        read.Click += async delegate { await ReadSettings(); };
        apply.Click += async delegate { await ApplyChanges(); };
        revert.Click += delegate { ShowSnapshot(); };
        export.Click += delegate { ExportPreset(); };
        import.Click += delegate { ImportPreset(); };
        includeButtons.CheckedChanged+=delegate { UpdateButtons(); };
        windowsMouse.Click+=delegate {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo {FileName=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"control.exe"),Arguments="main.cpl",UseShellExecute=true}); }
            catch(Exception error) { Report(error); }
        };
        tips.SetToolTip(apply, "Сохраняются только изменённые параметры. Перед каждой записью — копия текущих настроек, после — проверка чтением.");
        status.Text="Прочитайте настройки, затем измените нужные значения. Запись выполняется только по кнопке «Применить изменения».";
        ShowSnapshot();
    }
    internal async Task ReadSettings() {
        if (busy) return; SetBusy(true); status.Text="Читаю текущий профиль, DPI, сенсор и питание…";
        try {
            snapshot=G9SettingsSnapshot.Parse(await Task.Run(delegate { return device("settings-read"); }), false);
            if(IsDisposed || Disposing)return;
            ShowSnapshot(); status.ForeColor=Color.DarkGreen;
            status.Text="Настройки прочитаны. Меняйте значения и нажмите «Применить изменения».";
            if(ProfileChanged!=null)ProfileChanged(this,EventArgs.Empty);
        } catch (Exception error) { if(!IsDisposed && !Disposing)Invalidate(error); }
        finally { if(!IsDisposed && !Disposing)SetBusy(false); }
    }
    void ShowSnapshot() {
        if(IsDisposed || Disposing)return;
        pages.SuspendLayout();
        try {
            while (pages.TabPages.Count > 0) { var old=pages.TabPages[0]; pages.TabPages.RemoveAt(0); old.Dispose(); }
            rows.Clear();
            pendingKeys=null;includeButtons.Checked=false;includeButtons.Visible=false;
            if (snapshot == null) { identity.Text="Параметры пока не прочитаны. Программа не применяет настройки при запуске."; return; }
            identity.Text="G9 PRO · профиль " + snapshot.activeProfile + " из " + snapshot.maxProfiles +
                " · " + (snapshot.productId==0x2148?"USB-кабель":"Приёмник 2.4G") +
                Environment.NewLine + "Прочитано: " + DateTime.Parse(snapshot.readAtUtc,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind).ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss") + " · резервные копии сохраняются локально";
            string[] categories={"DPI", "Опрос и сенсор", "Питание", "Профили", "Доступность"};
            foreach (string category in categories) {
                var tab = new TabPage(category) { Padding=new Padding(8), AutoScroll=true };
                var grid = new TableLayoutPanel { Dock=DockStyle.Top, AutoSize=true, ColumnCount=3, GrowStyle=TableLayoutPanelGrowStyle.AddRows };
                grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 240));
                grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 145));
                grid.Controls.Add(new Label {Text="Параметр",AutoSize=true,Font=new Font(Font,FontStyle.Bold)},0,0);
                grid.Controls.Add(new Label {Text="Новое значение",AutoSize=true,Font=new Font(Font,FontStyle.Bold)},1,0);
                grid.Controls.Add(new Label {Text="В мыши",AutoSize=true,Font=new Font(Font,FontStyle.Bold)},2,0);
                int at=1;
                foreach (var field in snapshot.fields) {
                    if (Category(field)!=category) continue;
                    if(category=="DPI" && field.id.StartsWith("dpi.color",StringComparison.Ordinal))continue;
                    var row=new SettingRow(field, tips, UpdateButtons); rows.Add(row);
                    Control editor=row.Editor;
                    if(category=="DPI" && field.id.StartsWith("dpi.value",StringComparison.Ordinal)) {
                        string index=field.id.Substring(9);
                        var colorField=snapshot.fields.FirstOrDefault(delegate(G9SettingsField f) {return f.id=="dpi.color"+index;});
                        if(colorField!=null && colorField.canWrite) {
                            var colorRow=new SettingRow(colorField,tips,UpdateButtons);rows.Add(colorRow);
                            var pair=new TableLayoutPanel {ColumnCount=2,AutoSize=true,Dock=DockStyle.Top,Margin=Padding.Empty};
                            pair.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,65));pair.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,35));
                            pair.Controls.Add(row.Editor,0,0);pair.Controls.Add(colorRow.Editor,1,0);editor=pair;
                            row.Stored.Text=field.value+" DPI"+Environment.NewLine+"#"+colorField.value;
                            colorRow.Caption.Visible=colorRow.Stored.Visible=false;
                            pair.Controls.Add(colorRow.Caption,0,1);pair.Controls.Add(colorRow.Stored,1,1);
                        }
                        if(index=="7" || index=="8")row.Caption.Text+=" (резерв)";
                    }
                    grid.Controls.Add(row.Caption,0,at); grid.Controls.Add(editor,1,at); grid.Controls.Add(row.Stored,2,at); at++;
                }
                if (category=="Профили") {
                    var note=new Label {AutoSize=true,MaximumSize=new Size(760,0),Text="Профили 1–5 хранятся в мыши. Переключение выполняется явно. Экспорт/импорт сохраняет параметры и назначения кнопок. Импорт сначала открывает правки для просмотра; макросы сохраняются отдельной библиотекой."};
                    grid.Controls.Add(note,0,at); grid.SetColumnSpan(note,3);
                }
                if (at>1 || category=="Профили") { tab.Controls.Add(grid); pages.TabPages.Add(tab); } else tab.Dispose();
            }
            var libraryPage=new TabPage("Библиотека настроек");
            libraryPage.Controls.Add(new G9ProfileLibrary(CapturePreset,OpenPreset,logger));pages.TabPages.Add(libraryPage);
        } finally { pages.ResumeLayout(); UpdateButtons(); }
    }
    static string Category(G9SettingsField field) {
        if (!field.canWrite) return "Доступность";
        if (field.id.StartsWith("dpi.",StringComparison.Ordinal)) return "DPI";
        if (field.id.StartsWith("sleep.",StringComparison.Ordinal)) return "Питание";
        if (field.id=="active-profile" || field.id=="max-profiles") return "Профили";
        return "Опрос и сенсор";
    }
    internal Dictionary<string,string> DraftChanges() {
        var result=new Dictionary<string,string>(StringComparer.Ordinal);
        foreach (var row in rows) if (row.Edited && row.Field.canWrite && row.Draft != row.Field.value) result.Add(row.Field.id,row.Draft);
        return result;
    }
    internal static List<KeyValuePair<string,string>> PrepareEdits(G9SettingsSnapshot before, Dictionary<string,string> changes) {
        // Values first, then a temporary union mask, then stage, then final mask.
        // Disabling the old active stage therefore never produces an invalid intermediate profile.
        var edits=new List<KeyValuePair<string,string>>();
        var model=before.Model;
        foreach(var pair in changes) {
            var field=before.fields.FirstOrDefault(delegate(G9SettingsField f) { return f.id==pair.Key; });
            if(field==null || !field.canWrite || pair.Value==null)throw new InvalidOperationException("Недоступный параметр: "+pair.Key);
        }
        foreach (var pair in changes.OrderBy(delegate(KeyValuePair<string,string> p) { return p.Key; },StringComparer.Ordinal)) {
            if (pair.Key.StartsWith("dpi.value",StringComparison.Ordinal) || pair.Key.StartsWith("dpi.color",StringComparison.Ordinal)) edits.Add(pair);
        }
        string mask;
        if (changes.TryGetValue("dpi.enabledMask",out mask)) {
            string original=before.fields.First(delegate(G9SettingsField f) { return f.id=="dpi.enabledMask"; }).value;
            int union=int.Parse(original,CultureInfo.InvariantCulture)|int.Parse(mask,CultureInfo.InvariantCulture);
            if (union.ToString(CultureInfo.InvariantCulture)!=original) edits.Add(new KeyValuePair<string,string>("dpi.enabledMask",union.ToString(CultureInfo.InvariantCulture)));
        }
        string stage;
        if (changes.TryGetValue("dpi.stage",out stage)) edits.Add(new KeyValuePair<string,string>("dpi.stage",stage));
        if (mask!=null) edits.Add(new KeyValuePair<string,string>("dpi.enabledMask",mask));
        foreach (var pair in changes.OrderBy(delegate(KeyValuePair<string,string> p) { return p.Key; },StringComparer.Ordinal)) {
            if (!pair.Key.StartsWith("dpi.",StringComparison.Ordinal) && pair.Key!="active-profile" && pair.Key!="max-profiles") edits.Add(pair);
        }
        // Validate every draft locally before the first USB write.
        foreach (var pair in edits) model=MouseSettingsCodec.BuildEdit(model,pair.Key,pair.Value).Updated;
        if(model.EnabledStageMask.HasValue && model.SelectedStage.HasValue && (model.EnabledStageMask.Value&(1<<(model.SelectedStage.Value-1)))==0)
            throw new InvalidOperationException("Активный уровень DPI должен быть отмечен в списке включённых уровней.");
        if (changes.ContainsKey("active-profile") && changes.Count!=1 || changes.ContainsKey("max-profiles") && changes.Count!=1)
            throw new InvalidOperationException("Примените смену или количество профилей отдельно от других правок.");
        foreach (string global in new[] {"active-profile","max-profiles"}) {
            string value;
            if (!changes.TryGetValue(global,out value)) continue;
            int number;
            if (!int.TryParse(value,NumberStyles.None,CultureInfo.InvariantCulture,out number) || number<1 || number>5 ||
                global=="active-profile" && number>before.maxProfiles || global=="max-profiles" && number<before.activeProfile)
                throw new InvalidOperationException("Неверный номер или количество профилей. Сначала выберите профиль в допустимом диапазоне.");
            edits.Add(new KeyValuePair<string,string>(global,value));
        }
        return edits;
    }
    async Task ApplyChanges() {
        if (busy || snapshot==null) return;
        var changes=DraftChanges();byte[] keysToApply=includeButtons.Checked?pendingKeys:null;
        if (changes.Count==0 && keysToApply==null) return;
        List<KeyValuePair<string,string>> edits;
        try {
            edits=PrepareEdits(snapshot,changes);
            if(keysToApply!=null && (changes.ContainsKey("active-profile") || changes.ContainsKey("max-profiles")))throw new InvalidOperationException("Примените переключение профилей отдельно от переноса назначений.");
        }
        catch (Exception error) { Report(error); return; }
        int completed=0, oldProfile=snapshot.profile;
        SetBusy(true);
        try {
            foreach (var edit in edits) {
                if(IsDisposed || Disposing)return;
                int profile=snapshot.profile;
                status.Text="Сохраняю " + Caption(edit.Key) + " (" + (completed+1) + "/" + edits.Count + ")…";
                string command="settings-set --profile " + profile.ToString(CultureInfo.InvariantCulture) + " --field " + edit.Key + " --value " + edit.Value;
                var readback=G9SettingsSnapshot.Parse(await Task.Run(delegate { return device(command); }),true);
                if(IsDisposed || Disposing)return;
                if (readback.changedField!=edit.Key) throw new InvalidDataException("Ответ относится к другому изменению.");
                var field=readback.fields.FirstOrDefault(delegate(G9SettingsField f) { return f.id==edit.Key; });
                if (field==null || field.value!=edit.Value) throw new InvalidDataException("Прочитано другое значение параметра.");
                snapshot=readback; completed++;
            }
            if(keysToApply!=null) {
                if(IsDisposed || Disposing)return;
                status.Text="Переношу назначения из набора и проверяю чтением…";
                int profile=snapshot.profile;
                string directory=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"FURYCUBE-G9-PRO","backups","profile-drafts");Directory.CreateDirectory(directory);
                string path=Path.Combine(directory,Guid.NewGuid().ToString("N")+".bin");
                File.WriteAllBytes(path,BindingCatalog.RetargetKeymap(keysToApply,profile));
                await Task.Run(delegate { device("restore \""+path+"\" --pid 2148 --profile "+profile.ToString(CultureInfo.InvariantCulture)); });
                if(IsDisposed || Disposing)return;
                var readback=G9SettingsSnapshot.Parse(await Task.Run(delegate {return device("settings-read --pid 2148 --profile "+profile.ToString(CultureInfo.InvariantCulture));}),false);
                if(IsDisposed || Disposing)return;
                if(!readback.Keys.SequenceEqual(keysToApply))throw new InvalidDataException("Назначения после переноса расходятся с набором.");
                snapshot=readback;completed++;
                if(ProfileChanged!=null)ProfileChanged(this,EventArgs.Empty);
            }
            ShowSnapshot(); status.ForeColor=Color.DarkGreen;
            status.Text="Изменения сохранены и проверены чтением. Копии исходных настроек — в локальной папке backups.";
            if (oldProfile!=snapshot.profile && ProfileChanged!=null) ProfileChanged(this,EventArgs.Empty);
        } catch (Exception error) {
            if(IsDisposed || Disposing)return;
            snapshot=null; ShowSnapshot();
            if (ProfileChanged!=null) ProfileChanged(this,EventArgs.Empty);
            Report(new InvalidOperationException("Подтверждено изменений: " + completed + ". " + error.Message + " Перечитайте настройки перед следующей записью."));
        } finally { if(!IsDisposed && !Disposing)SetBusy(false); }
    }
    void ExportPreset() {
        if (snapshot==null || busy) return;
        using (var dialog=new SaveFileDialog {Filter="Настройки G9 PRO (*.g9settings.json)|*.g9settings.json", FileName="G9-PRO-profile-"+snapshot.profile+".g9settings.json"}) {
            if (dialog.ShowDialog(this)!=DialogResult.OK) return;
            try {
                var preset=CapturePreset();preset.name=Path.GetFileNameWithoutExtension(dialog.FileName);
                File.WriteAllText(dialog.FileName,new JavaScriptSerializer().Serialize(preset),new UTF8Encoding(false));
                status.ForeColor=Color.DarkGreen; status.Text="Параметры экспортированы. Файл не является копией прошивки или банка макросов.";
            } catch (Exception error) { Report(error); }
        }
    }
    void ImportPreset() {
        if (snapshot==null || busy) return;
        using (var dialog=new OpenFileDialog {Filter="Настройки G9 PRO (*.g9settings.json)|*.g9settings.json|JSON (*.json)|*.json"}) {
            if (dialog.ShowDialog(this)!=DialogResult.OK) return;
            try {
                if (new FileInfo(dialog.FileName).Length>65536) throw new InvalidDataException("Файл настроек слишком велик.");
                var preset=new JavaScriptSerializer().Deserialize<G9SettingsPreset>(File.ReadAllText(dialog.FileName,Encoding.UTF8));
                OpenPreset(preset);
            } catch (Exception error) { Report(error); }
        }
    }
    G9SettingsPreset CapturePreset() {
        if(snapshot==null || busy)throw new InvalidOperationException("Сначала прочитайте настройки мыши.");
        PrepareEdits(snapshot,DraftChanges());
        var values=new Dictionary<string,string>(StringComparer.Ordinal);
        foreach(var row in rows)if(row.Field.canWrite && row.Field.id!="active-profile" && row.Field.id!="max-profiles")values.Add(row.Field.id,row.Draft);
        byte[] keys=includeButtons.Checked && pendingKeys!=null?pendingKeys:snapshot.Keys;
        return new G9SettingsPreset {schemaVersion=1,name="Профиль "+snapshot.profile,values=values,keymapHex=BitConverter.ToString(keys).Replace("-","")};
    }
    void OpenPreset(G9SettingsPreset preset) {
        if(snapshot==null || busy)throw new InvalidOperationException("Сначала прочитайте настройки мыши.");
        if(preset==null || preset.schemaVersion!=1 || preset.values==null || preset.values.Count>32)throw new InvalidDataException("Неверный формат настроек.");
        var known=new Dictionary<string,SettingRow>(StringComparer.Ordinal);foreach(var row in rows)known.Add(row.Field.id,row);
        foreach(var pair in preset.values) {
            SettingRow row;
            if(pair.Key=="active-profile" || pair.Key=="max-profiles" || !known.TryGetValue(pair.Key,out row) || !row.Field.canWrite)
                throw new InvalidDataException("Набор содержит недоступный параметр: "+pair.Key);
        }
        PrepareEdits(snapshot,preset.values.Where(delegate(KeyValuePair<string,string> p) {return known[p.Key].Field.value!=p.Value;}).ToDictionary(p=>p.Key,p=>p.Value,StringComparer.Ordinal));
        byte[] importedKeys=String.IsNullOrEmpty(preset.keymapHex)?null:BindingCatalog.RetargetKeymap(G9SettingsSnapshot.Hex(preset.keymapHex,59),snapshot.profile);
        foreach(var row in rows)if(row.Edited)row.Reset();
        foreach(var pair in preset.values)if(known[pair.Key].Field.value!=pair.Value)known[pair.Key].SetDraft(pair.Value);
        pendingKeys=importedKeys!=null && !importedKeys.SequenceEqual(snapshot.Keys)?importedKeys:null;
        includeButtons.Visible=pendingKeys!=null;includeButtons.Checked=pendingKeys!=null;
        UpdateButtons();status.ForeColor=Color.DarkGoldenrod;
        status.Text="Набор открыт как правки. Проверьте значения и нажмите «Применить изменения», чтобы записать их в мышь.";
    }
    void Invalidate(Exception error) { snapshot=null; ShowSnapshot(); Report(error); }
    internal void InvalidateProfile() {snapshot=null;ShowSnapshot();status.ForeColor=Color.DarkGoldenrod;status.Text="Назначения или профиль изменены. Прочитайте текущие настройки заново.";}
    void Report(Exception error) { status.ForeColor=Color.DarkRed; status.Text=error.Message; if(logger!=null)logger(error.Message); }
    void SetBusy(bool value) { busy=value; pages.Enabled=!value; UpdateButtons(); }
    void UpdateButtons() {
        if(IsDisposed || Disposing)return;
        includeButtons.Enabled=!busy;
        read.Enabled=!busy; apply.Enabled=!busy && snapshot!=null && (DraftChanges().Count>0 || includeButtons.Checked && pendingKeys!=null);
        revert.Enabled=export.Enabled=import.Enabled=!busy && snapshot!=null;
    }
    static string Caption(string id) {
        if (id.StartsWith("dpi.value",StringComparison.Ordinal)) return "Уровень DPI " + id.Substring(9);
        if (id.StartsWith("dpi.color",StringComparison.Ordinal)) return "Цвет уровня DPI " + id.Substring(9);
        switch(id) {
            case "dpi.stage": return "Активный уровень DPI";
            case "dpi.enabledMask": return "Уровни в цикле DPI";
            case "polling.hz": return "Частота опроса";
            case "debounce.ms": return "Задержка фильтра кликов";
            case "sensor.ripple": return "Сглаживание сенсора";
            case "sensor.angleSnap": return "Выравнивание движения";
            case "sleep.level1Seconds": return "Сон первого уровня";
            case "sleep.idleMinutes": return "Режим ожидания";
            case "active-profile": return "Активный профиль";
            case "max-profiles": return "Количество профилей в цикле";
            default: return id;
        }
    }
    static string Help(string id) {
        switch(id) {
            case "dpi.enabledMask": return "Галочки определяют уровни, между которыми переключается кнопка DPI. Активный уровень должен быть включён. Штатный HUB показывает первые шесть уровней.";
            case "dpi.stage": return "Выбор текущего уровня из включённых уровней DPI.";
            case "debounce.ms": return "Фильтр повторных срабатываний механических кнопок. Шаг — 2 мс. Эта настройка не исправляет отдельную логику удержания OLED-кнопки.";
            case "sensor.ripple": return "Ripple Control: сглаживание колебаний траектории сенсора.";
            case "sensor.angleSnap": return "Angle Snap: коррекция траектории к прямым линиям.";
            case "sleep.level1Seconds": return "Время до сна первого уровня: 0,5–30 минут, шаг 0,5 минуты. Штатная прошивка может очистить OLED при выключении.";
            case "sleep.idleMinutes": return "Таймер режима ожидания: 1–60 минут. Это отдельное поле, не время фильтра кнопок.";
            case "polling.hz": return "125, 250, 500 или 1000 Гц. Остальные биты поля частоты сохраняются без изменений.";
            case "active-profile": return "Перед переключением отпустите все кнопки мыши. Применяйте отдельно от других правок, затем перечитайте назначения.";
            case "max-profiles": return "Число аппаратных профилей для циклического переключения, от 1 до 5. Не удаляет локальные файлы настроек.";
            default: return "Настраиваемое значение в памяти мыши. Изменения применяются только по кнопке.";
        }
    }
    sealed class SettingRow {
        internal readonly G9SettingsField Field;
        internal readonly Label Caption, Stored;
        internal readonly Control Editor;
        internal bool Edited;
        readonly Action changed;
        readonly NumericUpDown number;
        readonly TrackBar slider;
        readonly ComboBox combo;
        readonly CheckBox toggle;
        readonly CheckBox[] bits;
        readonly TextBox colorText;
        readonly Button colorPick;
        bool setting;
        internal SettingRow(G9SettingsField field,ToolTip tips,Action onChanged) {
            Field=field; changed=onChanged;
            string title=G9MouseSettingsEditor.Caption(field.id); if(title==field.id)title=field.name??field.id;
            Caption=new Label {Text=title,AutoSize=true,MaximumSize=new Size(235,0),Margin=new Padding(0,13,8,10)};
            string storedValue=field.value+(String.IsNullOrEmpty(field.unit)?"":" "+field.unit);
            if(field.options!=null)foreach(var option in field.options)if(option.value==field.value){storedValue=option.name;break;}
            if(field.id=="dpi.enabledMask" && field.value!=null) {
                int mask=int.Parse(field.value,CultureInfo.InvariantCulture);var levels=new List<string>();for(int i=0;i<8;i++)if((mask&(1<<i))!=0)levels.Add((i+1).ToString(CultureInfo.InvariantCulture));storedValue=String.Join(", ",levels);
            }
            if(field.id=="sleep.level1Seconds" && field.value!=null)storedValue=(decimal.Parse(field.value,CultureInfo.InvariantCulture)/60m).ToString("0.#",CultureInfo.CurrentCulture)+" мин";
            Stored=new Label {Text=storedValue,AutoSize=true,MaximumSize=new Size(140,0),Margin=new Padding(12,13,0,10),ForeColor=Color.DimGray};
            tips.SetToolTip(Caption,Help(field.id));
            if (!field.canWrite) {
                Editor=new Label {Text=field.unsupportedReason??"Настройка недоступна на G9 PRO",AutoSize=true,MaximumSize=new Size(580,0),Margin=new Padding(0,13,0,10)};
                return;
            }
            if (field.type=="color") {
                var flow=new FlowLayoutPanel {AutoSize=true,Dock=DockStyle.Top,WrapContents=false,Margin=new Padding(0,6,0,8)};
                colorText=new TextBox {Text=field.value,MaxLength=6,Width=100,CharacterCasing=CharacterCasing.Upper};
                colorPick=new Button {Text="Цвет…",AutoSize=true,UseVisualStyleBackColor=false};
                Action updateSwatch=delegate {int rgb;if(colorText.Text.Length==6 && int.TryParse(colorText.Text,NumberStyles.HexNumber,CultureInfo.InvariantCulture,out rgb)){colorPick.BackColor=Color.FromArgb((rgb>>16)&255,(rgb>>8)&255,rgb&255);colorPick.ForeColor=colorPick.BackColor.GetBrightness()<0.48f?Color.White:Color.Black;}};
                colorText.TextChanged+=delegate {updateSwatch();Touch();};updateSwatch();
                colorPick.Click+=delegate {
                    using(var dialog=new ColorDialog {FullOpen=true}) {
                        int rgb;if(int.TryParse(colorText.Text,NumberStyles.HexNumber,CultureInfo.InvariantCulture,out rgb))dialog.Color=Color.FromArgb((rgb>>16)&255,(rgb>>8)&255,rgb&255);
                        if(dialog.ShowDialog(colorPick.FindForm())==DialogResult.OK)colorText.Text=(dialog.Color.ToArgb()&0xFFFFFF).ToString("X6",CultureInfo.InvariantCulture);
                    }
                };
                flow.Controls.Add(colorText);flow.Controls.Add(colorPick);Editor=flow;
            } else if (field.id=="dpi.enabledMask") {
                var flow=new FlowLayoutPanel {AutoSize=true,Dock=DockStyle.Top,WrapContents=true}; bits=new CheckBox[8];
                int mask; int.TryParse(field.value,NumberStyles.None,CultureInfo.InvariantCulture,out mask);
                for(int i=0;i<8;i++) {
                    bits[i]=new CheckBox {Text=(i+1).ToString(CultureInfo.InvariantCulture),AutoSize=true,Checked=(mask&(1<<i))!=0,Margin=new Padding(0,10,13,8)};
                    if (field.max.HasValue && (field.max.Value&(1<<i))==0) bits[i].Enabled=false;
                    bits[i].CheckedChanged+=delegate { Touch(); }; flow.Controls.Add(bits[i]);
                }
                Editor=flow;
            } else if (field.id=="dpi.stage" || field.options!=null && field.options.Length>0) {
                combo=new ComboBox {DropDownStyle=ComboBoxStyle.DropDownList,Width=280,Dock=DockStyle.Top,Margin=new Padding(0,8,0,8)};
                if(field.id=="dpi.stage")for(int i=1;i<=8;i++)combo.Items.Add(new G9SettingsOption {value=i.ToString(CultureInfo.InvariantCulture),name="Уровень "+i});
                else foreach (var option in field.options) combo.Items.Add(option);
                foreach (G9SettingsOption option in combo.Items) if(option.value==field.value)combo.SelectedItem=option;
                combo.SelectedIndexChanged+=delegate { Touch(); }; Editor=combo;
            } else if (field.type=="boolean" || field.type=="bool") {
                toggle=new CheckBox {Text="Включено",AutoSize=true,Dock=DockStyle.Top,Margin=new Padding(0,10,0,8),Checked=field.value=="1" || field.value=="true"};
                toggle.CheckedChanged+=delegate { Touch(); }; Editor=toggle;
            } else if (field.min.HasValue && field.max.HasValue && field.min>=0 && field.max<=65535 && field.min<=field.max) {
                int step=Math.Max(1,field.step??1), initial; int.TryParse(field.value,NumberStyles.Integer,CultureInfo.InvariantCulture,out initial);
                decimal scale=field.id=="sleep.level1Seconds"?60m:1m;
                bool dpi=field.id.StartsWith("dpi.value",StringComparison.Ordinal);
                decimal minimum=dpi && initial==0?0:field.min.Value/scale;
                number=new NumericUpDown {Minimum=minimum,Maximum=field.max.Value/scale,Increment=step/scale,DecimalPlaces=scale==60m?1:0,Width=112,ThousandsSeparator=true,Value=Math.Max(minimum,Math.Min(field.max.Value/scale,initial/scale))};
                int positions=dpi?320:(field.max.Value-field.min.Value)/step;
                slider=new TrackBar {Minimum=dpi && initial!=0?1:0,Maximum=Math.Max(dpi?1:0,positions),TickStyle=TickStyle.None,AutoSize=false,Height=28,Dock=DockStyle.Fill,SmallChange=1,LargeChange=dpi?10:5};
                slider.Value=ToSlider((int)(number.Value*scale),dpi,field.min.Value,step,slider.Maximum);
                var layout=new TableLayoutPanel {ColumnCount=3,AutoSize=true,Dock=DockStyle.Top,Margin=new Padding(0,6,0,8)};
                layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,118));layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,40));
                layout.Controls.Add(slider,0,0);layout.Controls.Add(number,1,0);layout.Controls.Add(new Label {Text=scale==60m?"мин":field.unit??"",AutoSize=true,Margin=new Padding(2,5,0,0)},2,0);
                slider.ValueChanged+=delegate { if(setting)return;setting=true;number.Value=(dpi?(slider.Value<=200?slider.Value*50:(slider.Value-100)*100):field.min.Value+slider.Value*step)/scale;setting=false;Touch(); };
                number.ValueChanged+=delegate { if(setting)return;setting=true;slider.Value=ToSlider((int)(number.Value*scale),dpi,field.min.Value,step,slider.Maximum);setting=false;Touch(); };
                Editor=layout;
            } else Editor=new Label {Text="Нет поддержанного редактора значения",AutoSize=true};
            tips.SetToolTip(Editor,Help(field.id));
        }
        static int ToSlider(int value,bool dpi,int min,int step,int max) { return Math.Max(dpi && value!=0?1:0,Math.Min(max,dpi?(value<=10000?value/50:value/100+100):(value-min)/step)); }
        void Touch() { if(setting)return;Edited=true;Stored.ForeColor=Draft==Field.value?Color.DimGray:Color.DarkBlue;changed(); }
        internal string Draft {
            get {
                if(!Edited)return Field.value;
                if(colorText!=null)return colorText.Text;
                if(bits!=null) {int value=0;for(int i=0;i<bits.Length;i++)if(bits[i].Checked)value|=1<<i;return value.ToString(CultureInfo.InvariantCulture);}
                if(combo!=null) {var item=combo.SelectedItem as G9SettingsOption;return item==null?Field.value:item.value;}
                if(toggle!=null)return Field.type=="boolean" && (Field.value=="true" || Field.value=="false")?(toggle.Checked?"true":"false"):(toggle.Checked?"1":"0");
                if(number!=null)return (number.Value*(Field.id=="sleep.level1Seconds"?60m:1m)).ToString("0",CultureInfo.InvariantCulture);
                return Field.value;
            }
        }
        internal void SetDraft(string value) {
            setting=true;
            try {
                if(colorText!=null)colorText.Text=value;
                else if(bits!=null) {int mask=int.Parse(value,CultureInfo.InvariantCulture);for(int i=0;i<bits.Length;i++)bits[i].Checked=(mask&(1<<i))!=0;}
                else if(combo!=null) {foreach(G9SettingsOption option in combo.Items)if(option.value==value){combo.SelectedItem=option;break;}}
                else if(toggle!=null)toggle.Checked=value=="1" || value=="true";
                else if(number!=null) {decimal scale=Field.id=="sleep.level1Seconds"?60m:1m;number.Value=decimal.Parse(value,CultureInfo.InvariantCulture)/scale;bool dpi=Field.id.StartsWith("dpi.value",StringComparison.Ordinal);slider.Value=ToSlider((int)(number.Value*scale),dpi,Field.min.Value,Math.Max(1,Field.step??1),slider.Maximum);}
            } finally {setting=false;}
            Touch();
        }
        internal void Reset() {
            if(number!=null && Field.value!=null) {
                decimal scale=Field.id=="sleep.level1Seconds"?60m:1m, value=decimal.Parse(Field.value,CultureInfo.InvariantCulture)/scale;
                if(value<number.Minimum || value>number.Maximum) {setting=true;number.Value=number.Minimum;slider.Value=slider.Minimum;setting=false;Edited=false;Stored.ForeColor=Color.DimGray;return;}
            }
            SetDraft(Field.value);Edited=false;Stored.ForeColor=Color.DimGray;
        }
    }
    protected override void Dispose(bool disposing) { if(disposing)tips.Dispose();base.Dispose(disposing); }
}
