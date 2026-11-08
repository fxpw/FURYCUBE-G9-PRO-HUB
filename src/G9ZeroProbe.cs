using System;
using System.IO;
using System.Text;
using System.Drawing;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Forms;

class G9ZeroProbe : Form {
 [StructLayout(LayoutKind.Sequential)]struct RID{public ushort page,usage;public uint flags;public IntPtr window;}
 [DllImport("user32.dll",SetLastError=true)]static extern bool RegisterRawInputDevices(RID[] devices,uint count,uint size);
 [DllImport("user32.dll")]static extern uint GetRawInputData(IntPtr input,uint command,IntPtr data,ref uint size,uint header);
 [DllImport("user32.dll",CharSet=CharSet.Unicode,EntryPoint="GetRawInputDeviceInfoW")]static extern uint GetName(IntPtr handle,uint command,StringBuilder text,ref uint size);
 readonly Dictionary<IntPtr,string> names=new Dictionary<IntPtr,string>();
 readonly StreamWriter log;
 readonly Label status=new Label{Dock=DockStyle.Top,Height=64,Font=new Font("Segoe UI",12)};
 readonly TextBox output=new TextBox{Dock=DockStyle.Fill,Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical,Font=new Font("Consolas",9)};
 readonly TestField field=new TestField{Dock=DockStyle.Top,Height=45,Font=new Font("Consolas",22)};
 readonly Timer lifetime=new Timer{Interval=300000};
 readonly Timer traceTimer=new Timer{Interval=250};
 readonly Button start=new Button{Dock=DockStyle.Top,Height=40,Text="Подготовка USB-проверки…",Enabled=false};
 readonly string traceReady,traceArm;
 DateTime? traceStarted;
 bool traceRequested;
 int controls,g9keys,clicks,chars;
 class TestField:TextBox{
  public Action<string> Observed;
  protected override void WndProc(ref Message m){
   if(Observed!=null){
    if((m.Msg==0x100||m.Msg==0x101)&&m.WParam.ToInt64()==0x30)Observed("TEXTFIELD KEY0 "+(m.Msg==0x100?"DOWN":"UP"));
    if(m.Msg==0x102&&m.WParam.ToInt64()==0x30)Observed("TEXTFIELD CHAR0");
   }base.WndProc(ref m);
  }
 }
 static bool IsG9(string name){return name.IndexOf("VID_1D57&PID_FA60",StringComparison.OrdinalIgnoreCase)>=0||name.IndexOf("VID_1D57&PID_2148",StringComparison.OrdinalIgnoreCase)>=0;}
 public G9ZeroProbe(string path,string ready,string arm){
  traceReady=ready;traceArm=arm;
  Text="G9 PRO — обычная 0: проверка";Width=1050;Height=510;StartPosition=FormStartPosition.CenterScreen;
  log=new StreamWriter(path,false,new UTF8Encoding(false));log.AutoFlush=true;
  var instructions=new Label{Dock=DockStyle.Top,Height=110,Font=new Font("Segoe UI",11),Text="1. В поле ниже нажми обычную 0 на клавиатуре один раз.\n2. Коротко нажми шестую кнопку с экраном три раза. Не удерживай.\n3. Один раз нажми колёсико (временно назначена 0 для сравнения).\n4. Один раз нажми обычную левую кнопку мыши."};
  if(traceReady==null)instructions.Text="1. В поле ниже нажми обычную 0 на клавиатуре один раз.\n2. Коротко нажми кнопку-экран отдельно.\n3. Зажми ПРАВУЮ кнопку мыши и, не отпуская её, коротко нажми кнопку-экран дважды.\n4. Отпусти правую кнопку. Колёсику возвращено обычное действие.";
  Controls.Add(output);Controls.Add(field);Controls.Add(status);if(traceReady!=null)Controls.Add(start);Controls.Add(instructions);
  if(traceReady!=null){field.Enabled=false;start.Click+=delegate{File.WriteAllText(traceArm,"ARM");traceRequested=true;start.Enabled=false;start.Text="Запускаю USB-проверку…";Note("USB TEST START REQUESTED");};
   traceTimer.Tick+=delegate{try{if(!File.Exists(traceReady))return;string state=File.ReadAllText(traceReady);
    if(state.Contains("\"phase\":\"error\"")||state.Contains("\"phase\":\"target-lost\"")){start.Enabled=false;start.Text="USB-проверка остановлена — сообщение записано в журнал";Note("TRACE STATE "+state);traceTimer.Stop();return;}
    if(!traceRequested&&state.Contains("\"phase\":\"waiting-arm\"")){start.Text="Начать проверку (30 секунд)";start.Enabled=true;}
    if(!traceStarted.HasValue&&state.Contains("\"phase\":\"capturing\"")){traceStarted=DateTime.UtcNow;field.Enabled=true;field.Focus();Note("USB CAPTURE READY");}
    if(traceStarted.HasValue){int left=30-(int)(DateTime.UtcNow-traceStarted.Value).TotalSeconds;start.Text=left>0?"Идёт USB-проверка: "+left+" с":"USB-проверка завершена";}
   }catch(IOException){}};traceTimer.Start();}
  field.Observed=delegate(string line){if(line=="TEXTFIELD CHAR0")chars++;Note(line);UpdateStatus();};
  lifetime.Tick+=delegate{Close();};
  Shown+=delegate{
   var devices=new RID[]{new RID{page=1,usage=6,flags=0x100,window=Handle},new RID{page=1,usage=2,flags=0x100,window=Handle},new RID{page=0x0a,usage=0,flags=0x120,window=Handle},new RID{page=0x0c,usage=1,flags=0x100,window=Handle}};
   foreach(var device in devices)if(!RegisterRawInputDevices(new RID[]{device},1,(uint)Marshal.SizeOf(typeof(RID))))throw new System.ComponentModel.Win32Exception();
   Note("READY: passive Raw Input, VID1D57 PID2148 + FA60. No hooks, no configuration writes, no generated input.");
   UpdateStatus();field.Focus();lifetime.Start();
  };
  FormClosed+=delegate{traceTimer.Dispose();lifetime.Dispose();log.Dispose();};
 }
 void UpdateStatus(){status.Text="Контрольная 0 с клавиатуры: "+controls+"; события клавиатуры G9: "+g9keys+"; клики G9: "+clicks+"\nНапечатано нулей в поле: "+chars;}
 void Note(string line){line=DateTime.UtcNow.ToString("o")+" "+line;log.WriteLine(line);if(output.TextLength>100000)output.Clear();output.AppendText(line+Environment.NewLine);}
 string DeviceName(IntPtr handle){string name;if(names.TryGetValue(handle,out name))return name;uint size=1024;var text=new StringBuilder(1024);GetName(handle,0x20000007,text,ref size);name=text.ToString();names[handle]=name;return name;}
 protected override void WndProc(ref Message m){
  if(m.Msg==0xff){uint header=(uint)(IntPtr.Size==8?24:16),size=0;
   if(GetRawInputData(m.LParam,0x10000003,IntPtr.Zero,ref size,header)!=0xffffffff&&size>=header&&size<=1048576){
    IntPtr data=Marshal.AllocHGlobal((int)size);try{
     if(GetRawInputData(m.LParam,0x10000003,data,ref size,header)==size){int type=Marshal.ReadInt32(data),h=(int)header;string name=DeviceName(Marshal.ReadIntPtr(data,8));bool g9=IsG9(name);
      if(type==1&&size>=header+16){ushort scan=(ushort)Marshal.ReadInt16(data,h),flags=(ushort)Marshal.ReadInt16(data,h+2),key=(ushort)Marshal.ReadInt16(data,h+6);
       if(g9){g9keys++;Note("G9 KEY scan="+scan.ToString("X4")+" flags="+flags.ToString("X4")+" vkey="+key.ToString("X4")+" device="+name);}
       else if(key==0x30){if((flags&1)==0)controls++;Note("CONTROL_ZERO "+((flags&1)==0?"DOWN":"UP")+" device="+name);}
       if(g9||key==0x30)UpdateStatus();
      }else if(g9&&type==0&&size>=header+24){ushort flags=(ushort)Marshal.ReadInt16(data,h+4);short delta=Marshal.ReadInt16(data,h+6);if(flags!=0){if((flags&0x155)!=0)clicks++;Note("G9 MOUSE flags="+flags.ToString("X4")+" delta="+delta+" device="+name);UpdateStatus();}}
      else if(g9&&type==2&&size>=header+8){uint bytes=(uint)Marshal.ReadInt32(data,h),count=(uint)Marshal.ReadInt32(data,h+4);if(bytes>0&&bytes<=1024&&count<=1024&&(ulong)bytes*count<=size-header-8){byte[] report=new byte[(int)(bytes*count)];Marshal.Copy(IntPtr.Add(data,h+8),report,0,report.Length);Note("G9 HID "+BitConverter.ToString(report)+" device="+name);}}
     }
    }finally{Marshal.FreeHGlobal(data);}
   }
  }base.WndProc(ref m);
 }
 [STAThread]static void Main(string[] args){Application.EnableVisualStyles();string root=Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,".."));string folder=Path.Combine(root,"evidence");Directory.CreateDirectory(folder);string path=args.Length>0?args[0]:Path.Combine(folder,"zero-probe-"+DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffffffZ")+".log");Application.Run(new G9ZeroProbe(path,args.Length>1?args[1]:null,args.Length>2?args[2]:null));}
}
