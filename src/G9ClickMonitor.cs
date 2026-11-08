using System;
using System.IO;
using System.Text;
using System.Drawing;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Forms;

class G9ClickMonitor : Form {
 [StructLayout(LayoutKind.Sequential)]struct RID {public ushort page,usage;public uint flags;public IntPtr hwnd;}
 [DllImport("user32.dll",SetLastError=true)]static extern bool RegisterRawInputDevices(RID[] d,uint n,uint size);
 [DllImport("user32.dll",SetLastError=true)]static extern uint GetRawInputData(IntPtr h,uint command,IntPtr data,ref uint size,uint header);
 [DllImport("user32.dll",SetLastError=true)]static extern uint GetRawInputDeviceList(IntPtr list,ref uint count,uint size);
 [DllImport("user32.dll",CharSet=CharSet.Unicode,EntryPoint="GetRawInputDeviceInfoW")]static extern uint GetName(IntPtr h,uint command,StringBuilder name,ref uint size);
 [DllImport("user32.dll",EntryPoint="GetRawInputDeviceInfoW")]static extern uint GetInfo(IntPtr h,uint command,IntPtr info,ref uint size);
 readonly Dictionary<IntPtr,string> names=new Dictionary<IntPtr,string>();
 readonly Dictionary<IntPtr,string> lastHid=new Dictionary<IntPtr,string>();
 readonly Dictionary<IntPtr,uint> lastButtons=new Dictionary<IntPtr,uint>();
 readonly StreamWriter log;
 readonly TextBox output=new TextBox {Dock=DockStyle.Fill,Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical,Font=new Font("Consolas",10)};
 readonly Label status=new Label {Dock=DockStyle.Top,Height=65,Font=new Font("Segoe UI",12),Text="Подключаю G9 PRO…"};
 readonly Label counter=new Label {Dock=DockStyle.Top,Height=65,Font=new Font("Segoe UI",24,FontStyle.Bold),Text="Кнопка 6: 0",ForeColor=Color.DarkGreen};
 readonly Timer flash=new Timer {Interval=180};
 public event Action SixthButtonPressed;
 int presses,tilts;
 static bool IsG9(string s){return s.IndexOf("VID_1D57&PID_FA60",StringComparison.OrdinalIgnoreCase)>=0;}
 public G9ClickMonitor(string path) {
  Text="FURYCUBE G9 PRO — регистрация нажатий";Width=1000;Height=540;StartPosition=FormStartPosition.CenterScreen;
  log=new StreamWriter(path,false,new UTF8Encoding(false));log.AutoFlush=true;
  flash.Tick+=delegate {counter.BackColor=SystemColors.Control;flash.Stop();};
  SixthButtonPressed+=delegate {tilts++;UpdateCounter();counter.BackColor=Color.LightGreen;flash.Stop();flash.Start();Note("BUTTON6 CLICK #"+tilts);};
  var clear=new Button {Text="Обнулить счётчики",Dock=DockStyle.Bottom,Height=35};
  clear.Click+=delegate {presses=tilts=0;UpdateCounter();Note("COUNTERS RESET");};
  Controls.Add(output);Controls.Add(counter);Controls.Add(status);Controls.Add(clear);
  Shown+=delegate {try {RegisterInputs();}catch(Exception e){status.Text="Ошибка регистрации: "+e.Message;Note("ERROR "+e);}};
  FormClosed+=delegate {flash.Dispose();log.Dispose();};
 }
 string DeviceName(IntPtr h) {
  string value;if(names.TryGetValue(h,out value))return value;
  uint length=1024;var s=new StringBuilder(1024);GetName(h,0x20000007,s,ref length);value=s.ToString();names[h]=value;return value;
 }
 void RegisterInputs() {
  var regs=new Dictionary<string,RID>();
  Action<ushort,ushort> add=delegate(ushort page,ushort usage) {
   string key=page+":"+usage;
   regs[key]=new RID {page=page,usage=usage,flags=(uint)(0x100|(usage==0?0x20:0)),hwnd=Handle};
  };
  add(1,2);add(1,6);
  uint count=0,entry=(uint)(IntPtr.Size==8?16:8);
  if(GetRawInputDeviceList(IntPtr.Zero,ref count,entry)==0xffffffff)throw new System.ComponentModel.Win32Exception();
  IntPtr list=Marshal.AllocHGlobal(checked((int)(count*entry)));
  int found=0;
  try {
   uint received=GetRawInputDeviceList(list,ref count,entry);
   if(received==0xffffffff)throw new System.ComponentModel.Win32Exception();
   for(int i=0;i<received;i++) {
    IntPtr row=IntPtr.Add(list,(int)(i*entry)),device=Marshal.ReadIntPtr(row);int type=Marshal.ReadInt32(row,IntPtr.Size);
    string name=DeviceName(device);if(!IsG9(name))continue;found++;Note("DEVICE type="+type+" "+name);
    if(type==2) {
     IntPtr info=Marshal.AllocHGlobal(32);
     try {Marshal.WriteInt32(info,32);uint bytes=32;if(GetInfo(device,0x2000000b,info,ref bytes)!=0xffffffff) {
      ushort page=(ushort)Marshal.ReadInt16(info,20),usage=(ushort)Marshal.ReadInt16(info,22);add(page,usage);Note("COLLECTION page="+page.ToString("X4")+" usage="+usage.ToString("X4"));
     }}finally {Marshal.FreeHGlobal(info);}
    }
   }
  }finally {Marshal.FreeHGlobal(list);}
  foreach(RID registration in regs.Values) {
   if(!RegisterRawInputDevices(new RID[]{registration},1,(uint)Marshal.SizeOf(typeof(RID))))throw new System.ComponentModel.Win32Exception();
   Note("REGISTER page="+registration.page.ToString("X4")+" usage="+registration.usage.ToString("X4"));
  }
  status.Text="Нажимай шестую кнопку — счётчик увеличится и мигнёт зелёным.\nВ HUB для кнопки 6 должна стоять «Прокрутка влево».";
  Note("READY — passive input, no suppression, no generated clicks or keys");
 }
 void Note(string s){string line=DateTime.UtcNow.ToString("o")+" "+s;log.WriteLine(line);if(output.TextLength>120000)output.Text=output.Text.Substring(output.TextLength-60000);output.AppendText(line+Environment.NewLine);}
 void UpdateCounter(){counter.Text="Кнопка 6: "+tilts;}
 static string MouseEvents(ushort f,short delta) {
  string[] n={"LEFT DOWN","LEFT UP","RIGHT DOWN","RIGHT UP","MIDDLE DOWN","MIDDLE UP","BACK DOWN","BACK UP","FORWARD DOWN","FORWARD UP"};
  var a=new List<string>();for(int i=0;i<n.Length;i++)if((f&(1<<i))!=0)a.Add(n[i]);
  if((f&0x400)!=0)a.Add("VERTICAL "+delta);if((f&0x800)!=0)a.Add("HORIZONTAL "+delta);return string.Join(", ",a.ToArray());
 }
 protected override void WndProc(ref Message m) {
  if(m.Msg==0xff) {
   uint size=0,header=(uint)(IntPtr.Size==8?24:16);
   if(GetRawInputData(m.LParam,0x10000003,IntPtr.Zero,ref size,header)!=0xffffffff && size>=header && size<=1048576) {
    IntPtr data=Marshal.AllocHGlobal((int)size);
    try {
     if(GetRawInputData(m.LParam,0x10000003,data,ref size,header)==size) {
      IntPtr device=Marshal.ReadIntPtr(data,8);string name=DeviceName(device);int h=(int)header,type=Marshal.ReadInt32(data);
      if(IsG9(name)) {
       if(type==0 && size>=header+24) {
        ushort f=(ushort)Marshal.ReadInt16(data,h+4);short delta=Marshal.ReadInt16(data,h+6);uint raw=(uint)Marshal.ReadInt32(data,h+8),old;
        bool change=!lastButtons.TryGetValue(device,out old)?raw!=0:old!=raw;lastButtons[device]=raw;
        if(f!=0||change) {Note("MOUSE flags="+f.ToString("X4")+" raw="+raw.ToString("X8")+" "+MouseEvents(f,delta));
         if((f&0x155)!=0)presses++;
         if(G9ClickSignal.Matches(name,f,delta)) {var callback=SixthButtonPressed;if(callback!=null)callback();}}
       } else if(type==1 && size>=header+16) {
        Note("KEY scan="+((ushort)Marshal.ReadInt16(data,h)).ToString("X4")+" flags="+((ushort)Marshal.ReadInt16(data,h+2)).ToString("X4")+" vkey="+((ushort)Marshal.ReadInt16(data,h+6)).ToString("X4"));
       } else if(type==2 && size>=header+8) {
        uint bytes=(uint)Marshal.ReadInt32(data,h),reports=(uint)Marshal.ReadInt32(data,h+4);
        if(bytes>0 && bytes<=1024 && reports<1024 && (ulong)bytes*reports<=size-header-8) {
         byte[] payload=new byte[(int)(bytes*reports)];Marshal.Copy(IntPtr.Add(data,h+8),payload,0,payload.Length);
         string hex=BitConverter.ToString(payload),previous;
         if(!lastHid.TryGetValue(device,out previous)||previous!=hex) {lastHid[device]=hex;Note("HID "+name+" size="+bytes+" reports="+reports+" data="+hex);}
        }
       }
      }
     }
    }finally {Marshal.FreeHGlobal(data);}
   }
  }
  base.WndProc(ref m);
 }
 [STAThread]static void Main(string[] args) {
  Application.EnableVisualStyles();
  string folder=Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"..","evidence"));Directory.CreateDirectory(folder);
  string path=args.Length==0?Path.Combine(folder,"g9-clicks-"+DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffffffZ")+".log"):args[0];
  Application.Run(new G9ClickMonitor(path));
 }
}
