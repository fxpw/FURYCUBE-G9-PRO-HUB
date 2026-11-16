using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;

// Passive acceptance check: only Windows Raw Input from the chosen G9 PID.
// No HID features, hooks, injected input, firmware or settings writes.
internal sealed class G9InputCheckState {
    internal static readonly string[] Ids={"movement","left","right","middle","back","forward","wheelUp","wheelDown","screenKey"};
    internal static readonly string[] Labels={"Движение мыши","Левая кнопка","Правая кнопка","Клик колёсика","Боковая: назад","Боковая: вперёд","Прокрутка вверх","Прокрутка вниз","Кнопка-экран → 0"};
    internal static readonly string[] EnglishLabels={"Mouse movement","Left button","Right button","Middle click","Side button: back","Side button: forward","Scroll up","Scroll down","Screen button → 0"};
    internal readonly ushort Product;
    readonly bool[] done=new bool[9],pressed=new bool[6];
    internal G9InputCheckState(ushort product){if(product!=0x2148&&product!=0xFA60)throw new ArgumentOutOfRangeException("product");Product=product;}
    internal bool Matches(string path){
        if(String.IsNullOrEmpty(path))return false;
        string marker="VID_1D57&PID_"+Product.ToString("X4");int at=path.IndexOf(marker,StringComparison.OrdinalIgnoreCase);
        if(at<0)return false;int end=at+marker.Length;
        return end==path.Length||path[end]=='&'||path[end]=='#';
    }
    internal bool IsDone(int index){return done[index];}
    internal int CompletedCount{get{int n=0;foreach(bool value in done)if(value)n++;return n;}}
    internal bool Complete{get{return CompletedCount==Ids.Length;}}
    internal string[] Missing{get{var missing=new List<string>();for(int i=0;i<done.Length;i++)if(!done[i])missing.Add(Ids[i]);return missing.ToArray();}}
    internal void Mouse(string path,int x,int y,ushort flags,short wheel){
        if(!Matches(path))return;
        if(x!=0||y!=0)done[0]=true;
        for(int i=0;i<5;i++){
            int down=1<<(i*2),up=down<<1;
            if((flags&down)!=0)pressed[i]=true;
            if((flags&up)!=0&&pressed[i]){done[i+1]=true;pressed[i]=false;}
        }
        if((flags&0x400)!=0){if(wheel>0)done[6]=true;else if(wheel<0)done[7]=true;}
    }
    internal void Keyboard(string path,ushort virtualKey,bool release){
        if(!Matches(path)||virtualKey!=0x30)return;
        if(!release)pressed[5]=true;
        else if(pressed[5]){done[8]=true;pressed[5]=false;}
    }
    internal object Report(DateTime startUtc,string reason,bool english=false){
        var steps=new List<object>();for(int i=0;i<done.Length;i++)steps.Add(new{id=Ids[i],label=(english?EnglishLabels:Labels)[i],passed=done[i]});
        return new{schemaVersion=1,productId=Product.ToString("x4"),transport=Product==0x2148?"usb":"radio",
            startedUtc=startUtc.ToString("o"),finishedUtc=DateTime.UtcNow.ToString("o"),success=Complete,
            reason=reason,passed=CompletedCount,total=Ids.Length,missing=Missing,steps=steps.ToArray(),
            passive=true,deviceWrites=false};
    }
}

internal sealed class G9InputCheckForm:Form {
    [StructLayout(LayoutKind.Sequential)]struct Rid{public ushort page,usage;public uint flags;public IntPtr window;}
    [DllImport("user32.dll",SetLastError=true)]static extern bool RegisterRawInputDevices(Rid[] devices,uint count,uint size);
    [DllImport("user32.dll")]static extern uint GetRawInputData(IntPtr input,uint command,IntPtr data,ref uint size,uint headerSize);
    [DllImport("user32.dll",CharSet=CharSet.Unicode,EntryPoint="GetRawInputDeviceInfoW")]static extern uint GetDeviceName(IntPtr device,uint command,StringBuilder name,ref uint size);
    readonly G9InputCheckState state;
    readonly Label progress=new Label{AutoSize=true,Font=new Font("Segoe UI",15,FontStyle.Bold)};
    readonly Label[] indicators=new Label[G9InputCheckState.Ids.Length];
    readonly Dictionary<IntPtr,string> devices=new Dictionary<IntPtr,string>();
    readonly System.Windows.Forms.Timer timer=new System.Windows.Forms.Timer{Interval=100};
    readonly Stopwatch elapsed=Stopwatch.StartNew();
    readonly DateTime started=DateTime.UtcNow;
    readonly string output;
    readonly bool english;
    readonly StreamWriter log;
    double closeAt=-1;
    string reason="closed";
    bool saved;

    string T(string ru,string en){return english?en:ru;}
    internal G9InputCheckForm(ushort product,string reportFile,bool useEnglish=false){
        english=useEnglish;
        state=new G9InputCheckState(product);output=Path.GetFullPath(reportFile);Directory.CreateDirectory(Path.GetDirectoryName(output));
        log=new StreamWriter(new FileStream(output+".log",FileMode.CreateNew,FileAccess.Write,FileShare.Read),new UTF8Encoding(false));log.AutoFlush=true;
        Text=T("G9 PRO — быстрая проверка ","G9 PRO — quick input check ")+(product==0x2148?"USB":"2.4G");
        Font=new Font("Segoe UI",11);BackColor=Color.FromArgb(22,25,31);ForeColor=Color.FromArgb(235,238,244);
        ClientSize=new Size(650,665);MinimumSize=new Size(590,690);StartPosition=FormStartPosition.CenterScreen;
        var panel=new FlowLayoutPanel{Dock=DockStyle.Fill,FlowDirection=FlowDirection.TopDown,WrapContents=false,Padding=new Padding(22),AutoScroll=true};Controls.Add(panel);
        panel.Controls.Add(progress);
        panel.Controls.Add(new Label{Text=T("Подвигай G9 и пройди пункты ниже. Кнопки нажми и отпусти.\nДругие мыши и клавиатуры не засчитываются.","Move your G9 and complete each check below. Press and release buttons.\nOther mice and keyboards do not count."),AutoSize=true,Margin=new Padding(0,12,0,14)});
        for(int i=0;i<indicators.Length;i++){
            indicators[i]=new Label{Text="○  "+(english?G9InputCheckState.EnglishLabels:G9InputCheckState.Labels)[i],Width=570,Height=36,Padding=new Padding(10,6,0,0),BackColor=Color.FromArgb(35,39,47),Margin=new Padding(0,0,0,5)};
            panel.Controls.Add(indicators[i]);
        }
        panel.Controls.Add(new Label{Text=T("Для проверки кнопки-экрана назначенная клавиша должна быть «0».","Assign the 0 key to the screen button before running this check."),AutoSize=true,Margin=new Padding(0,8,0,6)});
        panel.Controls.Add(new TextBox{Width=570,Height=32,Font=new Font("Segoe UI",12),AccessibleName=T("Поле для проверки клавиши 0","Test field for the 0 key")});
        var close=new Button{Text=T("Закрыть и сохранить отчёт","Close and save report"),AutoSize=true,Padding=new Padding(10,5,10,5),Margin=new Padding(0,10,0,7)};
        close.Click+=delegate{Close();};panel.Controls.Add(close);
        panel.Controls.Add(new Label{Text=T("Все 9 отметок → автозакрытие через 2 секунды.\nЧастичная проверка тоже сохраняется. Лимит: 10 минут.\nОтчёт: ","All 9 checks → closes automatically after 2 seconds.\nIncomplete checks are saved too. Time limit: 10 minutes.\nReport: ")+output,AutoSize=true,MaximumSize=new Size(570,0),ForeColor=Color.Silver});
        Shown+=delegate{
            try{
                var r=new[]{new Rid{page=1,usage=2,flags=0x2100,window=Handle},new Rid{page=1,usage=6,flags=0x2100,window=Handle}};
                if(!RegisterRawInputDevices(r,2,(uint)Marshal.SizeOf(typeof(Rid))))throw new System.ComponentModel.Win32Exception();
                Write("BEGIN product="+product.ToString("x4")+" passive=true deviceWrites=false");RefreshChecks();timer.Start();
            }catch(Exception error){reason="error";Write("ERROR "+error.Message);Close();}
        };
        timer.Tick+=delegate{
            if(closeAt>=0&&elapsed.Elapsed.TotalSeconds>=closeAt){reason="completed";Close();}
            else if(elapsed.Elapsed.TotalMinutes>=10){reason="timeout";Close();}
        };
        FormClosed+=delegate{timer.Stop();SaveReport();log.Dispose();};
    }
    void Write(string value){log.WriteLine(DateTime.UtcNow.ToString("o")+" "+value);}
    void RefreshChecks(){
        for(int i=0;i<indicators.Length;i++)if(state.IsDone(i)){
            indicators[i].Text="✓  "+(english?G9InputCheckState.EnglishLabels:G9InputCheckState.Labels)[i];indicators[i].BackColor=Color.FromArgb(25,81,56);indicators[i].ForeColor=Color.FromArgb(140,245,179);
        }
        progress.Text=state.CompletedCount+" / "+indicators.Length+(state.Complete?T(" — всё пройдено, закрытие через 2 с"," — all passed, closing in 2 s"):T(" — проверка ввода"," — input check"));
        if(state.Complete&&closeAt<0)closeAt=elapsed.Elapsed.TotalSeconds+2;
    }
    string DeviceName(IntPtr handle){
        string name;if(devices.TryGetValue(handle,out name))return name;
        uint size=2048;var buffer=new StringBuilder((int)size);
        if(GetDeviceName(handle,0x20000007,buffer,ref size)==UInt32.MaxValue)return "";
        name=buffer.ToString();devices[handle]=name;if(state.Matches(name))Write("DEVICE "+name);return name;
    }
    protected override void WndProc(ref Message message){
        if(message.Msg==0xFE)devices.Clear();
        if(message.Msg==0xFF&&!saved){
            uint size=0,header=(uint)(8+IntPtr.Size*2);
            if(GetRawInputData(message.LParam,0x10000003,IntPtr.Zero,ref size,header)!=UInt32.MaxValue&&size>=header&&size<=4096){
                IntPtr bytes=Marshal.AllocHGlobal((int)size);
                try{if(GetRawInputData(message.LParam,0x10000003,bytes,ref size,header)==size){
                    string name=DeviceName(Marshal.ReadIntPtr(bytes,8));int type=Marshal.ReadInt32(bytes),body=(int)header,before=state.CompletedCount;
                    if(type==0&&size>=header+24){
                        ushort flags=(ushort)Marshal.ReadInt16(bytes,body+4);short wheel=Marshal.ReadInt16(bytes,body+6);
                        state.Mouse(name,Marshal.ReadInt32(bytes,body+12),Marshal.ReadInt32(bytes,body+16),flags,wheel);
                        if(state.Matches(name)&&flags!=0)Write("MOUSE flags="+flags.ToString("X4")+" wheel="+wheel);
                    }else if(type==1&&size>=header+16){
                        ushort key=(ushort)Marshal.ReadInt16(bytes,body+6);bool released=((ushort)Marshal.ReadInt16(bytes,body+2)&1)!=0;
                        state.Keyboard(name,key,released);
                        if(state.Matches(name)&&key==0x30)Write(released?"SCREEN_KEY_UP":"SCREEN_KEY_DOWN");
                    }
                    if(before!=state.CompletedCount){Write("CHECKPOINT passed="+state.CompletedCount+" missing="+String.Join(",",state.Missing));RefreshChecks();}
                }}finally{Marshal.FreeHGlobal(bytes);}
            }
        }
        base.WndProc(ref message);
    }
    void SaveReport(){
        if(saved)return;saved=true;
        try{
            string json=new JavaScriptSerializer().Serialize(state.Report(started,reason,english));
            // Each invocation uses a unique path; never overwrite an older report.
            using(var file=new FileStream(output,FileMode.CreateNew,FileAccess.Write,FileShare.Read))
            using(var writer=new StreamWriter(file,new UTF8Encoding(false)))writer.WriteLine(json);
            Write("RESULT success="+state.Complete+" passed="+state.CompletedCount+" total=9 reason="+reason+" report="+output);
        }catch(Exception error){Write("REPORT_ERROR "+error.Message);MessageBox.Show(T("Не удалось сохранить отчёт: ","Could not save report: ")+error.Message,T("G9 — отчёт","G9 — report"),MessageBoxButtons.OK,MessageBoxIcon.Error);}
    }
}

internal static class G9InputCheckProgram {
    [STAThread]static int Main(string[] args){
        try{
            ushort product=0;string output=null;bool english=false,languageSet=false;
            for(int i=0;i<args.Length;i++){
                if(args[i]=="--pid"&&i+1<args.Length&&product==0){
                    if(!UInt16.TryParse(args[++i],System.Globalization.NumberStyles.HexNumber,System.Globalization.CultureInfo.InvariantCulture,out product)||(product!=0x2148&&product!=0xFA60))throw new ArgumentException("Use --pid 2148 (USB) or --pid fa60 (2.4G).");
                }else if(args[i]=="--output"&&i+1<args.Length&&output==null)output=Path.GetFullPath(args[++i]);
                else if(args[i]=="--lang"&&i+1<args.Length&&!languageSet){string language=args[++i];if(language!="ru"&&language!="en")throw new ArgumentException("Use --lang ru or --lang en.");english=language=="en";languageSet=true;}
                else throw new ArgumentException("Unknown input-check argument.");
            }
            if(product==0)throw new ArgumentException("Specify --pid 2148 (USB) or --pid fa60 (2.4G).");
            if(output==null)output=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"FURYCUBE-G9-PRO","input-checks",DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmssfffffff'Z'")+"-"+Guid.NewGuid().ToString("N")+".json");
            if(File.Exists(output)||File.Exists(output+".log"))throw new IOException("Choose a new report path; previous checks are preserved.");
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);Application.Run(new G9InputCheckForm(product,output,english));return 0;
        }catch(Exception error){MessageBox.Show(error.Message,"G9 — проверка ввода",MessageBoxButtons.OK,MessageBoxIcon.Error);return 1;}
    }
}
