using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using Furycube.G9.Screen;
using Furycube.G9.Sensors;

internal sealed class G9ScreenEditor : UserControl {
    ScreenProject project = new ScreenProject();
    MonoFrame current;
    readonly SensorProvider sensors = new SensorProvider();
    SensorSnapshot snapshot;
    readonly PictureBox preview = new PictureBox { BackColor=Color.Black, SizeMode=PictureBoxSizeMode.CenterImage, Dock=DockStyle.Fill };
    readonly Label info = new Label { Dock=DockStyle.Bottom, Height=44, ForeColor=Color.DimGray };
    readonly Label imageName = new Label { AutoSize=true, Text="Картинка не выбрана" };
    readonly ComboBox preset = new ComboBox { DropDownStyle=ComboBoxStyle.DropDownList, Width=180 };
    readonly ComboBox composition = new ComboBox { DropDownStyle=ComboBoxStyle.DropDownList, Width=175 };
    readonly ComboBox resize = new ComboBox { DropDownStyle=ComboBoxStyle.DropDownList, Width=130 };
    readonly ComboBox newKind = new ComboBox { DropDownStyle=ComboBoxStyle.DropDownList, Width=145 };
    readonly TrackBar threshold = new TrackBar { Minimum=0,Maximum=255,Value=128,Width=150,Height=35,TickFrequency=64 };
    readonly CheckBox dither = new CheckBox { Text="Дизеринг",AutoSize=true }, invert = new CheckBox { Text="Инверсия",AutoSize=true };
    readonly ListBox widgets = new ListBox { Dock=DockStyle.Top, Height=130 };
    readonly PropertyGrid properties = new PropertyGrid { Dock=DockStyle.Fill, ToolbarVisible=false, HelpVisible=true };
    readonly Timer timer = new Timer { Interval=1000 };
    readonly Action<string> report;
    bool syncing, sampling;
    static readonly string[] Names={"Текст","Часы","Заряд мыши","DPI","Нагрузка CPU","ОЗУ","Нагрузка GPU","Температура CPU","Температура GPU"};
    sealed class Item {
        internal ScreenWidget Value;
        public override string ToString(){return Names[(int)Value.Kind]+"  ("+Value.X+", "+Value.Y+")";}
    }
    sealed class WidgetView {
        readonly ScreenWidget value;
        internal WidgetView(ScreenWidget widget){value=widget;}
        [Category("Содержимое"),DisplayName("Текст")] public string Text {get{return value.Text;}set{this.value.Text=value;}}
        [Category("Содержимое"),DisplayName("Формат часов")] public string Format {get{return value.Format;}set{this.value.Format=value;}}
        [Category("Расположение"),DisplayName("X")]public int X{get{return value.X;}set{this.value.X=value;}}
        [Category("Расположение"),DisplayName("Y")]public int Y{get{return value.Y;}set{this.value.Y=value;}}
        [Category("Расположение"),DisplayName("Ширина")]public int Width{get{return value.Width;}set{this.value.Width=value;}}
        [Category("Расположение"),DisplayName("Высота")]public int Height{get{return value.Height;}set{this.value.Height=value;}}
        [Category("Шрифт"),DisplayName("Размер в пикселях")]public float FontSize{get{return value.FontSize;}set{this.value.FontSize=value;}}
        [Category("Шрифт"),DisplayName("Жирный")]public bool Bold{get{return value.Bold;}set{this.value.Bold=value;}}
        [Category("Шрифт"),DisplayName("Подпись")]public bool ShowLabel{get{return value.ShowLabel;}set{this.value.ShowLabel=value;}}
        [Category("Шрифт"),DisplayName("Белый текст")]public bool Lit{get{return value.Lit;}set{this.value.Lit=value;}}
        [Category("Шрифт"),DisplayName("Очистить фон")]public bool ClearBackground{get{return value.ClearBackground;}set{this.value.ClearBackground=value;}}
    }
    internal G9ScreenEditor(Action<string> logger){
        report=logger;Dock=DockStyle.Fill;Padding=new Padding(10);BackColor=Color.FromArgb(244,246,249);
        var top=new FlowLayoutPanel { Dock=DockStyle.Top,Height=84,WrapContents=true,Padding=new Padding(0,3,0,3) };
        preset.Items.AddRange(new object[]{"Часы · заряд · DPI","CPU · GPU · ОЗУ","Температуры CPU/GPU","Крупные часы","Свой текст","Картинка"});
        composition.Items.AddRange(new object[]{"Только картинка","Только виджеты","Картинка + виджеты"});
        resize.Items.AddRange(new object[]{"Вписать","Обрезать","Растянуть"});
        top.Controls.Add(preset);top.Controls.Add(composition);
        Button(top,"Выбрать картинку",ChooseImage);Button(top,"Открыть проект",LoadProject);Button(top,"Сохранить проект",SaveProject);
        Button(top,"Экспорт PNG",ExportPng);Button(top,"Экспорт кадра",ExportFrame);
        var upload=new Button {Text="На OLED",AutoSize=true,Enabled=false};
        new ToolTip().SetToolTip(upload,"Передача на физический экран будет доступна только после проверки протокола и прошивки G9.");top.Controls.Add(upload);
        var images=new FlowLayoutPanel { Dock=DockStyle.Top,Height=45,WrapContents=false };
        images.Controls.Add(resize);images.Controls.Add(new Label{Text="Порог",AutoSize=true,Padding=new Padding(8,8,0,0)});images.Controls.Add(threshold);images.Controls.Add(dither);images.Controls.Add(invert);images.Controls.Add(imageName);
        var split=new SplitContainer { Size=new Size(1040,480),Dock=DockStyle.Fill,Orientation=Orientation.Vertical,Panel1MinSize=400,Panel2MinSize=260,SplitterDistance=700,FixedPanel=FixedPanel.Panel2 };
        var screenBox=new Panel { Dock=DockStyle.Fill,Padding=new Padding(12),BackColor=Color.FromArgb(22,27,35) };
        var hint=new Label { Text="120 × 28 · 1 бит/пиксель · предпросмотр",ForeColor=Color.Gainsboro,Dock=DockStyle.Top,Height=30 };
        screenBox.Controls.Add(preview);screenBox.Controls.Add(hint);split.Panel1.Controls.Add(screenBox);
        var edit=new Panel { Dock=DockStyle.Fill,Padding=new Padding(8) };
        var add=new FlowLayoutPanel { Dock=DockStyle.Top,Height=65 };
        newKind.Items.AddRange(Names);newKind.SelectedIndex=0;add.Controls.Add(newKind);
        Button(add,"+",delegate{project.Widgets.Add(new ScreenWidget{Kind=(WidgetKind)newKind.SelectedIndex,X=0,Y=14,Width=120,Height=14,FontSize=9});RefreshWidgets(project.Widgets.Count-1);Render();});
        Button(add,"Удалить",delegate{int index=widgets.SelectedIndex;if(index>=0){project.Widgets.RemoveAt(index);RefreshWidgets(Math.Min(index,project.Widgets.Count-1));Render();}});
        edit.Controls.Add(properties);edit.Controls.Add(widgets);edit.Controls.Add(add);split.Panel2.Controls.Add(edit);
        info.Text="Предпросмотр работает на ПК. Прошивка для загрузки своих кадров на G9 ещё не установлена.";
        Controls.Add(split);Controls.Add(images);Controls.Add(top);Controls.Add(info);
        preset.SelectedIndexChanged+=delegate{if(!syncing){ApplyPreset(preset.SelectedIndex);SyncControls();Render();}};
        composition.SelectedIndexChanged+=delegate{if(!syncing&&composition.SelectedIndex>=0){project.Composition=(CompositionMode)composition.SelectedIndex;Render();}};
        resize.SelectedIndexChanged+=delegate{if(!syncing&&resize.SelectedIndex>=0){project.ImageOptions.Resize=(ResizeMode)resize.SelectedIndex;Render();}};
        threshold.ValueChanged+=delegate{if(!syncing){project.ImageOptions.Threshold=threshold.Value;Render();}};
        dither.CheckedChanged+=delegate{if(!syncing){project.ImageOptions.Dither=dither.Checked;Render();}};
        invert.CheckedChanged+=delegate{if(!syncing){project.ImageOptions.Invert=invert.Checked;Render();}};
        widgets.SelectedIndexChanged+=delegate{Item selected=widgets.SelectedItem as Item;properties.SelectedObject=selected==null?null:new WidgetView(selected.Value);};
        properties.PropertyValueChanged+=delegate{Render();};
        timer.Tick+=async delegate{await RefreshSensors();};
        SyncControls();preset.SelectedIndex=0;timer.Start();Disposed+=delegate{timer.Dispose();sensors.Dispose();if(preview.Image!=null)preview.Image.Dispose();};
    }
    static void Button(Control parent,string label,Action click){var button=new Button{Text=label,AutoSize=true,Height=30};button.Click+=delegate{click();};parent.Controls.Add(button);}
    WidgetContext Context(){return new WidgetContext {Now=DateTime.Now,CpuPercent=snapshot==null?null:snapshot.CpuUsagePercent,RamPercent=snapshot==null?null:snapshot.RamUsagePercent,
        GpuPercent=snapshot==null?null:snapshot.GpuUsagePercent,CpuTemperatureC=snapshot==null?null:snapshot.CpuTemperatureCelsius,GpuTemperatureC=snapshot==null?null:snapshot.GpuTemperatureCelsius};}
    async Task RefreshSensors(){if(sampling||IsDisposed)return;sampling=true;try{snapshot=await Task.Run(()=>sensors.Sample());if(!IsDisposed)Render();}catch(Exception e){if(!IsDisposed)info.Text="Датчики: "+e.Message;}finally{sampling=false;}}
    void ApplyPreset(int index){
        project.Composition=CompositionMode.Widgets;project.Widgets.Clear();
        if(index==0)project.Widgets=WidgetRenderer.CreateDefault(120,28);
        else if(index==1){Add(WidgetKind.CpuPercent,0,0,60,14);Add(WidgetKind.GpuPercent,60,0,60,14);Add(WidgetKind.RamPercent,0,14,120,14);}
        else if(index==2){Add(WidgetKind.CpuTemperatureC,0,0,60,14);Add(WidgetKind.GpuTemperatureC,60,0,60,14);Add(WidgetKind.Clock,0,14,120,14);}
        else if(index==3){Add(WidgetKind.Clock,0,0,120,28);project.Widgets[0].FontSize=22;project.Widgets[0].ShowLabel=false;}
        else if(index==4){Add(WidgetKind.Text,0,0,120,28);project.Widgets[0].Text="FURYCUBE";project.Widgets[0].FontSize=18;}
        else project.Composition=CompositionMode.Image;
    }
    void Add(WidgetKind kind,int x,int y,int width,int height){project.Widgets.Add(new ScreenWidget{Kind=kind,X=x,Y=y,Width=width,Height=height,FontSize=9,ShowLabel=true});}
    void RefreshWidgets(int selected){widgets.Items.Clear();foreach(var widget in project.Widgets)widgets.Items.Add(new Item{Value=widget});if(selected>=0&&selected<widgets.Items.Count)widgets.SelectedIndex=selected;else properties.SelectedObject=null;}
    void SyncControls(){syncing=true;composition.SelectedIndex=(int)project.Composition;resize.SelectedIndex=(int)project.ImageOptions.Resize;threshold.Value=project.ImageOptions.Threshold;dither.Checked=project.ImageOptions.Dither;invert.Checked=project.ImageOptions.Invert;
        imageName.Text=String.IsNullOrEmpty(project.ImageFile)?"Картинка не выбрана":Path.GetFileName(project.ImageFile);RefreshWidgets(project.Widgets.Count>0?0:-1);syncing=false;}
    void Render(){if(IsDisposed)return;try{current=project.Render(Context());Image old=preview.Image;preview.Image=current.ToBitmap(5);if(old!=null)old.Dispose();
        info.Text="Предпросмотр 120 × 28. Передача на физический OLED пока недоступна."+(snapshot!=null&&!String.IsNullOrEmpty(snapshot.CpuTemperatureSourceName)?"\nТемпература CPU: "+snapshot.CpuTemperatureSourceName:"");
    }catch(Exception e){info.Text="Не удалось отрисовать: "+e.Message;}}
    void ChooseImage(){using(var dialog=new OpenFileDialog{Filter="Картинки|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff"})if(dialog.ShowDialog(this)==DialogResult.OK){project.ImageFile=dialog.FileName;project.Composition=CompositionMode.Image;SyncControls();Render();}}
    void SaveProject(){using(var dialog=new SaveFileDialog{Filter="Проект экрана|*.g9screen.json",FileName="screen.g9screen.json"})if(dialog.ShowDialog(this)==DialogResult.OK)try{project.Save(dialog.FileName);SyncControls();report("Сохранён проект экрана: "+dialog.FileName);}catch(Exception e){MessageBox.Show(this,e.Message,"Сохранение",MessageBoxButtons.OK,MessageBoxIcon.Error);}}
    void LoadProject(){using(var dialog=new OpenFileDialog{Filter="Проект экрана|*.json"})if(dialog.ShowDialog(this)==DialogResult.OK)try{project=ScreenProject.Load(dialog.FileName);SyncControls();Render();report("Открыт проект экрана: "+dialog.FileName);}catch(Exception e){MessageBox.Show(this,e.Message,"Открытие проекта",MessageBoxButtons.OK,MessageBoxIcon.Error);}}
    void ExportPng(){Render();if(current==null)return;using(var dialog=new SaveFileDialog{Filter="PNG|*.png",FileName="oled.png"})if(dialog.ShowDialog(this)==DialogResult.OK)using(var image=current.ToBitmap())image.Save(dialog.FileName,System.Drawing.Imaging.ImageFormat.Png);}
    void ExportFrame(){Render();if(current==null)return;using(var dialog=new SaveFileDialog{Filter="Кадр OLED, 1bpp|*.bin",FileName="oled-120x28.bin"})if(dialog.ShowDialog(this)==DialogResult.OK)File.WriteAllBytes(dialog.FileName,current.Buffer);}
}
