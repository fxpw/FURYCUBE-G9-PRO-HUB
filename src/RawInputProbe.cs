using System;
using System.IO;
using System.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

class RawInputProbe : Form {
    [StructLayout(LayoutKind.Sequential)] struct RID { public ushort page, usage; public uint flags; public IntPtr hwnd; }
    [DllImport("user32.dll", SetLastError=true)] static extern bool RegisterRawInputDevices(RID[] d, uint n, uint size);
    [DllImport("user32.dll")] static extern uint GetRawInputData(IntPtr h, uint command, IntPtr data, ref uint size, uint header);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern uint GetRawInputDeviceInfo(IntPtr h, uint command, StringBuilder data, ref uint size);
    StreamWriter log;
    TextBox output;
    Timer timer;
    int seconds = 600;
    public RawInputProbe(string path) {
        Text = "G9 PRO — проверка шестой кнопки"; Width=780; Height=340;
        log = new StreamWriter(path, false, new UTF8Encoding(false)); log.AutoFlush=true;
        output=new TextBox { Dock=DockStyle.Fill, Multiline=true, ReadOnly=true, ScrollBars=ScrollBars.Vertical };
        Controls.Add(output);
        Shown += delegate {
            RID[] r = new RID[] { new RID {page=1,usage=6,flags=0x100,hwnd=Handle}, new RID {page=1,usage=2,flags=0x100,hwnd=Handle} };
            if(!RegisterRawInputDevices(r,2,(uint)Marshal.SizeOf(typeof(RID)))) throw new System.ComponentModel.Win32Exception();
            Note("Нажмите шестую кнопку несколько раз. Можно переключаться в Блокнот.");
            Note("Записываются события G9 PRO и отдельно только клавиша 0 от любого устройства для проверки маршрута.");
            timer=new Timer {Interval=1000}; timer.Tick+=delegate { if(--seconds<=0) Close(); }; timer.Start();
        };
        FormClosed += delegate { if(timer!=null)timer.Dispose(); log.Dispose(); };
    }
    void Note(string s) { string line=DateTime.UtcNow.ToString("o")+" "+s; log.WriteLine(line); output.AppendText(line+Environment.NewLine); }
    protected override void WndProc(ref Message m) {
        if((m.Msg==0x100 || m.Msg==0x101) && m.WParam.ToInt64()==0x30) Note("FOCUSED zero "+(m.Msg==0x100?"DOWN":"UP"));
        if(m.Msg==0xff) {
            uint header=(uint)(IntPtr.Size==8?24:16), size=0;
            GetRawInputData(m.LParam,0x10000003,IntPtr.Zero,ref size,header);
            IntPtr data=Marshal.AllocHGlobal((int)size);
            try {
                if(GetRawInputData(m.LParam,0x10000003,data,ref size,header)!=0xffffffff) {
                    IntPtr device=Marshal.ReadIntPtr(data,8); uint chars=1024; var name=new StringBuilder(1024);
                    GetRawInputDeviceInfo(device,0x20000007,name,ref chars);
                    string n=name.ToString().ToUpperInvariant();
                    int rawType=Marshal.ReadInt32(data);
                    bool zero=rawType==1 && (ushort)Marshal.ReadInt16(data,(int)header+6)==0x30;
                    if(n.Contains("VID_1D57&PID_FA60") || zero) {
                        int type=Marshal.ReadInt32(data); int h=(int)header;
                        if(type==1) Note("KEYBOARD "+n+" scan="+((ushort)Marshal.ReadInt16(data,h)).ToString("X4")+" flags="+((ushort)Marshal.ReadInt16(data,h+2)).ToString("X4")+" vkey="+((ushort)Marshal.ReadInt16(data,h+6)).ToString("X4"));
                        if(type==0) { ushort flags=(ushort)Marshal.ReadInt16(data,h+4); if(flags!=0)Note("G9 mouse buttons="+flags.ToString("X4")); }
                    }
                }
            } finally { Marshal.FreeHGlobal(data); }
        }
        base.WndProc(ref m);
    }
    [STAThread] static void Main(string[] args) { Application.EnableVisualStyles(); Application.Run(new RawInputProbe(args[0])); }
}
