using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Furycube.G9.Screen;
using Furycube.G9.Sensors;
using Furycube.G9.Oled;

internal sealed class G9ScreenEditor : UserControl {
    ScreenProject project=new ScreenProject();
    MonoFrame current;
    readonly SensorProvider sensors=new SensorProvider();
    readonly G9TelemetryReader telemetry=new G9TelemetryReader();
    SensorSnapshot snapshot;
    readonly PictureBox preview=new PictureBox {BackColor=Color.FromArgb(18,23,30),SizeMode=PictureBoxSizeMode.CenterImage,Dock=DockStyle.Fill};
    readonly Label info=new Label {Dock=DockStyle.Bottom,Height=44,ForeColor=Color.DimGray,Padding=new Padding(0,5,0,0)};
    readonly Label canvasCaption=new Label {Dock=DockStyle.Top,Height=30,ForeColor=Color.Gainsboro};
    readonly Label autosaveStatus=new Label {AutoSize=true,ForeColor=Color.DimGray,Padding=new Padding(8,7,0,0)};
    readonly Label imageName=new Label {AutoEllipsis=true,Dock=DockStyle.Fill,Text="Картинка не выбрана",Padding=new Padding(0,6,0,0)};
    readonly ComboBox preset=new ComboBox {DropDownStyle=ComboBoxStyle.DropDownList,Width=210};
    readonly ComboBox composition=new ComboBox {DropDownStyle=ComboBoxStyle.DropDownList,Width=185};
    readonly ComboBox resize=new ComboBox {DropDownStyle=ComboBoxStyle.DropDownList,Dock=DockStyle.Fill};
    readonly TrackBar threshold=new TrackBar {Minimum=0,Maximum=255,Value=128,Dock=DockStyle.Fill,Height=28,AutoSize=false,TickFrequency=64};
    readonly Label thresholdValue=new Label {AutoSize=true,Text="128",Padding=new Padding(3,5,0,0)};
    readonly CheckBox dither=new CheckBox {Text="Сгладить градации (дизеринг)",AutoSize=true};
    readonly CheckBox invert=new CheckBox {Text="Поменять белое и чёрное",AutoSize=true};
    readonly ListBox layers=new ListBox {Dock=DockStyle.Fill,IntegralHeight=false};
    readonly ComboBox widgetType=new ComboBox {DropDownStyle=ComboBoxStyle.DropDownList,Dock=DockStyle.Fill};
    readonly TextBox text=new TextBox {Multiline=true,Height=54,Dock=DockStyle.Fill,MaxLength=4096,ScrollBars=ScrollBars.Vertical};
    readonly ComboBox clockFormat=new ComboBox {DropDownStyle=ComboBoxStyle.DropDownList,Dock=DockStyle.Fill};
    readonly CheckBox bold=new CheckBox {Text="Жирный шрифт",AutoSize=true};
    readonly CheckBox showLabel=new CheckBox {Text="Подписывать значение (CPU, DPI…)",AutoSize=true};
    readonly CheckBox lit=new CheckBox {Text="Белый текст",AutoSize=true};
    readonly CheckBox clearBackground=new CheckBox {Text="Очистить фон под виджетом",AutoSize=true};
    readonly Label selectionHint=new Label {AutoSize=true,MaximumSize=new Size(300,0),ForeColor=Color.DimGray,Margin=new Padding(3,5,3,9)};
    readonly Label textLabel=new Label {Text="Надпись",AutoSize=true};
    readonly Label formatLabel=new Label {Text="Формат даты и времени",AutoSize=true};
    readonly Timer timer=new Timer {Interval=1000};
    readonly Timer autosaveTimer=new Timer {Interval=500};
    readonly ToolTip tips=new ToolTip {AutoPopDelay=12000,InitialDelay=400,ReshowDelay=100};
    readonly Action<string> report;
    readonly Func<byte[],Task<string>> sendFrame;
    readonly Func<Task<OledRuntimeStatus>> readRuntimeStatus;
    OledRuntimeStatus runtimeStatus;
    Button upload;
    readonly CheckBox live=new CheckBox {Text="Обновлять OLED каждую секунду",AutoSize=true};
    bool uploading;
    string oledNote="Передача требует прошивки OL2. Штатная прошивка будет проверена до отправки кадра; установка прошивки здесь не выполняется.";
    readonly GeometrySlider xSlider=new GeometrySlider("X — отступ слева"),ySlider=new GeometrySlider("Y — отступ сверху"),widthSlider=new GeometrySlider("Ширина блока"),heightSlider=new GeometrySlider("Высота блока"),fontSlider=new GeometrySlider("Размер шрифта");
    readonly TabControl editingTabs=new TabControl {Dock=DockStyle.Fill};
    readonly string autosaveFile;
    bool syncing,sampling,syncingGeometry,dragging,dirty,disposedEditor;
    int previewScale=5;
    Point dragOffset;
    static readonly string[] Names={"Текст","Часы","Заряд мыши","DPI — значение чувствительности","CPU — загрузка процессора","ОЗУ — занятая память","GPU — загрузка видеокарты","Температура процессора","Температура видеокарты","DPI — номер уровня (1–8)"};
    static readonly string[] Formats={"HH:mm","HH:mm:ss","dd.MM","d MMMM"};
    static readonly string[] FormatNames={"13:45 — часы и минуты","13:45:08 — с секундами","09.11 — день и месяц","9 ноября — дата словами"};
    sealed class LayerItem {
        internal bool Image;
        internal ScreenWidget Widget;
        public override string ToString(){return Image?"Картинка":Names[(int)Widget.Kind];}
    }
    sealed class ClockChoice {
        internal string Value,Caption;
        public override string ToString(){return Caption;}
    }
    sealed class GeometrySlider:Panel {
        internal readonly TrackBar Track=new TrackBar {Dock=DockStyle.Bottom,Height=24,AutoSize=false,TickStyle=TickStyle.None,SmallChange=1,LargeChange=5};
        readonly Label caption=new Label {Dock=DockStyle.Top,Height=20};
        readonly string title;
        internal GeometrySlider(string value){title=value;Height=49;Dock=DockStyle.Top;Margin=new Padding(0,1,0,3);Controls.Add(Track);Controls.Add(caption);}
        internal void Sync(int minimum,int maximum,int value,bool available){maximum=Math.Max(minimum,maximum);Track.Minimum=minimum;Track.Maximum=maximum;Track.Value=Math.Max(minimum,Math.Min(maximum,value));Track.Enabled=available&&maximum>minimum;caption.Text=title+": "+Track.Value+" px";}
    }
    internal G9ScreenEditor(Action<string> logger,Func<byte[],Task<string>> frameSender=null,Func<Task<OledRuntimeStatus>> runtimeStatusReader=null){
        report=logger;sendFrame=frameSender;readRuntimeStatus=runtimeStatusReader;Dock=DockStyle.Fill;Padding=new Padding(10);BackColor=Color.FromArgb(244,246,249);
        autosaveFile=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"FURYCUBE-G9-PRO","editor","autosave.g9screen.json");
        BuildLayout();BindEvents();bool restored=RestoreAutosave();
        syncing=true;try{preset.SelectedIndex=restored?-1:0;}finally{syncing=false;}
        SyncControls(null,false);Render();timer.Start();telemetry.Start();
        Disposed+=delegate{if(disposedEditor)return;disposedEditor=true;timer.Stop();autosaveTimer.Stop();SaveAutosave();timer.Dispose();autosaveTimer.Dispose();tips.Dispose();telemetry.Dispose();sensors.Dispose();if(preview.Image!=null)preview.Image.Dispose();};
    }
    void BuildLayout(){
        preset.Items.AddRange(new object[]{"Часы · заряд · DPI","CPU · GPU · ОЗУ","Температуры CPU/GPU","Крупные часы","Свой текст","Картинка"});
        composition.Items.AddRange(new object[]{"Только картинка","Только виджеты","Картинка + виджеты"});
        resize.Items.AddRange(new object[]{"Вписать целиком","Заполнить с обрезкой","Растянуть"});widgetType.Items.AddRange(Names);
        for(int i=0;i<Formats.Length;i++)clockFormat.Items.Add(new ClockChoice {Value=Formats[i],Caption=FormatNames[i]});
        var toolbar=new TableLayoutPanel {Dock=DockStyle.Top,AutoSize=true,ColumnCount=1,Padding=new Padding(0,0,0,6)};
        var first=Flow();first.Controls.Add(Caption("Макет:"));first.Controls.Add(preset);
        AddButton(first,"Открыть проект…",LoadProject,"Открыть сохранённый макет вместе с его картинкой.");
        AddButton(first,"Сохранить проект…",SaveProject,"Сохранить макет и копию картинки в переносимую папку.");first.Controls.Add(autosaveStatus);
        var second=Flow();second.Controls.Add(Caption("На экране:"));second.Controls.Add(composition);
        AddButton(second,"Выбрать картинку…",ChooseImage,"PNG, JPG, BMP, GIF или TIFF. Картинку можно двигать и изменять в размерах.");
        AddButton(second,"Экспорт PNG",ExportPng,"Сохранить чёрно-белый кадр без оранжевой рамки выделения.");
        AddButton(second,"Экспорт .bin",ExportFrame,"Сохранить упакованный кадр для дальнейшей работы с устройством.");
        upload=AddButton(second,"На OLED",async delegate{await UploadFrame();},"Проверить поддержку OL2 и передать текущий кадр по кабелю. На штатной прошивке запись будет отклонена. Прошивка мыши не меняется.");upload.Enabled=sendFrame!=null;
        var third=Flow();third.Controls.Add(live);live.Enabled=sendFrame!=null;tips.SetToolTip(live,"Для живых часов и показателей ПК. HUB должен работать. При ошибке обновление выключается; после запуска программы само не включается.");
        toolbar.Controls.Add(first);toolbar.Controls.Add(second);toolbar.Controls.Add(third);
        var split=new SplitContainer {Size=new Size(1040,440),Dock=DockStyle.Fill,Orientation=Orientation.Vertical,Panel1MinSize=300,Panel2MinSize=300,SplitterDistance=670,FixedPanel=FixedPanel.Panel2};
        var left=new Panel {Dock=DockStyle.Fill};
        var stage=new Panel {Dock=DockStyle.Fill,Padding=new Padding(12),BackColor=Color.FromArgb(22,27,35)};
        var moveHint=new Label {Dock=DockStyle.Bottom,Height=36,ForeColor=Color.Silver,Text="Выбери слой справа и перетащи рамку.\nЧасть блока за краем экрана будет обрезана."};
        stage.Controls.Add(preview);stage.Controls.Add(canvasCaption);stage.Controls.Add(moveHint);
        var artwork=new GroupBox {Text="Картинка",Dock=DockStyle.Bottom,Height=153,Padding=new Padding(8)};
        var imageFields=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=3,RowCount=4};
        imageFields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,112));imageFields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));imageFields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,48));
        imageFields.RowStyles.Add(new RowStyle(SizeType.Absolute,28));imageFields.RowStyles.Add(new RowStyle(SizeType.Absolute,30));imageFields.RowStyles.Add(new RowStyle(SizeType.Absolute,34));imageFields.RowStyles.Add(new RowStyle(SizeType.Percent,100));
        imageFields.Controls.Add(imageName,0,0);imageFields.SetColumnSpan(imageName,3);
        imageFields.Controls.Add(Caption("Заполнение:"),0,1);imageFields.Controls.Add(resize,1,1);imageFields.SetColumnSpan(resize,2);
        imageFields.Controls.Add(Caption("Порог белого:"),0,2);imageFields.Controls.Add(threshold,1,2);imageFields.Controls.Add(thresholdValue,2,2);
        var imageChecks=Flow();imageChecks.WrapContents=true;imageChecks.Controls.Add(dither);imageChecks.Controls.Add(invert);imageFields.Controls.Add(imageChecks,0,3);imageFields.SetColumnSpan(imageChecks,3);
        artwork.Controls.Add(imageFields);left.Controls.Add(stage);left.Controls.Add(artwork);split.Panel1.Controls.Add(left);
        var right=new Panel {Dock=DockStyle.Fill,Padding=new Padding(8,0,0,0)};
        var layerGroup=new GroupBox {Text="Слои экрана",Dock=DockStyle.Top,Height=151,Padding=new Padding(8)};
        var layerButtons=Flow();layerButtons.Dock=DockStyle.Bottom;layerButtons.Height=36;
        AddButton(layerButtons,"Добавить виджет",AddWidget,"Добавить текстовый блок, затем выбрать, что в нём показывать.");
        AddButton(layerButtons,"Удалить слой",RemoveLayer,"Удалить выбранную картинку или виджет из макета.");layerGroup.Controls.Add(layers);layerGroup.Controls.Add(layerButtons);
        var contentTab=new TabPage("Содержимое") {Padding=new Padding(8),AutoScroll=true};
        var contentFields=new TableLayoutPanel {Dock=DockStyle.Top,AutoSize=true,ColumnCount=1};contentFields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
        AddField(contentFields,new Label {Text="Что показывать",AutoSize=true});AddField(contentFields,widgetType);AddField(contentFields,selectionHint);
        AddField(contentFields,textLabel);AddField(contentFields,text);AddField(contentFields,formatLabel);AddField(contentFields,clockFormat);
        var style=new FlowLayoutPanel {Dock=DockStyle.Fill,AutoSize=true,FlowDirection=FlowDirection.TopDown,WrapContents=false,Margin=new Padding(0,12,0,3)};
        style.Controls.Add(bold);style.Controls.Add(showLabel);style.Controls.Add(lit);style.Controls.Add(clearBackground);AddField(contentFields,style);contentTab.Controls.Add(contentFields);
        var geometryTab=new TabPage("Положение и размер") {Padding=new Padding(8),AutoScroll=true};
        var geometryFields=new TableLayoutPanel {Dock=DockStyle.Top,AutoSize=true,ColumnCount=1};geometryFields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
        AddField(geometryFields,new Label {AutoSize=true,MaximumSize=new Size(300,0),Text="Пиксели экрана. Положение не зависит от размера: блок можно сдвинуть за край."});
        foreach(var slider in new[]{xSlider,ySlider,widthSlider,heightSlider,fontSlider})AddField(geometryFields,slider);
        var alignment=Flow();alignment.WrapContents=true;
        AddButton(alignment,"По центру",CenterLayer,"Расположить выбранный блок в центре экрана.");AddButton(alignment,"Во всю ширину",FillWidth,"Занять ширину экрана, сохранив высоту блока.");AddButton(alignment,"Во весь экран",FillCanvas,"Разместить выбранный блок в области всего экрана.");AddField(geometryFields,alignment);geometryTab.Controls.Add(geometryFields);
        editingTabs.TabPages.Add(contentTab);editingTabs.TabPages.Add(geometryTab);right.Controls.Add(editingTabs);right.Controls.Add(layerGroup);split.Panel2.Controls.Add(right);
        Controls.Add(split);Controls.Add(toolbar);Controls.Add(info);
        tips.SetToolTip(resize,"Вписать — сохранить пропорции целиком. Заполнить — сохранить пропорции и обрезать края. Растянуть — изменить пропорции под размер блока.");
        tips.SetToolTip(threshold,"Пиксели светлее порога становятся белыми, остальные чёрными. На OLED доступно только два цвета.");
        tips.SetToolTip(dither,"У экрана 1 бит на пиксель. Дизеринг изображает серые оттенки узором из чёрных и белых точек.");tips.SetToolTip(invert,"Меняет местами белые и чёрные пиксели картинки.");
        tips.SetToolTip(showLabel,"Добавляет понятную подпись к числу: например CPU 42% или DPI 1600.");tips.SetToolTip(lit,"Включено — белые буквы. Выключено — чёрные буквы, которые видны на белом фоне.");
        tips.SetToolTip(clearBackground,"Убирает картинку и предыдущие слои под прямоугольником этого виджета, чтобы надпись читалась.");tips.SetToolTip(text,"Короткая надпись. Если она не помещается, уменьши шрифт или увеличь блок на вкладке «Положение и размер».");
        foreach(var slider in new[]{xSlider,ySlider,widthSlider,heightSlider,fontSlider})tips.SetToolTip(slider.Track,"Значение в пикселях физического экрана. Размер и положение изменяются независимо.");
    }
    static FlowLayoutPanel Flow(){return new FlowLayoutPanel {AutoSize=true,WrapContents=false,Margin=new Padding(0),Padding=new Padding(0,2,0,2)};}
    static Label Caption(string value){return new Label {Text=value,AutoSize=true,Padding=new Padding(0,7,4,0),Margin=new Padding(0,0,3,0)};}
    static void AddField(TableLayoutPanel parent,Control field){int row=parent.RowCount++;parent.RowStyles.Add(new RowStyle(SizeType.AutoSize));field.Anchor=AnchorStyles.Left|AnchorStyles.Right;field.Margin=new Padding(0,3,0,4);parent.Controls.Add(field,0,row);}
    Button AddButton(Control parent,string caption,Action action,string hint){var button=new Button {Text=caption,AutoSize=true,Height=29,Margin=new Padding(3,0,3,0),Padding=new Padding(4,1,4,1)};button.Click+=delegate{action();};parent.Controls.Add(button);tips.SetToolTip(button,hint);return button;}
    void BindEvents(){
        preset.SelectedIndexChanged+=delegate{if(syncing||preset.SelectedIndex<0)return;ApplyPreset(preset.SelectedIndex);SyncControls(null,false);Changed();};
        composition.SelectedIndexChanged+=delegate{if(!syncing&&composition.SelectedIndex>=0){project.Composition=(CompositionMode)composition.SelectedIndex;Changed();}};
        resize.SelectedIndexChanged+=delegate{if(!syncing&&resize.SelectedIndex>=0){project.ImageOptions.Resize=(ResizeMode)resize.SelectedIndex;Changed();}};
        threshold.ValueChanged+=delegate{thresholdValue.Text=threshold.Value.ToString();if(!syncing){project.ImageOptions.Threshold=threshold.Value;Changed();}};
        dither.CheckedChanged+=delegate{if(!syncing){project.ImageOptions.Dither=dither.Checked;Changed();}};invert.CheckedChanged+=delegate{if(!syncing){project.ImageOptions.Invert=invert.Checked;Changed();}};
        layers.SelectedIndexChanged+=delegate{SyncSelection();preview.Invalidate();};
        widgetType.SelectedIndexChanged+=delegate{ScreenWidget w=SelectedWidget();if(syncing||w==null||widgetType.SelectedIndex<0)return;w.Kind=(WidgetKind)widgetType.SelectedIndex;RefreshLayers(w,false);SyncSelection();Changed();};
        text.TextChanged+=delegate{ScreenWidget w=SelectedWidget();if(!syncing&&w!=null&&w.Kind==WidgetKind.Text){w.Text=text.Text;Changed();}};
        clockFormat.SelectedIndexChanged+=delegate{ScreenWidget w=SelectedWidget();ClockChoice choice=clockFormat.SelectedItem as ClockChoice;if(!syncing&&w!=null&&w.Kind==WidgetKind.Clock&&choice!=null){w.Format=choice.Value;Changed();}};
        bold.CheckedChanged+=delegate{WidgetStyleChanged();};showLabel.CheckedChanged+=delegate{WidgetStyleChanged();};lit.CheckedChanged+=delegate{WidgetStyleChanged();};clearBackground.CheckedChanged+=delegate{WidgetStyleChanged();};
        foreach(var slider in new[]{xSlider,ySlider,widthSlider,heightSlider,fontSlider})slider.Track.ValueChanged+=delegate(object sender,EventArgs e){GeometryChanged((TrackBar)sender);};
        preview.Paint+=DrawSelection;preview.MouseDown+=StartDrag;preview.MouseMove+=MoveDrag;preview.MouseUp+=delegate{dragging=false;preview.Capture=false;};preview.Resize+=delegate{Render();};
        timer.Tick+=async delegate{await RefreshSensors();if(!IsDisposed&&!disposedEditor&&live.Checked)await UploadFrame(true);};autosaveTimer.Tick+=delegate{autosaveTimer.Stop();SaveAutosave();};
        live.CheckedChanged+=async delegate{if(live.Checked&&!syncing)await UploadFrame();};
    }
    LayerItem SelectedLayer(){return layers.SelectedItem as LayerItem;}
    ScreenWidget SelectedWidget(){LayerItem selected=SelectedLayer();return selected==null?null:selected.Widget;}
    bool ImageSelected(){LayerItem selected=SelectedLayer();return selected!=null&&selected.Image;}
    int ImageWidth(){return project.ImageWidth==0?project.Width:project.ImageWidth;}
    int ImageHeight(){return project.ImageHeight==0?project.Height:project.ImageHeight;}
    Rectangle LayerBounds(){ScreenWidget w=SelectedWidget();return ImageSelected()?new Rectangle(project.ImageX,project.ImageY,ImageWidth(),ImageHeight()):w==null?Rectangle.Empty:new Rectangle(w.X,w.Y,w.Width,w.Height);}
    void RefreshLayers(ScreenWidget selected,bool image){
        bool previous=syncing;syncing=true;
        try{layers.BeginUpdate();layers.Items.Clear();int index=-1;if(!String.IsNullOrEmpty(project.ImageFile)){layers.Items.Add(new LayerItem {Image=true});if(image)index=0;}
            foreach(var widget in project.Widgets){int currentIndex=layers.Items.Add(new LayerItem {Widget=widget});if(Object.ReferenceEquals(widget,selected))index=currentIndex;}
            if(index<0&&project.Widgets.Count>0)index=String.IsNullOrEmpty(project.ImageFile)?0:1;if(index<0&&layers.Items.Count>0)index=0;layers.SelectedIndex=index;
        }finally{layers.EndUpdate();syncing=previous;}SyncSelection();
    }
    void SyncControls(ScreenWidget selected,bool image){
        bool previous=syncing;syncing=true;
        try{composition.SelectedIndex=(int)project.Composition;resize.SelectedIndex=(int)project.ImageOptions.Resize;threshold.Value=project.ImageOptions.Threshold;thresholdValue.Text=threshold.Value.ToString();dither.Checked=project.ImageOptions.Dither;invert.Checked=project.ImageOptions.Invert;
            bool hasImage=!String.IsNullOrEmpty(project.ImageFile);resize.Enabled=threshold.Enabled=dither.Enabled=invert.Enabled=hasImage;imageName.Text=hasImage?Path.GetFileName(project.ImageFile):"Картинка не выбрана — добавь её кнопкой сверху";RefreshLayers(selected,image);
        }finally{syncing=previous;}
    }
    void SyncSelection(){
        bool previous=syncing;syncing=true;ScreenWidget w=SelectedWidget();
        try{bool selected=w!=null;widgetType.Enabled=selected;widgetType.SelectedIndex=selected?(int)w.Kind:-1;bool isText=selected&&w.Kind==WidgetKind.Text,isClock=selected&&w.Kind==WidgetKind.Clock;
            text.Enabled=textLabel.Enabled=isText;clockFormat.Enabled=formatLabel.Enabled=isClock;text.Text=isText?w.Text??"":"";
            while(clockFormat.Items.Count>Formats.Length)clockFormat.Items.RemoveAt(clockFormat.Items.Count-1);int clockIndex=-1;
            if(selected){for(int i=0;i<Formats.Length;i++)if(Formats[i]==w.Format)clockIndex=i;if(clockIndex<0&&!String.IsNullOrEmpty(w.Format))clockIndex=clockFormat.Items.Add(new ClockChoice {Value=w.Format,Caption="Свой формат: "+w.Format});}
            clockFormat.SelectedIndex=clockIndex<0?0:clockIndex;bold.Enabled=lit.Enabled=clearBackground.Enabled=selected;showLabel.Enabled=selected&&!isText&&!isClock;
            bold.Checked=selected&&w.Bold;showLabel.Checked=selected&&w.ShowLabel;lit.Checked=selected&&w.Lit;clearBackground.Checked=selected&&w.ClearBackground;
            selectionHint.Text=ImageSelected()?"Выбрана картинка. Двигай рамку или открой «Положение и размер». Обработка картинки находится под предпросмотром.":w==null?"Выбери слой в списке или добавь виджет.":Explanation(w.Kind);
        }finally{syncing=previous;}SyncGeometry();
    }
    static string Explanation(WidgetKind kind){switch(kind){
        case WidgetKind.Text:return "Короткая надпись. Её размер и положение задаются на соседней вкладке.";
        case WidgetKind.Clock:return "Время и дата компьютера. По умолчанию используются 24 часа и русский язык.";
        case WidgetKind.Battery:return "Уровень заряда мыши. Если устройство не передаёт значение, показывается прочерк.";
        case WidgetKind.Dpi:return "Настроенное число точек на дюйм. Считывается из мыши перед передачей на OLED, если прошивка поддерживает этот ответ. Без свежего ответа показывается прочерк. Номер уровня — отдельный виджет.";
        case WidgetKind.DpiStage:return "Последний уровень DPI (1–8), сообщённый мышью. Это номер настройки, а не число точек на дюйм. При уведомлении о смене профиля значение сбрасывается до нового сообщения.";
        case WidgetKind.CpuPercent:return "CPU — процессор компьютера. Показывается его текущая загрузка в процентах.";
        case WidgetKind.RamPercent:return "ОЗУ (RAM) — оперативная память. Показывается доля занятой памяти.";
        case WidgetKind.GpuPercent:return "GPU — видеокарта. Показывается загрузка графического процессора.";
        case WidgetKind.CpuTemperatureC:return "Температура процессора в °C. Если датчик недоступен, вместо числа показывается —.";
        default:return "Температура видеокарты в °C. Если датчик недоступен, вместо числа показывается —.";
    }}
    void WidgetStyleChanged(){if(syncing)return;ScreenWidget w=SelectedWidget();if(w==null)return;w.Bold=bold.Checked;w.ShowLabel=showLabel.Checked;w.Lit=lit.Checked;w.ClearBackground=clearBackground.Checked;Changed();}
    void SyncGeometry(){
        Rectangle bounds=LayerBounds();ScreenWidget w=SelectedWidget();bool available=SelectedLayer()!=null;syncingGeometry=true;
        try{xSlider.Sync(Math.Min(0,bounds.X),Math.Max(project.Width-1,bounds.X),bounds.X,available);ySlider.Sync(Math.Min(0,bounds.Y),Math.Max(project.Height-1,bounds.Y),bounds.Y,available);
            widthSlider.Sync(1,Math.Max(project.Width,bounds.Width),Math.Max(1,bounds.Width),available);heightSlider.Sync(1,Math.Max(project.Height,bounds.Height),Math.Max(1,bounds.Height),available);
            fontSlider.Sync(4,Math.Max(4,Math.Max(project.Height,w==null?9:(int)Math.Ceiling(w.FontSize))),w==null?9:(int)Math.Round(w.FontSize),w!=null);
        }finally{syncingGeometry=false;}
    }
    void GeometryChanged(TrackBar source){
        if(syncingGeometry||syncing||SelectedLayer()==null)return;
        if(ImageSelected()){if(source==xSlider.Track)project.ImageX=source.Value;else if(source==ySlider.Track)project.ImageY=source.Value;else if(source==widthSlider.Track)project.ImageWidth=source.Value;else if(source==heightSlider.Track)project.ImageHeight=source.Value;}
        else{ScreenWidget w=SelectedWidget();if(w==null)return;if(source==xSlider.Track)w.X=source.Value;else if(source==ySlider.Track)w.Y=source.Value;else if(source==widthSlider.Track)w.Width=source.Value;else if(source==heightSlider.Track)w.Height=source.Value;else if(source==fontSlider.Track)w.FontSize=source.Value;}
        SyncGeometry();Changed();
    }
    void SetBounds(Rectangle bounds){if(ImageSelected()){project.ImageX=bounds.X;project.ImageY=bounds.Y;project.ImageWidth=bounds.Width;project.ImageHeight=bounds.Height;}else{ScreenWidget w=SelectedWidget();if(w==null)return;w.X=bounds.X;w.Y=bounds.Y;w.Width=bounds.Width;w.Height=bounds.Height;}SyncGeometry();Changed();}
    void CenterLayer(){if(SelectedLayer()==null)return;Rectangle b=LayerBounds();b.X=Math.Max(0,(project.Width-b.Width)/2);b.Y=Math.Max(0,(project.Height-b.Height)/2);SetBounds(b);}
    void FillWidth(){if(SelectedLayer()==null)return;Rectangle b=LayerBounds();b.X=0;b.Width=project.Width;SetBounds(b);}
    void FillCanvas(){if(SelectedLayer()!=null)SetBounds(new Rectangle(0,0,project.Width,project.Height));}
    Point CanvasOrigin(){return preview.Image==null?Point.Empty:new Point((preview.ClientSize.Width-preview.Image.Width)/2,(preview.ClientSize.Height-preview.Image.Height)/2);}
    Point CanvasPoint(Point location){Point origin=CanvasOrigin();return new Point((int)Math.Floor((location.X-origin.X)/(double)previewScale),(int)Math.Floor((location.Y-origin.Y)/(double)previewScale));}
    void DrawSelection(object sender,PaintEventArgs e){if(SelectedLayer()==null||preview.Image==null)return;Rectangle b=LayerBounds();Point origin=CanvasOrigin();var state=e.Graphics.Save();try{e.Graphics.SetClip(new Rectangle(origin,preview.Image.Size));using(var pen=new Pen(Color.FromArgb(255,147,65),2)){pen.DashStyle=System.Drawing.Drawing2D.DashStyle.Dash;e.Graphics.DrawRectangle(pen,origin.X+b.X*previewScale,origin.Y+b.Y*previewScale,Math.Max(1,b.Width*previewScale-1),Math.Max(1,b.Height*previewScale-1));}}finally{e.Graphics.Restore(state);}}
    void StartDrag(object sender,MouseEventArgs e){
        if(e.Button!=MouseButtons.Left||current==null)return;Point p=CanvasPoint(e.Location);if(p.X<0||p.Y<0||p.X>=project.Width||p.Y>=project.Height)return;
        if(SelectedLayer()==null||!LayerBounds().Contains(p)){bool found=false;for(int i=layers.Items.Count-1;i>=0;i--){LayerItem item=(LayerItem)layers.Items[i];Rectangle b=item.Image?new Rectangle(project.ImageX,project.ImageY,ImageWidth(),ImageHeight()):new Rectangle(item.Widget.X,item.Widget.Y,item.Widget.Width,item.Widget.Height);if(b.Contains(p)){layers.SelectedIndex=i;found=true;break;}}if(!found)return;}
        Rectangle selected=LayerBounds();dragOffset=new Point(p.X-selected.X,p.Y-selected.Y);dragging=true;preview.Capture=true;
    }
    void MoveDrag(object sender,MouseEventArgs e){if(!dragging||SelectedLayer()==null)return;Point p=CanvasPoint(e.Location);Rectangle b=LayerBounds();b.X=Math.Max(0,Math.Min(project.Width-1,p.X-dragOffset.X));b.Y=Math.Max(0,Math.Min(project.Height-1,p.Y-dragOffset.Y));SetBounds(b);}
    void AddWidget(){var w=new ScreenWidget {Kind=WidgetKind.Text,X=0,Y=Math.Max(0,project.Height/2),Width=project.Width,Height=Math.Max(1,project.Height/2),FontSize=9,Text="Новый текст"};project.Widgets.Add(w);if(project.Composition==CompositionMode.Image)project.Composition=CompositionMode.ImageAndWidgets;SyncControls(w,false);editingTabs.SelectedIndex=0;Changed();text.Focus();text.SelectAll();}
    void RemoveLayer(){LayerItem selected=SelectedLayer();if(selected==null)return;if(selected.Image){project.ImageFile=null;project.ImageX=project.ImageY=project.ImageWidth=project.ImageHeight=0;if(project.Composition!=CompositionMode.Widgets)project.Composition=CompositionMode.Widgets;}else project.Widgets.Remove(selected.Widget);SyncControls(null,false);Changed();}
    int? FreshConfiguredDpi(){return runtimeStatus!=null&&runtimeStatus.IsValid&&!runtimeStatus.IsBusy&&runtimeStatus.IsFresh(DateTimeOffset.UtcNow)&&runtimeStatus.ConfiguredDpi.HasValue?(int?)runtimeStatus.ConfiguredDpi.Value:null;}
    bool UsesNumericDpi(){if(project.Composition==CompositionMode.Image)return false;foreach(var widget in project.Widgets)if(widget.Kind==WidgetKind.Dpi)return true;return false;}
    WidgetContext Context(){var mouse=telemetry.Snapshot;return new WidgetContext {Now=DateTime.Now,CultureName=project.CultureName,BatteryPercent=mouse==null?null:(double?)mouse.BatteryPercent,Dpi=FreshConfiguredDpi(),DpiStage=mouse==null?null:mouse.DpiStage,CpuPercent=snapshot==null?null:snapshot.CpuUsagePercent,RamPercent=snapshot==null?null:snapshot.RamUsagePercent,GpuPercent=snapshot==null?null:snapshot.GpuUsagePercent,CpuTemperatureC=snapshot==null?null:snapshot.CpuTemperatureCelsius,GpuTemperatureC=snapshot==null?null:snapshot.GpuTemperatureCelsius};}
    async Task RefreshSensors(){if(sampling||IsDisposed||disposedEditor)return;sampling=true;try{snapshot=await Task.Run(()=>sensors.Sample());if(!IsDisposed&&!disposedEditor)Render();}catch(Exception e){if(!IsDisposed&&!disposedEditor)info.Text="Датчики: "+e.Message;}finally{sampling=false;}}
    void ApplyPreset(int index){
        project.Composition=CompositionMode.Widgets;project.Widgets.Clear();int half=Math.Max(1,project.Width/2),top=Math.Max(1,project.Height/2),bottom=Math.Max(1,project.Height-top);
        if(index==0)project.Widgets=WidgetRenderer.CreateDefault(project.Width,project.Height);
        else if(index==1){Add(WidgetKind.CpuPercent,0,0,half,top);Add(WidgetKind.GpuPercent,half,0,Math.Max(1,project.Width-half),top);Add(WidgetKind.RamPercent,0,top,project.Width,bottom);}
        else if(index==2){Add(WidgetKind.CpuTemperatureC,0,0,half,top);Add(WidgetKind.GpuTemperatureC,half,0,Math.Max(1,project.Width-half),top);Add(WidgetKind.Clock,0,top,project.Width,bottom);}
        else if(index==3){Add(WidgetKind.Clock,0,0,project.Width,project.Height);project.Widgets[0].FontSize=Math.Max(4,project.Height-6);project.Widgets[0].ShowLabel=false;}
        else if(index==4){Add(WidgetKind.Text,0,0,project.Width,project.Height);project.Widgets[0].Text="FURYCUBE";project.Widgets[0].FontSize=Math.Max(4,project.Height-10);}
        else project.Composition=CompositionMode.Image;
    }
    void Add(WidgetKind kind,int x,int y,int width,int height){project.Widgets.Add(new ScreenWidget {Kind=kind,X=x,Y=y,Width=width,Height=height,FontSize=9,ShowLabel=true});}
    void Changed(){if(syncing||disposedEditor)return;dirty=true;autosaveTimer.Stop();autosaveTimer.Start();autosaveStatus.Text="Изменения сохраняются…";autosaveStatus.ForeColor=Color.DimGray;Render();}
    bool RestoreAutosave(){if(!File.Exists(autosaveFile)){autosaveStatus.Text="Автосохранение включено";return false;}try{project=ScreenProject.Load(autosaveFile);autosaveStatus.Text="Макет восстановлен";autosaveStatus.ForeColor=Color.DarkGreen;return true;}catch(Exception e){autosaveStatus.Text="Не удалось восстановить макет";autosaveStatus.ForeColor=Color.DarkGoldenrod;Log("Автовосстановление экрана: "+e.Message);return false;}}
    void SaveAutosave(){
        if(!dirty)return;
        try{
            // Save on a copy: Save updates asset paths and DirectoryPath; the
            // explicitly opened/saved user project must retain its own folder.
            var serializer=new JavaScriptSerializer();var copy=serializer.Deserialize<ScreenProject>(serializer.Serialize(project));copy.ImageFile=project.ResolveImagePath();copy.Save(autosaveFile);dirty=false;
            if(!disposedEditor){autosaveStatus.Text="Автосохранено "+DateTime.Now.ToString("HH:mm");autosaveStatus.ForeColor=Color.DarkGreen;}
        }catch(Exception e){if(!disposedEditor){autosaveStatus.Text="Автосохранение недоступно";autosaveStatus.ForeColor=Color.DarkGoldenrod;}Log("Автосохранение экрана: "+e.Message);}
    }
    void Log(string value){if(report!=null&&!disposedEditor)report(value);}
    internal string DescribeMouseTelemetry(){
        var mouse=telemetry.Snapshot;
        return "Пассивный HID: "+(telemetry.ProductId==0?"не подключён":telemetry.ProductId.ToString("X4"))+"; заряд: "+(mouse.BatteryPercent.HasValue?mouse.BatteryPercent.Value+"%":"--")+"; уровень DPI#: "+(mouse.DpiStage.HasValue?mouse.DpiStage.Value.ToString():"--")+"; числовой DPI: неизвестен"+(String.IsNullOrEmpty(telemetry.LastError)?"":"; ошибка: "+telemetry.LastError);
    }
    void Render(){
        if(IsDisposed||disposedEditor)return;
        try{current=project.Render(Context());int fitX=Math.Max(1,(preview.ClientSize.Width-16)/project.Width),fitY=Math.Max(1,(preview.ClientSize.Height-16)/project.Height);previewScale=Math.Max(1,Math.Min(5,Math.Min(fitX,fitY)));
            Image old=preview.Image;preview.Image=current.ToBitmap(previewScale);if(old!=null)old.Dispose();canvasCaption.Text=project.Width+" × "+project.Height+" px · чёрно-белый экран · масштаб "+previewScale+"×";
            info.Text=oledNote;
        }catch(Exception e){current=null;info.Text="Не удалось отрисовать: "+e.Message;}
    }
    async Task UploadFrame(bool quiet=false){
        if(uploading||sendFrame==null)return;Render();if(current==null)return;
        if(project.Width!=120||project.Height!=28){live.Checked=false;oledNote="G9 PRO принимает кадр 120 × 28. Откройте макет этого размера.";Render();return;}
        uploading=true;upload.Enabled=false;runtimeStatus=null;oledNote="Проверяю прошивку и передаю кадр…";Render();
        try{
            // STATUS is requested only by an explicit upload/live tick which
            // actually renders numeric DPI. Preview/sensors never query it.
            if(UsesNumericDpi()&&readRuntimeStatus!=null)runtimeStatus=await readRuntimeStatus();
            if(IsDisposed||disposedEditor)return;
            if(project.Width!=120||project.Height!=28){live.Checked=false;oledNote="G9 PRO принимает кадр 120 × 28. Откройте макет этого размера.";return;}
            Render();if(current==null){live.Checked=false;return;}
            byte[] pixels=(byte[])current.Buffer.Clone();
            oledNote=await sendFrame(pixels);if(IsDisposed||disposedEditor)return;
            if(live.Checked)oledNote+=" Автообновление включено; HUB должен работать.";if(!quiet)Log(oledNote);
        }
        catch(Exception error){runtimeStatus=null;if(!IsDisposed&&!disposedEditor){live.Checked=false;oledNote="Передача остановлена: "+error.Message;Log(oledNote);}}
        finally{uploading=false;if(!IsDisposed&&!disposedEditor){upload.Enabled=true;Render();}}
    }
    void ChooseImage(){using(var dialog=new OpenFileDialog {Filter="Картинки|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff"})if(dialog.ShowDialog(this)==DialogResult.OK){project.ImageFile=dialog.FileName;if(project.Composition==CompositionMode.Widgets)project.Composition=project.Widgets.Count>0?CompositionMode.ImageAndWidgets:CompositionMode.Image;SyncControls(null,true);Changed();}}
    void SaveProject(){using(var dialog=new SaveFileDialog {Filter="Проект экрана|*.g9screen.json",FileName="screen.g9screen.json"})if(dialog.ShowDialog(this)==DialogResult.OK)try{ScreenWidget w=SelectedWidget();bool image=ImageSelected();project.Save(dialog.FileName);SyncControls(w,image);Log("Сохранён проект экрана: "+dialog.FileName);dirty=true;SaveAutosave();}catch(Exception e){MessageBox.Show(this,e.Message,"Сохранение проекта",MessageBoxButtons.OK,MessageBoxIcon.Error);}}
    void LoadProject(){using(var dialog=new OpenFileDialog {Filter="Проект экрана|*.g9screen.json;*.json"})if(dialog.ShowDialog(this)==DialogResult.OK)try{project=ScreenProject.Load(dialog.FileName);syncing=true;try{preset.SelectedIndex=-1;}finally{syncing=false;}SyncControls(null,false);Changed();Log("Открыт проект экрана: "+dialog.FileName);}catch(Exception e){MessageBox.Show(this,e.Message,"Открытие проекта",MessageBoxButtons.OK,MessageBoxIcon.Error);}}
    void ExportPng(){Render();if(current==null)return;using(var dialog=new SaveFileDialog {Filter="PNG|*.png",FileName="oled.png"})if(dialog.ShowDialog(this)==DialogResult.OK)try{using(var image=current.ToBitmap())image.Save(dialog.FileName,System.Drawing.Imaging.ImageFormat.Png);}catch(Exception e){MessageBox.Show(this,e.Message,"Экспорт PNG",MessageBoxButtons.OK,MessageBoxIcon.Error);}}
    void ExportFrame(){Render();if(current==null)return;using(var dialog=new SaveFileDialog {Filter="Кадр OLED,1bpp|*.bin",FileName="oled-"+project.Width+"x"+project.Height+".bin"})if(dialog.ShowDialog(this)==DialogResult.OK)try{File.WriteAllBytes(dialog.FileName,current.Buffer);}catch(Exception e){MessageBox.Show(this,e.Message,"Экспорт кадра",MessageBoxButtons.OK,MessageBoxIcon.Error);}}
}
