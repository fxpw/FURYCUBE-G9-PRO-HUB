using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Furycube.G9.Bindings;

internal sealed class G9MappingRow {
    public int index { get; set; }
    public int function { get; set; }
    public int param1 { get; set; }
    public int param2 { get; set; }
}
internal sealed class G9MappingSnapshot {
    public int profile { get; set; }
    public string keymapHex { get; set; }
    public G9MappingRow[] buttons { get; set; }
    internal byte[] Keys;
    internal void RequireMatches(byte[] expected) {
        if(expected==null||expected.Length!=59||Keys==null||Keys.Length!=59)throw new InvalidDataException("Неверный размер ожидаемой таблицы.");
        for(int i=0;i<expected.Length;i++)if(Keys[i]!=expected[i])throw new InvalidDataException("Прочитанная таблица отличается от ожидаемой. Перечитайте назначения перед следующей записью.");
    }
    internal static G9MappingSnapshot Parse(string output) {
        if (String.IsNullOrEmpty(output) || output.Length > 1048576) throw new InvalidDataException("Отсутствует или слишком велик ответ устройства.");
        string json = null;
        foreach (string line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)) {
            if (!line.StartsWith("MAPPING_JSON ", StringComparison.Ordinal)) continue;
            if (json != null) throw new InvalidDataException("Получено несколько таблиц кнопок.");
            json = line.Substring(13);
        }
        if (json == null) throw new InvalidDataException("Устройство не вернуло таблицу кнопок.");
        if (json.Length > 16384) throw new InvalidDataException("Описание таблицы кнопок слишком велико.");
        var serializer = new JavaScriptSerializer();
        IDictionary<string,object> document;
        try {
            RejectDuplicateMembers(json, serializer);
            document = serializer.DeserializeObject(json) as IDictionary<string,object>;
        } catch (ArgumentException error) { throw new InvalidDataException("Некорректный JSON таблицы кнопок.", error); }
        if (document == null || document.Count != 3 || !document.ContainsKey("profile") || !document.ContainsKey("keymapHex") || !document.ContainsKey("buttons")) throw new InvalidDataException("Неожиданный формат таблицы кнопок.");
        var result = new G9MappingSnapshot { profile = Integer(document,"profile"), keymapHex = document["keymapHex"] as string };
        object[] rows = document["buttons"] as object[];
        if (result.profile < 1 || result.profile > 5 || rows == null || rows.Length != 18 || result.keymapHex == null || result.keymapHex.Length != 118) throw new InvalidDataException("Неожиданный формат таблицы кнопок.");
        result.buttons = new G9MappingRow[18];
        for (int i = 0; i < 18; i++) {
            var row = rows[i] as IDictionary<string,object>;
            if (row == null || !row.ContainsKey("index") || !row.ContainsKey("function") || !row.ContainsKey("param1") || !row.ContainsKey("param2")) throw new InvalidDataException("В описании кнопки отсутствуют обязательные поля.");
            foreach(var member in row) {
                if(member.Key=="index"||member.Key=="function"||member.Key=="param1"||member.Key=="param2")continue;
                if(member.Key=="visible") {if(!(member.Value is bool))throw new InvalidDataException("Признак видимости кнопки должен быть логическим значением.");}
                else if(member.Key=="name"||member.Key=="physicalLabel"||member.Key=="bindingId") {if(member.Value!=null&&!(member.Value is string))throw new InvalidDataException("Метаданные названия кнопки должны быть строкой.");}
                else throw new InvalidDataException("Неизвестное поле описания кнопки: "+member.Key);
            }
            result.buttons[i] = new G9MappingRow { index=Integer(row,"index"), function=Integer(row,"function"), param1=Integer(row,"param1"), param2=Integer(row,"param2") };
        }
        result.Keys = new byte[59];
        for (int i = 0; i < 59; i++) {
            char a=result.keymapHex[i*2], b=result.keymapHex[i*2+1];
            if (!IsHex(a) || !IsHex(b)) throw new InvalidDataException("Таблица содержит недопустимые шестнадцатеричные символы.");
            result.Keys[i] = byte.Parse(result.keymapHex.Substring(i * 2, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
        }
        if (result.Keys[0] != 8 || result.Keys[1] != 59 || result.Keys[2] != result.profile) throw new InvalidDataException("Неверный заголовок таблицы кнопок.");
        int sum = 0; for (int i = 3; i < 57; i++) sum += result.Keys[i];
        if (sum != (result.Keys[57] << 8 | result.Keys[58])) throw new InvalidDataException("Контрольная сумма таблицы не совпадает.");
        var seen = new HashSet<int>();
        foreach (var row in result.buttons) {
            if (row == null || row.index < 0 || row.index >= 18 || !seen.Add(row.index)) throw new InvalidDataException("Повторный или неизвестный индекс кнопки.");
            int offset = 3 + row.index * 3;
            if (row.function != result.Keys[offset] || row.param1 != result.Keys[offset + 1] || row.param2 != result.Keys[offset + 2])
                throw new InvalidDataException("Описание назначения не совпадает с прочитанными байтами.");
        }
        return result;
    }
    static bool IsHex(char value) { return value >= '0' && value <= '9' || value >= 'a' && value <= 'f' || value >= 'A' && value <= 'F'; }
    static int Integer(IDictionary<string,object> values,string name) {
        object value=values[name];
        if (!(value is int)) throw new InvalidDataException("Поле «"+name+"» должно быть целым JSON-числом.");
        return (int)value;
    }
    static void RejectDuplicateMembers(string json,JavaScriptSerializer serializer) {
        // The framework deserializer accepts repeated member names using the last
        // value. Reject that ambiguity before interpreting a device snapshot.
        var levels=new Stack<HashSet<string>>();
        for(int i=0;i<json.Length;i++) {
            char c=json[i];
            if(c=='{'){levels.Push(new HashSet<string>(StringComparer.Ordinal));continue;}
            if(c=='['){levels.Push(null);continue;}
            if(c=='}'||c==']'){if(levels.Count>0)levels.Pop();continue;}
            if(c!='"')continue;
            int start=i;bool escaped=false;for(i++;i<json.Length;i++){char next=json[i];if(escaped){escaped=false;continue;}if(next=='\\'){escaped=true;continue;}if(next=='"')break;}
            if(i>=json.Length)throw new InvalidDataException("Незавершённая строка JSON.");
            int after=i+1;while(after<json.Length&&Char.IsWhiteSpace(json[after]))after++;
            if(after<json.Length&&json[after]==':'&&levels.Count>0&&levels.Peek()!=null) {
                string name=serializer.Deserialize<string>(json.Substring(start,i-start+1));
                if(!levels.Peek().Add(name))throw new InvalidDataException("Поле JSON «"+name+"» повторяется.");
            }
        }
    }
}

internal sealed class G9ButtonEditor : UserControl {
    readonly Func<string, string> runDevice;
    readonly Action<string> logger;
    readonly ListBox buttons = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };
    readonly ComboBox categories = Combo();
    readonly ComboBox actions = Combo();
    readonly ComboBox keys = Combo();
    readonly CheckBox[] modifiers = new CheckBox[8];
    readonly NumericUpDown parameter = new NumericUpDown { Minimum = 0, Maximum = 255, Width = 110 };
    readonly FlowLayoutPanel keyboardPanel = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true };
    readonly FlowLayoutPanel parameterPanel = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true };
    readonly Label parameterLabel = new Label { AutoSize = true, Padding = new Padding(0, 5, 0, 0) };
    readonly Label current = new Label { AutoSize = false, Height = 65, Dock = DockStyle.Top };
    readonly Label help = new Label { AutoSize = false, Height = 90, Dock = DockStyle.Top, ForeColor = Color.DimGray };
    readonly Label status = new Label { AutoSize = false, Height = 54, Dock = DockStyle.Bottom, ForeColor = Color.DarkGoldenrod };
    readonly Button apply = new Button { Text = "Записать в мышь", AutoSize = true, Height = 36 };
    readonly Button read = new Button { Text = "Прочитать из мыши", AutoSize = true, Height = 36 };
    readonly CheckBox reserved = new CheckBox { Text = "Показать резервные слоты", AutoSize = true, Dock = DockStyle.Bottom };
    readonly ToolTip tips = new ToolTip();
    G9MappingSnapshot snapshot;
    internal event EventHandler MappingChanged;
    bool changing, busy;

    sealed class Item {
        internal ButtonDefinition Definition; internal string Assignment;
        public override string ToString() { return Definition.PhysicalLabel + "  —  " + Assignment; }
    }
    sealed class KeyChoice {
        internal KeyboardUsage Key;internal string Caption;
        public override string ToString(){return Caption??(Key==null?"Выберите клавишу":Key.Name);}
    }
    static ComboBox Combo() { return new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Top, Height = 30, IntegralHeight = false, DropDownHeight = 330 }; }
    static Label Caption(string text) { return new Label { Text = text, AutoSize = false, Height = 29, Dock = DockStyle.Top, Padding = new Padding(0, 7, 0, 0) }; }
    internal G9ButtonEditor(Func<string, string> device, Action<string> log) {
        runDevice = device; logger = log; Dock = DockStyle.Fill; Padding = new Padding(12);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 46)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 54));
        var left = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 0, 16, 0) };
        left.Controls.Add(buttons); left.Controls.Add(reserved); left.Controls.Add(Caption("Кнопки и направления колеса"));
        var right = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        var edit = new Panel { Dock = DockStyle.Top, Height = 470 };
        var controls = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 45 };
        controls.Controls.Add(read); controls.Controls.Add(apply);
        var modifierRow = new FlowLayoutPanel { Width = 450, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true };
        string[] names = { "Ctrl", "Shift", "Alt", "Win", "Правый Ctrl", "Правый Shift", "Правый Alt", "Правый Win" };
        for (int i = 0; i < 8; i++) { modifiers[i] = new CheckBox { Text = names[i], AutoSize = true, Margin = new Padding(0, 4, 12, 4) }; modifierRow.Controls.Add(modifiers[i]); }
        keys.Dock = DockStyle.None; keys.Width = 265;keys.DropDownWidth=360;keys.BindingContext=new BindingContext();SetKeyboardChoice(null);
        keyboardPanel.Controls.Add(new Label { Text = "Клавиша", AutoSize = true, Padding = new Padding(0, 5, 5, 0) });
        keyboardPanel.Controls.Add(keys); keyboardPanel.SetFlowBreak(keys, true); keyboardPanel.Controls.Add(modifierRow);
        parameterPanel.Controls.Add(parameterLabel); parameterPanel.Controls.Add(parameter);
        edit.Controls.Add(help); edit.Controls.Add(parameterPanel); edit.Controls.Add(keyboardPanel);
        edit.Controls.Add(actions); edit.Controls.Add(Caption("Действие")); edit.Controls.Add(categories); edit.Controls.Add(Caption("Категория")); edit.Controls.Add(current);
        right.Controls.Add(edit); right.Controls.Add(controls);
        layout.Controls.Add(left, 0, 0); layout.Controls.Add(right, 1, 0); Controls.Add(layout); Controls.Add(status);
        var allCategories = new List<string>(); foreach (var action in BindingCatalog.Actions) if (!allCategories.Contains(action.Category)) allCategories.Add(action.Category);
        categories.DataSource = allCategories;
        categories.SelectedIndexChanged += delegate { if (!changing) FillActions(null,true); };
        actions.SelectedIndexChanged += delegate { if (!changing) SetParameters(true); };
        buttons.SelectedIndexChanged += delegate { if (!changing) ShowSelection(); };
        reserved.CheckedChanged += delegate { FillButtons(); };
        read.Click += async delegate { await LoadMapping(); };
        apply.Click += async delegate { await SaveAssignment(); };
        keys.SelectedIndexChanged += delegate { if(!changing) UpdateApply(); };
        foreach(var modifier in modifiers)modifier.CheckedChanged+=delegate { if(!changing) UpdateApply(); };
        keyboardPanel.SizeChanged+=delegate { int width=Math.Max(230,keyboardPanel.ClientSize.Width-6);modifierRow.MaximumSize=new Size(width,0);modifierRow.Width=width; };
        tips.SetToolTip(reserved, "Слоты старого HUB, для которых не подтверждена отдельная физическая кнопка. Текущее значение сохраняется, пока вы его не измените.");
        tips.SetToolTip(apply, "Меняется только выбранное назначение. Перед записью создаётся резервная копия, затем таблица читается повторно.");
        FillButtons(); FillActions(null,true);UpdateApply();
        status.Text = "Нажмите «Прочитать из мыши». Будет прочитан активный профиль. Назначения хранятся в мыши и работают после закрытия HUB.";
    }
    void FillButtons() {
        int index = SelectedIndex(); changing = true; buttons.Items.Clear();
        foreach (var definition in BindingCatalog.Buttons) {
            if (!definition.IsVisible && !reserved.Checked) continue;
            var item = new Item { Definition = definition, Assignment = snapshot == null ? "не прочитано" : Describe(snapshot.Keys, definition.Index) };
            buttons.Items.Add(item); if (definition.Index == index) buttons.SelectedIndex = buttons.Items.Count - 1;
        }
        if (buttons.SelectedIndex < 0) for (int i = 0; i < buttons.Items.Count; i++) if (((Item)buttons.Items[i]).Definition.Index == 3) buttons.SelectedIndex = i;
        changing = false; ShowSelection();
    }
    int SelectedIndex() { var item = buttons.SelectedItem as Item; return item == null ? 3 : item.Definition.Index; }
    KeyboardUsage SelectedKey(){var choice=keys.SelectedItem as KeyChoice;return choice==null?null:choice.Key;}
    void SetKeyboardChoice(byte? usage) {
        var choices=new List<KeyChoice>();bool known=!usage.HasValue||usage.Value==0;
        foreach(var key in BindingCatalog.KeyboardKeys)if(usage.HasValue&&key.Usage==usage.Value)known=true;
        choices.Add(new KeyChoice {Caption=known?"Выберите клавишу":"Выберите клавишу (сейчас HID 0x"+usage.Value.ToString("X2")+")"});
        choices.Add(new KeyChoice {Key=new KeyboardUsage("Только модификаторы (без клавиши)",0)});
        foreach(var key in BindingCatalog.KeyboardKeys)choices.Add(new KeyChoice {Key=key});
        keys.DataSource=choices;keys.SelectedIndex=0;
        if(usage.HasValue)for(int i=1;i<choices.Count;i++)if(choices[i].Key.Usage==usage.Value){keys.SelectedIndex=i;break;}
    }
    static string Describe(byte[] data, int index) {
        int at = 3 + index * 3; var action = BindingCatalog.Resolve(data[at], data[at + 1], data[at + 2]);
        if(action!=null)try{BindingCatalog.Validate(data[at],data[at+1],data[at+2]);}catch(InvalidDataException){action=null;}
        if (action == null) return "неизвестное назначение (сохранено)";
        if (action.ParameterKind == BindingParameterKind.Keyboard) {
            string name = "HID " + data[at + 2].ToString("X2"); foreach (var key in BindingCatalog.KeyboardKeys) if (key.Usage == data[at + 2]) name = key.Name;
            string[] names = { "Ctrl", "Shift", "Alt", "Win", "R Ctrl", "R Shift", "R Alt", "R Win" }; var text = new StringBuilder();
            for (int i = 0; i < 8; i++) if ((data[at + 1] & (1 << i)) != 0) text.Append(names[i] + "+");
            if(data[at+2]==0&&text.Length>0)return text.ToString().TrimEnd('+');return text + name;
        }
        if (action.ParameterKind == BindingParameterKind.Macro) return "Макрос, слот " + data[at + 2];
        return action.Name;
    }
    void FillActions(string id,bool defaults) {
        bool previous = changing; changing = true; var items = new List<BindingAction>();
        foreach (var action in BindingCatalog.Actions) if (action.Category == (string)categories.SelectedItem) items.Add(action);
        actions.DataSource = items;
        if (id != null) {actions.SelectedIndex=-1;foreach (var action in items) if (action.Id == id) { actions.SelectedItem = action; break; }}
        changing = previous; SetParameters(defaults);
    }
    void ShowSelection() {
        var item = buttons.SelectedItem as Item; if (item == null) return;
        current.Text = item.Definition.PhysicalLabel + Environment.NewLine + "Сейчас: " + (snapshot == null ? "настройки ещё не прочитаны" : Describe(snapshot.Keys, item.Definition.Index));
        changing=true;
        try {
            SetKeyboardChoice(null);foreach(var modifier in modifiers)modifier.Checked=false;
            if (snapshot != null) {
            int at = 3 + item.Definition.Index * 3; var action = BindingCatalog.Resolve(snapshot.Keys[at], snapshot.Keys[at + 1], snapshot.Keys[at + 2]);
            if(action!=null)try{BindingCatalog.Validate(snapshot.Keys[at],snapshot.Keys[at+1],snapshot.Keys[at+2]);}catch(InvalidDataException){action=null;}
            if (action != null) {
                categories.SelectedItem = action.Category; FillActions(action.Id,false);
                byte p1 = snapshot.Keys[at + 1], p2 = snapshot.Keys[at + 2];
                if (action.ParameterKind == BindingParameterKind.Keyboard) {
                    SetKeyboardChoice(p2);
                    for (int i = 0; i < 8; i++) modifiers[i].Checked = (p1 & (1 << i)) != 0;
                    if(SelectedKey()==null)help.Text="В памяти записана HID-клавиша 0x"+p2.ToString("X2")+", которой нет в списке. Текущее назначение сохранено. Чтобы заменить его, явно выберите новую клавишу.";
                } else if (action.ParameterKind != BindingParameterKind.None) parameter.Value = Math.Max(parameter.Minimum, Math.Min(parameter.Maximum, p2));
            } else {
                categories.SelectedIndex=-1;actions.DataSource=null;keyboardPanel.Visible=parameterPanel.Visible=false;
                help.Text="Назначение "+snapshot.Keys[at].ToString("X2")+" "+snapshot.Keys[at+1].ToString("X2")+" "+snapshot.Keys[at+2].ToString("X2")+" пока не поддерживается редактором. Оно сохранено в памяти без изменений. Для замены явно выберите категорию и действие.";
            }
            }
        } finally { changing=false; }
        UpdateApply();
    }
    void SetParameters(bool defaults) {
        var action = actions.SelectedItem as BindingAction; if (action == null) {keyboardPanel.Visible=parameterPanel.Visible=false;UpdateApply();return;}
        keyboardPanel.Visible = action.ParameterKind == BindingParameterKind.Keyboard;
        parameterPanel.Visible = action.ParameterKind == BindingParameterKind.Macro || action.ParameterKind == BindingParameterKind.Sniper || action.ParameterKind == BindingParameterKind.Fire;
        parameter.Minimum = 0; parameter.Maximum = 255;
        if (action.ParameterKind == BindingParameterKind.Macro) { parameterLabel.Text = "Слот макроса в памяти"; parameter.Minimum = 1; parameter.Maximum = 32; }
        else if (action.ParameterKind == BindingParameterKind.Sniper) { parameterLabel.Text = "Уровень DPI (1–8)"; parameter.Minimum = 1; parameter.Maximum = 8; }
        else parameterLabel.Text = "Параметр скоростной стрельбы (0–255)";
        if (defaults) {
            SetKeyboardChoice(action.ParameterKind==BindingParameterKind.Keyboard?(byte?)action.DefaultParam2:null);
            parameter.Value = Math.Max(parameter.Minimum, Math.Min(parameter.Maximum, action.DefaultParam2));
            if (action.ParameterKind == BindingParameterKind.Macro) { byte slot = BindingCatalog.GetMacroSlot(SelectedIndex()); if (slot > 0) parameter.Value = slot; }
            for (int i = 0; i < 8; i++) modifiers[i].Checked = (action.DefaultParam1 & (1 << i)) != 0;
        }
        help.Text = action.ParameterKind == BindingParameterKind.Macro ? "Назначает уже записанный аппаратный макрос. Создать последовательность можно в редакторе макросов; выбор слота сам по себе её не загружает. Для кнопки-экрана штатный слот — 13." :
            SelectedIndex() == 3 ? "Кнопка 6 — нажимаемый OLED-экран. Выберите действие и запишите его. Проверка прошивки и её исправление доступны отдельно на вкладке диагностики." :
            "Выберите действие и нажмите «Записать в мышь». Остальные назначения сохраняются. Хотя бы одна основная кнопка должна выполнять левый клик.";
        UpdateApply();
    }
    void UpdateApply() {
        var action=actions.SelectedItem as BindingAction;var key=SelectedKey();
        bool keyboardValid=true;if(action!=null&&action.ParameterKind==BindingParameterKind.Keyboard){byte mask=0;for(int i=0;i<8;i++)if(modifiers[i].Checked)mask|=(byte)(1<<i);keyboardValid=key!=null&&(key.Usage!=0||mask!=0);}
        apply.Enabled=!busy&&snapshot!=null&&action!=null&&keyboardValid;
    }
    async Task LoadMapping() {
        if (busy) return; SetBusy(true); status.Text = "Читаю назначения…";
        try { snapshot = G9MappingSnapshot.Parse(await Task.Run(delegate { return runDevice("mapping --active"); })); FillButtons(); status.ForeColor = Color.DarkGreen; status.Text = "Таблица профиля " + snapshot.profile + " прочитана. Выберите кнопку и действие."; }
        catch (Exception error) { snapshot=null;FillButtons();Report(error); }
        finally { SetBusy(false); }
    }
    async Task SaveAssignment() {
        if (busy || snapshot == null) return;
        try {
            var action = actions.SelectedItem as BindingAction; if (action == null) return;
            byte p1 = action.DefaultParam1, p2 = action.DefaultParam2;
            if (action.ParameterKind == BindingParameterKind.Keyboard) {
                var key = SelectedKey(); if (key == null) throw new InvalidOperationException("Выберите клавишу.");
                p1 = 0; for (int i = 0; i < 8; i++) if (modifiers[i].Checked) p1 |= (byte)(1 << i); p2 = key.Usage;
            } else if (action.ParameterKind != BindingParameterKind.None) p2 = (byte)parameter.Value;
            int index = SelectedIndex(); int profile = snapshot.profile; byte[] expected=BindingCatalog.WithAssignment(snapshot.Keys, index, action.Function, p1, p2, profile);
            string command = string.Format(CultureInfo.InvariantCulture, "assign --profile {0} --index {1} --function {2:X2} --param1 {3:X2} --param2 {4:X2}", profile, index, action.Function, p1, p2);
            SetBusy(true); status.Text = "Сохраняю назначение и проверяю чтением…";
            await Task.Run(delegate { runDevice(command); });
            snapshot = G9MappingSnapshot.Parse(await Task.Run(delegate { return runDevice("mapping --profile " + profile.ToString(CultureInfo.InvariantCulture)); }));
            snapshot.RequireMatches(expected);
            FillButtons();
            status.ForeColor = Color.DarkGreen; status.Text = "Назначение записано и прочитано обратно. Проверьте действие выбранной кнопки.";
            if(MappingChanged!=null)MappingChanged(this,EventArgs.Empty);
        } catch (Exception error) { snapshot=null;FillButtons();Report(error); }
        finally { SetBusy(false); }
    }
    void SetBusy(bool value) { busy = value; read.Enabled = !value; buttons.Enabled = reserved.Enabled = categories.Enabled = actions.Enabled = keyboardPanel.Enabled = parameterPanel.Enabled = !value;UpdateApply(); }
    internal void InvalidateProfile() {
        snapshot = null; FillButtons(); UpdateApply();
        status.ForeColor = Color.DarkGoldenrod;
        status.Text = "Профиль изменён. Прочитайте назначения из мыши перед следующей записью.";
    }
    void Report(Exception error) { status.ForeColor = Color.DarkRed; status.Text = "Операция остановлена: " + error.Message;if(logger!=null)logger(error.Message); }
    protected override void Dispose(bool disposing) { if (disposing) tips.Dispose(); base.Dispose(disposing); }
}
