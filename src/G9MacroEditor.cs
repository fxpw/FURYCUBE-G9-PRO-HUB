// Offline macro authoring. Recording is scoped to its explicitly focused dialog.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace Furycube.G9.Macros {
    public sealed class G9MacroEditor : UserControl {
        readonly Action<string> log;readonly ListBox names=new ListBox();readonly DataGridView grid=new DataGridView();
        readonly TextBox name=new TextBox();readonly ComboBox repeat=new ComboBox(),kind=new ComboBox(),key=new ComboBox(),mouse=new ComboBox(),delayMode=new ComboBox();
        readonly NumericUpDown count=Number(1,999,1),x=Number(-32768,32767,0),y=Number(-32768,32767,0),wheel=Number(-32768,32767,120),delay=Number(0,99999,10),fixedDelay=Number(0,99999,10);
        readonly Label status=new Label(),warnings=new Label(),keyLabel=new Label(),mouseLabel=new Label(),xLabel=new Label(),yLabel=new Label(),wheelLabel=new Label(),delayLabel=new Label();
        readonly ToolTip tips=new ToolTip();readonly Timer autosave=new Timer();bool updating;int current=-1;
        readonly string autosavePath;
        public MacroLibrary Library {get;private set;}
        public MacroDefinition SelectedMacro {get{return current>=0&&current<Library.Macros.Count?Library.Macros[current]:null;}}
        static NumericUpDown Number(int minimum,int maximum,int value){return new NumericUpDown{Minimum=minimum,Maximum=maximum,Value=value,Width=86};}
        static ComboBox Combo(ComboBox box){box.DropDownStyle=ComboBoxStyle.DropDownList;box.Width=180;return box;}
        public G9MacroEditor():this(null){}
        public G9MacroEditor(Action<string> log):this(log,Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"FURYCUBE-G9-PRO","editor","macros.autosave.json")){}
        public G9MacroEditor(Action<string> log,string autosaveFile) {
            if(String.IsNullOrWhiteSpace(autosaveFile))throw new ArgumentNullException("autosaveFile");autosavePath=Path.GetFullPath(autosaveFile);
            this.log=log;Size=new Size(1050,620);MinimumSize=new Size(700,420);Dock=DockStyle.Fill;BackColor=Color.FromArgb(245,247,250);Padding=new Padding(10);Library=new MacroLibrary();
            var split=new SplitContainer{Size=new Size(1000,500),Dock=DockStyle.Fill,FixedPanel=FixedPanel.Panel1,SplitterDistance=205,Panel1MinSize=180,Panel2MinSize=340};
            names.Dock=DockStyle.Fill;names.IntegralHeight=false;names.SelectedIndexChanged+=delegate{if(!updating)SelectMacro(names.SelectedIndex);};
            var leftButtons=new FlowLayoutPanel{Dock=DockStyle.Bottom,Height=74,AutoSize=false};
            Button(leftButtons,"Добавить",delegate{AddMacro();});Button(leftButtons,"Копия",delegate{CopyMacro();});Button(leftButtons,"Удалить",delegate{DeleteMacro();});
            split.Panel1.Controls.Add(names);split.Panel1.Controls.Add(leftButtons);
            var right=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=6};
            right.RowStyles.Add(new RowStyle(SizeType.AutoSize));right.RowStyles.Add(new RowStyle(SizeType.Percent,100));right.RowStyles.Add(new RowStyle(SizeType.AutoSize));right.RowStyles.Add(new RowStyle(SizeType.AutoSize));right.RowStyles.Add(new RowStyle(SizeType.AutoSize));right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var header=new FlowLayoutPanel{Dock=DockStyle.Fill,AutoSize=true,WrapContents=true,Padding=new Padding(0,0,0,6)};
            header.Controls.Add(TextLabel("Название:"));name.Width=175;name.MaxLength=20;header.Controls.Add(name);header.Controls.Add(TextLabel("Повтор:"));
            Combo(repeat);repeat.Width=215;repeat.Items.AddRange(new object[]{"Заданное число раз","До нажатия любой клавиши","Пока кнопка удерживается"});header.Controls.Add(repeat);header.Controls.Add(count);
            tips.SetToolTip(count,"Количество повторов: 1–999. Для остальных режимов число не используется.");
            name.TextChanged+=delegate{if(!updating&&SelectedMacro!=null){SelectedMacro.Name=name.Text;RefreshName();Changed();}};
            repeat.SelectedIndexChanged+=delegate{count.Enabled=SelectedMacro!=null&&repeat.SelectedIndex==0;if(!updating&&SelectedMacro!=null){SelectedMacro.RepeatMode=(MacroRepeatMode)repeat.SelectedIndex;Changed();}};
            count.ValueChanged+=delegate{if(!updating&&SelectedMacro!=null){SelectedMacro.RepeatCount=(int)count.Value;Changed();}};
            right.Controls.Add(header,0,0);
            grid.Dock=DockStyle.Fill;grid.AllowUserToAddRows=false;grid.AllowUserToDeleteRows=false;grid.AllowUserToResizeRows=false;grid.ReadOnly=true;grid.MultiSelect=false;grid.SelectionMode=DataGridViewSelectionMode.FullRowSelect;grid.RowHeadersVisible=false;grid.AutoGenerateColumns=false;grid.BackgroundColor=Color.White;grid.BorderStyle=BorderStyle.FixedSingle;
            grid.Columns.Add(new DataGridViewTextBoxColumn{HeaderText="№",Width=42});grid.Columns.Add(new DataGridViewTextBoxColumn{HeaderText="Действие",Width=180});grid.Columns.Add(new DataGridViewTextBoxColumn{HeaderText="Параметры",AutoSizeMode=DataGridViewAutoSizeColumnMode.Fill});
            grid.SelectionChanged+=delegate{if(!updating)ShowStep();};right.Controls.Add(grid,0,1);
            var stepPanel=new FlowLayoutPanel{AutoSize=true,Dock=DockStyle.Fill,WrapContents=true,Padding=new Padding(0,5,0,0)};
            stepPanel.Controls.Add(TextLabel("Шаг:"));Combo(kind);kind.Width=172;kind.Items.AddRange(new object[]{"Нажать клавишу","Отпустить клавишу","Нажать кнопку мыши","Отпустить кнопку мыши","Переместить мышь","Прокрутить колесо","Пауза"});stepPanel.Controls.Add(kind);
            Combo(key);key.Width=140;for(int vk=8;vk<=254;vk++)key.Items.Add(new KeyChoice(vk));Combo(mouse);mouse.Width=130;mouse.Items.AddRange(new object[]{"Левая","Правая","Средняя","Вперёд","Назад"});
            AddField(stepPanel,keyLabel,"Клавиша",key);AddField(stepPanel,mouseLabel,"Кнопка",mouse);AddField(stepPanel,xLabel,"X, px",x);AddField(stepPanel,yLabel,"Y, px",y);AddField(stepPanel,wheelLabel,"Колесо",wheel);AddField(stepPanel,delayLabel,"Пауза, мс",delay);
            tips.SetToolTip(x,"Относительное смещение по горизонтали; отрицательное число — влево.");tips.SetToolTip(y,"Относительное смещение по вертикали; отрицательное число — вверх.");tips.SetToolTip(wheel,"Обычно 120 — один шаг вверх, −120 — вниз.");tips.SetToolTip(delay,"Задержка 0–99999 мс. Это отдельный шаг макроса.");
            kind.SelectedIndexChanged+=delegate{ShowFields();};kind.SelectedIndex=0;key.SelectedIndex=0x30-8;mouse.SelectedIndex=0;
            Button(stepPanel,"Добавить шаг",delegate{EditStep(false);});Button(stepPanel,"Изменить выбранный",delegate{EditStep(true);});right.Controls.Add(stepPanel,0,2);
            var editButtons=new FlowLayoutPanel{AutoSize=true,Dock=DockStyle.Fill};
            Button(editButtons,"↑ Выше",delegate{MoveStep(-1);});Button(editButtons,"↓ Ниже",delegate{MoveStep(1);});Button(editButtons,"Удалить шаг",DeleteStep);Button(editButtons,"Очистить",delegate{if(SelectedMacro!=null){SelectedMacro.Steps.Clear();RefreshSteps(-1);Changed();}});
            right.Controls.Add(editButtons,0,3);
            var recording=new FlowLayoutPanel{AutoSize=true,Dock=DockStyle.Fill,WrapContents=true};
            Button(recording,"Записать в конец…",Record);recording.Controls.Add(TextLabel("Задержки:"));Combo(delayMode);delayMode.Width=155;delayMode.Items.AddRange(new object[]{"Как при записи","Фиксированные","Без задержек"});delayMode.SelectedIndex=0;recording.Controls.Add(delayMode);recording.Controls.Add(fixedDelay);recording.Controls.Add(TextLabel("мс"));
            delayMode.SelectedIndexChanged+=delegate{fixedDelay.Enabled=delayMode.SelectedIndex==1;};fixedDelay.Enabled=false;right.Controls.Add(recording,0,4);
            warnings.AutoSize=true;warnings.ForeColor=Color.FromArgb(135,85,0);warnings.MaximumSize=new Size(900,0);right.Controls.Add(warnings,0,5);
            split.Panel2.Controls.Add(right);
            var bottom=new FlowLayoutPanel{Dock=DockStyle.Bottom,Height=82,WrapContents=true};
            Button(bottom,"Импорт JSON…",Import);Button(bottom,"Экспорт JSON…",Export);
            var upload=Button(bottom,"В мышь",delegate{});upload.Enabled=false;tips.SetToolTip(upload,"Загрузка макроса пока недоступна: сначала требуется подтвердить обычную клавишу с шестой кнопки.");
            var limitation=TextLabel("Редактор сохраняет макросы на ПК. Загрузка в мышь — после проверки обычной клавиши.");limitation.ForeColor=Color.DimGray;limitation.AutoSize=true;bottom.Controls.Add(limitation);
            status.AutoSize=true;status.ForeColor=Color.DimGray;bottom.Controls.Add(status);
            Controls.Add(split);Controls.Add(bottom);
            autosave.Interval=500;autosave.Tick+=delegate{autosave.Stop();SaveAutosave();};
            if(File.Exists(autosavePath))try{Library=MacroProjectCodec.Load(autosavePath);status.Text="Библиотека восстановлена с ПК.";}catch(Exception ex){status.Text="Не удалось открыть автосохранение: "+ex.Message;}
            if(Library.Macros.Count==0)Library.Macros.Add(new MacroDefinition());RefreshNames(0);ShowFields();
        }
        static Label TextLabel(string text){return new Label{Text=text,AutoSize=true,Margin=new Padding(3,6,3,3)};}
        static void AddField(Control panel,Label label,string text,Control value){label.Text=text;label.AutoSize=true;label.Margin=new Padding(3,6,3,3);panel.Controls.Add(label);panel.Controls.Add(value);}
        static Button Button(Control parent,string text,Action action){var button=new Button{Text=text,AutoSize=true,Height=29,Margin=new Padding(3)};button.Click+=delegate{action();};parent.Controls.Add(button);return button;}
        void Report(string text){status.Text=text;if(log!=null)log(text);}
        void Changed(){autosave.Stop();autosave.Start();RefreshWarnings();}
        void SaveAutosave(){try{MacroProjectCodec.Save(Library,autosavePath);status.Text="Макросы сохранены на ПК.";}catch(Exception ex){status.Text="Не сохранено: "+ex.Message;}}
        void RefreshWarnings(){if(SelectedMacro==null){warnings.Text="";return;}try{var items=SelectedMacro.GetWarnings();for(int i=0;i<items.Length;i++)items[i]=items[i].Replace("The macro has no steps.","Добавьте шаги в макрос.").Replace("Repeated press without release: ","Повторное нажатие без отпускания: ").Replace("Release without press: ","Отпускание без нажатия: ").Replace("Missing release: ","Не хватает отпускания: ");warnings.Text=items.Length==0?"Нажатия и отпускания сбалансированы.":"Проверьте: "+String.Join("; ",items);}catch(Exception ex){warnings.Text=ex.Message;}}
        void RefreshName(){if(current<0)return;updating=true;try{names.Items[current]=SelectedMacro.Name;}finally{updating=false;}}
        void RefreshNames(int selection){updating=true;try{names.Items.Clear();foreach(var macro in Library.Macros)names.Items.Add(macro.Name);names.SelectedIndex=selection>=0&&selection<names.Items.Count?selection:-1;}finally{updating=false;}SelectMacro(names.SelectedIndex);}
        void SelectMacro(int index){current=index;updating=true;try{var macro=SelectedMacro;name.Enabled=repeat.Enabled=macro!=null;name.Text=macro==null?"":macro.Name;repeat.SelectedIndex=macro==null?-1:(int)macro.RepeatMode;count.Enabled=macro!=null&&macro.RepeatMode==MacroRepeatMode.Counted;count.Value=macro==null?1:macro.RepeatCount;}finally{updating=false;}RefreshSteps(-1);}
        string UniqueName(string basis){string trimmed=String.IsNullOrWhiteSpace(basis)?"Макрос":basis;for(int i=1;;i++){string suffix=" "+i;string value=trimmed.Substring(0,Math.Min(trimmed.Length,20-suffix.Length))+suffix;bool exists=false;foreach(var macro in Library.Macros)if(String.Equals(macro.Name,value,StringComparison.OrdinalIgnoreCase)){exists=true;break;}if(!exists)return value;}}
        void AddMacro(){if(Library.Macros.Count>=256){Report("Лимит библиотеки: 256 макросов.");return;}Library.Macros.Add(new MacroDefinition{Name=UniqueName("Макрос")});RefreshNames(Library.Macros.Count-1);Changed();}
        void CopyMacro(){if(SelectedMacro==null)return;if(Library.Macros.Count>=256){Report("Лимит библиотеки: 256 макросов.");return;}var copy=SelectedMacro.Clone();copy.Name=UniqueName(copy.Name);Library.Macros.Add(copy);RefreshNames(Library.Macros.Count-1);Changed();}
        void DeleteMacro(){if(SelectedMacro==null)return;int old=current;Library.Macros.RemoveAt(old);RefreshNames(Math.Min(old,Library.Macros.Count-1));Changed();}
        static string KindName(MacroStepKind value){return new[]{"Нажать клавишу","Отпустить клавишу","Нажать мышь","Отпустить мышь","Перемещение","Колесо","Пауза"}[(int)value];}
        static string Detail(MacroStep step){switch(step.Kind){case MacroStepKind.KeyDown:case MacroStepKind.KeyUp:return new KeyChoice(step.VirtualKey).ToString();case MacroStepKind.MouseDown:case MacroStepKind.MouseUp:return new[]{"Левая","Правая","Средняя","Вперёд","Назад"}[(int)step.Button];case MacroStepKind.MouseMove:return "X="+step.X+", Y="+step.Y;case MacroStepKind.Wheel:return step.Delta.ToString();default:return step.DelayMilliseconds+" мс";}}
        int StepIndex {get{return grid.SelectedRows.Count==0?-1:grid.SelectedRows[0].Index;}}
        void RefreshSteps(int selection){updating=true;try{grid.Rows.Clear();if(SelectedMacro!=null)for(int i=0;i<SelectedMacro.Steps.Count;i++){var step=SelectedMacro.Steps[i];grid.Rows.Add(i+1,KindName(step.Kind),Detail(step));}grid.ClearSelection();if(selection>=0&&selection<grid.Rows.Count){grid.Rows[selection].Selected=true;grid.CurrentCell=grid.Rows[selection].Cells[0];}}finally{updating=false;}ShowStep();RefreshWarnings();}
        void ShowStep(){int selected=StepIndex;if(selected<0||SelectedMacro==null)return;var step=SelectedMacro.Steps[selected];kind.SelectedIndex=(int)step.Kind;key.SelectedIndex=Math.Max(0,step.VirtualKey-8);mouse.SelectedIndex=(int)step.Button;x.Value=step.X;y.Value=step.Y;wheel.Value=step.Kind==MacroStepKind.Wheel?step.Delta:120;delay.Value=step.DelayMilliseconds;}
        void ShowFields(){bool keyboard=kind.SelectedIndex==0||kind.SelectedIndex==1,button=kind.SelectedIndex==2||kind.SelectedIndex==3,move=kind.SelectedIndex==4,scroll=kind.SelectedIndex==5,pause=kind.SelectedIndex==6;key.Visible=keyLabel.Visible=keyboard;mouse.Visible=mouseLabel.Visible=button;x.Visible=xLabel.Visible=y.Visible=yLabel.Visible=move;wheel.Visible=wheelLabel.Visible=scroll;delay.Visible=delayLabel.Visible=pause;}
        MacroStep ReadStep(){var step=new MacroStep{Kind=(MacroStepKind)kind.SelectedIndex};switch(step.Kind){case MacroStepKind.KeyDown:case MacroStepKind.KeyUp:step.VirtualKey=((KeyChoice)key.SelectedItem).Value;break;case MacroStepKind.MouseDown:case MacroStepKind.MouseUp:step.Button=(MacroMouseButton)mouse.SelectedIndex;break;case MacroStepKind.MouseMove:step.X=(int)x.Value;step.Y=(int)y.Value;break;case MacroStepKind.Wheel:step.Delta=(int)wheel.Value;break;case MacroStepKind.Delay:step.DelayMilliseconds=(int)delay.Value;break;}step.Validate();return step;}
        void EditStep(bool replace){if(SelectedMacro==null)return;int index=StepIndex;try{var step=ReadStep();if(replace){if(index<0)return;SelectedMacro.Steps[index]=step;}else{if(SelectedMacro.Steps.Count>=4096)throw new InvalidDataException("Лимит 4096 шагов.");index=index<0?SelectedMacro.Steps.Count:index+1;SelectedMacro.Steps.Insert(index,step);}RefreshSteps(index);Changed();}catch(Exception ex){Report(ex.Message);}}
        void MoveStep(int delta){if(SelectedMacro==null)return;int index=StepIndex,next=index+delta;if(index<0||next<0||next>=SelectedMacro.Steps.Count)return;var step=SelectedMacro.Steps[index];SelectedMacro.Steps.RemoveAt(index);SelectedMacro.Steps.Insert(next,step);RefreshSteps(next);Changed();}
        void DeleteStep(){if(SelectedMacro==null)return;int index=StepIndex;if(index<0)return;SelectedMacro.Steps.RemoveAt(index);RefreshSteps(Math.Min(index,SelectedMacro.Steps.Count-1));Changed();}
        void Record(){if(SelectedMacro==null)return;using(var dialog=new MacroRecordingDialog((MacroDelayMode)delayMode.SelectedIndex,(int)fixedDelay.Value)){if(dialog.ShowDialog(FindForm())!=DialogResult.OK)return;var recorded=dialog.Result;if(SelectedMacro.Steps.Count+recorded.Count>4096){Report("Запись не добавлена: превышен лимит 4096 шагов.");return;}SelectedMacro.Steps.AddRange(recorded);RefreshSteps(SelectedMacro.Steps.Count-1);Changed();Report("Запись добавлена. На устройство ничего не отправлено.");}}
        void Import(){using(var dialog=new OpenFileDialog{Filter="Макросы G9 (*.g9macros.json)|*.g9macros.json|JSON (*.json)|*.json",Title="Открыть библиотеку макросов"}){if(dialog.ShowDialog(FindForm())!=DialogResult.OK)return;try{var imported=MacroProjectCodec.Load(dialog.FileName);Library=imported;RefreshNames(Library.Macros.Count>0?0:-1);Changed();Report("Библиотека открыта на ПК.");}catch(Exception ex){Report("Импорт не выполнен: "+ex.Message);}}}
        void Export(){using(var dialog=new SaveFileDialog{Filter="Макросы G9 (*.g9macros.json)|*.g9macros.json",FileName="macros.g9macros.json",Title="Сохранить библиотеку макросов"}){if(dialog.ShowDialog(FindForm())!=DialogResult.OK)return;try{MacroProjectCodec.Save(Library,dialog.FileName);Report("Библиотека сохранена в JSON.");}catch(Exception ex){Report("Экспорт не выполнен: "+ex.Message);}}}
        protected override void Dispose(bool disposing){if(disposing){autosave.Stop();SaveAutosave();autosave.Dispose();tips.Dispose();}base.Dispose(disposing);}
        sealed class KeyChoice {public int Value;public KeyChoice(int value){Value=value;}public override string ToString(){if(!Enum.IsDefined(typeof(Keys),Value))return "Клавиша VK 0x"+Value.ToString("X2");string text=((Keys)Value).ToString();if(text.StartsWith("D")&&text.Length==2&&Char.IsDigit(text[1]))text=text.Substring(1);return text.Replace("ShiftKey","Shift").Replace("ControlKey","Ctrl").Replace("Menu","Alt");}}
    }

    sealed class MacroRecordingDialog : Form,IMessageFilter {
        readonly MacroRecorder recorder;readonly Stopwatch timer=new Stopwatch();readonly Panel area=new Panel();readonly Label progress=new Label();readonly CheckBox movement=new CheckBox();Point? previousPoint;long lastMovement;bool installed,stopping;
        public List<MacroStep> Result {get;private set;}
        public MacroRecordingDialog(MacroDelayMode delayMode,int fixedDelay) {
            recorder=new MacroRecorder(delayMode,fixedDelay);Text="Запись макроса — только в этом окне";StartPosition=FormStartPosition.CenterParent;Size=new Size(620,350);MinimumSize=new Size(500,300);ShowInTaskbar=false;
            var help=new Label{Dock=DockStyle.Top,Height=65,Padding=new Padding(10),Text="Нажимайте клавиши в этом окне. Кнопки мыши и колесо записываются в поле ниже. Esc завершает запись. За пределами этого окна ввод не записывается."};
            area.Dock=DockStyle.Fill;area.BackColor=Color.FromArgb(223,234,245);area.BorderStyle=BorderStyle.FixedSingle;area.TabStop=true;area.Controls.Add(new Label{Dock=DockStyle.Top,Height=30,Text="Поле записи мыши",TextAlign=ContentAlignment.MiddleCenter,Enabled=false});
            area.MouseEnter+=delegate{area.Focus();previousPoint=null;};
            var bottom=new FlowLayoutPanel{Dock=DockStyle.Bottom,Height=70,Padding=new Padding(8)};movement.Text="Записывать перемещение";movement.AutoSize=true;bottom.Controls.Add(movement);var stop=new Button{Text="Завершить",AutoSize=true};stop.Click+=delegate{Finish();};bottom.Controls.Add(stop);progress.AutoSize=true;bottom.Controls.Add(progress);
            Controls.Add(area);Controls.Add(bottom);Controls.Add(help);FormClosing+=delegate{RemoveFilter();};Shown+=delegate{timer.Start();Application.AddMessageFilter(this);installed=true;area.Focus();};
        }
        void RemoveFilter(){if(installed){Application.RemoveMessageFilter(this);installed=false;}timer.Stop();}
        void Finish(){if(stopping)return;stopping=true;try{Result=recorder.Finish();DialogResult=DialogResult.OK;Close();}catch(Exception ex){stopping=false;progress.Text=ex.Message;}}
        void Append(MacroStep step){try{recorder.Append(step,timer.ElapsedMilliseconds);progress.Text="Шагов: "+recorder.Count;}catch(Exception ex){progress.Text=ex.Message;Finish();}}
        public bool PreFilterMessage(ref Message message) {
            if(ActiveForm!=this||stopping)return false;int id=message.Msg;
            if(id==0x100||id==0x104||id==0x101||id==0x105) {
                int vk=unchecked((int)message.WParam.ToInt64())&255;
                if(vk==(int)Keys.Escape){if(id==0x100||id==0x104)Finish();return true;}
                if(vk>=8)Append(new MacroStep{Kind=id==0x100||id==0x104?MacroStepKind.KeyDown:MacroStepKind.KeyUp,VirtualKey=vk});return true;
            }
            if(message.HWnd!=area.Handle)return false;
            MacroMouseButton button=MacroMouseButton.Left;bool down=false,mouseEvent=true;
            switch(id){case 0x201:down=true;break;case 0x202:break;case 0x204:button=MacroMouseButton.Right;down=true;break;case 0x205:button=MacroMouseButton.Right;break;case 0x207:button=MacroMouseButton.Middle;down=true;break;case 0x208:button=MacroMouseButton.Middle;break;case 0x20B:case 0x20C:button=((message.WParam.ToInt64()>>16)&65535)==1?MacroMouseButton.Back:MacroMouseButton.Forward;down=id==0x20B;break;default:mouseEvent=false;break;}
            if(mouseEvent){Append(new MacroStep{Kind=down?MacroStepKind.MouseDown:MacroStepKind.MouseUp,Button=button});return true;}
            if(id==0x20A){int delta=(short)((message.WParam.ToInt64()>>16)&65535);if(delta!=0)Append(new MacroStep{Kind=MacroStepKind.Wheel,Delta=delta});return true;}
            if(id==0x200&&movement.Checked){long pos=message.LParam.ToInt64();var point=new Point((short)(pos&65535),(short)((pos>>16)&65535));if(previousPoint.HasValue&&timer.ElapsedMilliseconds-lastMovement>=25){int dx=point.X-previousPoint.Value.X,dy=point.Y-previousPoint.Value.Y;if(dx!=0||dy!=0){Append(new MacroStep{Kind=MacroStepKind.MouseMove,X=dx,Y=dy});previousPoint=point;lastMovement=timer.ElapsedMilliseconds;}}else if(!previousPoint.HasValue)previousPoint=point;}
            return false;
        }
        protected override void Dispose(bool disposing){if(disposing)RemoveFilter();base.Dispose(disposing);}
    }
}
