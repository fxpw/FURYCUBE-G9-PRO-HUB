// Windows x64, .NET Framework. Passive UCX ETW consumer: no USB requests,
// driver installation, PnP changes, ETL file, or input injection.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Web.Script.Serialization;

internal sealed class G9UsbTrace {
    static readonly Guid Provider = new Guid("36da592d-e43a-4e28-af6f-4bc57c5a11e8");
    static readonly Guid HubProvider = new Guid("ac52ad17-cc01-4f85-8df5-4dce4333c99b");
    const ulong Rundown = 0x8000, FullData = 0x100;
    const uint RealTimeMode = 0x100, EventRecordMode = 0x10000000;
    const ulong InvalidTraceHandle = ulong.MaxValue;

    public sealed class Target {
        public string InstanceId {get;set;}
        public string ControllerInstanceId {get;set;}
        public string LocationPath {get;set;}
        public string SnapshotUtc {get;set;}
        public string PdoName {get;set;}
        public int VendorId {get;set;}
        public int ProductId {get;set;}
        public int PciBus {get;set;}
        public int PciDevice {get;set;}
        public int PciFunction {get;set;}
        public int PciVendorId {get;set;}
        public int PciDeviceId {get;set;}
        public int[] Ports {get;set;}
    }
    sealed class Controller { public ulong Address; public int Bus, Device, Function, Vendor, Product; }
    sealed class Device { public ulong Controller, Address; public int[] Ports; }
    sealed class HubDevice { public ulong Address;public string Pdo,Interface;public int Vendor,Product; }
    sealed class Endpoint { public ulong Device, Pipe; public byte Address, Attributes; public ushort Maximum; }
    sealed class Transfer { public ulong Device, Pipe; public uint Status, NtStatus, Flags, Length; public ushort Count; public int Offset; }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate void EventCallback(IntPtr record);
    [DllImport("advapi32.dll", CharSet=CharSet.Unicode)] static extern uint StartTrace(out ulong handle, string name, IntPtr properties);
    [DllImport("advapi32.dll")] static extern uint EnableTraceEx2(ulong handle, ref Guid provider, uint control, byte level, ulong any, ulong all, uint timeout, IntPtr parameters);
    [DllImport("advapi32.dll", CharSet=CharSet.Unicode)] static extern uint ControlTrace(ulong handle, string name, IntPtr properties, uint control);
    [DllImport("advapi32.dll", EntryPoint="OpenTraceW", CharSet=CharSet.Unicode, SetLastError=true)] static extern ulong OpenTrace(IntPtr logfile);
    [DllImport("advapi32.dll")] static extern uint ProcessTrace(ulong[] handles, uint count, IntPtr start, IntPtr end);
    [DllImport("advapi32.dll")] static extern uint CloseTrace(ulong handle);

    readonly Target target;
    readonly StreamWriter output;
    readonly JavaScriptSerializer json = new JavaScriptSerializer();
    readonly object gate = new object();
    readonly ManualResetEvent rundownDone = new ManualResetEvent(false), targetLost = new ManualResetEvent(false);
    readonly ManualResetEvent hubRundownDone = new ManualResetEvent(false);
    readonly List<Controller> controllers = new List<Controller>();
    readonly List<Device> devices = new List<Device>();
    readonly List<Endpoint> endpoints = new List<Endpoint>();
    readonly List<HubDevice> hubDevices = new List<HubDevice>();
    readonly Dictionary<ulong,Endpoint> targetPipes = new Dictionary<ulong,Endpoint>();
    readonly EventCallback callback;
    ulong session, consumer = InvalidTraceHandle, targetDevice;
    IntPtr properties, logfile, loggerName;
    string sessionName;
    string armFile,readyFile;
    Thread consumerThread;
    bool capturing, disposed;
    long matchedReports, otherDeviceReports, unexpectedReports, malformedEvents, callbackFailures;
    long matchedTransferHeaders,unsupportedVersions;
    string callbackError;

    G9UsbTrace(Target value, string path) {
        target=value;
        output=new StreamWriter(path, false, new System.Text.UTF8Encoding(false)); output.AutoFlush=true;
        callback=OnEvent;
    }
    static ulong Pointer(IntPtr data, int offset, int size) {
        return size==8 ? unchecked((ulong)Marshal.ReadInt64(data,offset)) : unchecked((uint)Marshal.ReadInt32(data,offset));
    }
    static string Hex(ulong value) { return "0x"+value.ToString("x"); }
    static string Unicode(IntPtr data,int length,ref int offset) {
        int start=offset;while(offset+1<length&&Marshal.ReadInt16(data,offset)!=0)offset+=2;
        if(offset+1>=length)throw new InvalidDataException("Unterminated metadata string");
        string text=Marshal.PtrToStringUni(IntPtr.Add(data,start),(offset-start)/2);offset+=2;return text;
    }
    void Write(object value) { lock(gate) { output.WriteLine(json.Serialize(value)); } }
    void Ready(string phase,string error) {
        if(readyFile==null)return;
        string temporary=readyFile+".writing-"+Guid.NewGuid().ToString("N");
        string value=json.Serialize(new {phase=phase,utc=DateTime.UtcNow.ToString("o"),usbDevice=Hex(targetDevice),session=sessionName,error=error});
        File.WriteAllText(temporary,value,new System.Text.UTF8Encoding(false));
        if(File.Exists(readyFile))File.Replace(temporary,readyFile,null);else File.Move(temporary,readyFile);
    }
    static bool SamePorts(int[] a, int[] b) {
        if(a==null||b==null||a.Length!=b.Length)return false;
        for(int i=0;i<a.Length;i++)if(a[i]!=b[i])return false;
        return true;
    }
    static Transfer Header(IntPtr data, int length, int pointerSize, bool withData=true) {
        // UCX event 29 version 0 is a packed manifest payload, NOT an in-memory URB.
        // Five prefix pointers, then 16 fixed bytes + 14 pointers, NTSTATUS, UInt16 count.
        int payloadOffset=(withData?22:20)+19*pointerSize;
        if(length<payloadOffset)throw new InvalidDataException("Short UCX transfer header");
        var t=new Transfer {
            Device=Pointer(data,pointerSize,pointerSize), Pipe=Pointer(data,2*pointerSize,pointerSize),
            Status=unchecked((uint)Marshal.ReadInt32(data,5*pointerSize+4)),
            Flags=unchecked((uint)Marshal.ReadInt32(data,8+8*pointerSize)),
            Length=unchecked((uint)Marshal.ReadInt32(data,12+8*pointerSize)),
            NtStatus=unchecked((uint)Marshal.ReadInt32(data,16+19*pointerSize)),
            Count=withData?unchecked((ushort)Marshal.ReadInt16(data,20+19*pointerSize)):(ushort)0, Offset=payloadOffset
        };
        if(t.Count>length-payloadOffset)throw new InvalidDataException("Truncated UCX transfer data");
        return t;
    }
    void OnEvent(IntPtr record) {
        try {
            // EVENT_RECORD x64: EVENT_HEADER=80, buffer context=4, ext count=2,
            // user length=2, extended pointer=8, user pointer=8, user context=8.
            Guid eventProvider=(Guid)Marshal.PtrToStructure(IntPtr.Add(record,24),typeof(Guid));
            if(eventProvider!=Provider&&eventProvider!=HubProvider)return;
            int id=unchecked((ushort)Marshal.ReadInt16(record,40));
            int version=Marshal.ReadByte(record,42);
            int flags=unchecked((ushort)Marshal.ReadInt16(record,4));
            int ps=(flags&0x20)!=0?4:8;
            int length=unchecked((ushort)Marshal.ReadInt16(record,86));
            IntPtr data=Marshal.ReadIntPtr(record,96);
            if(eventProvider==HubProvider) {
                if(id==2){hubRundownDone.Set();return;}
                if(id==6&&length>=2*ps+4) {
                    int offset=2*ps+4;string description=Unicode(data,length,ref offset),deviceInterface=Unicode(data,length,ref offset);
                    if(offset+20>length)return;
                    int vendor=unchecked((ushort)Marshal.ReadInt16(data,offset+8)),product=unchecked((ushort)Marshal.ReadInt16(data,offset+10));offset+=18;
                    int configLength=unchecked((ushort)Marshal.ReadInt16(data,offset));offset+=2+configLength;
                    string pdo=Unicode(data,length,ref offset);
                    var info=new HubDevice{Address=Pointer(data,ps,ps),Pdo=pdo,Interface=deviceInterface,Vendor=vendor,Product=product};
                    lock(gate){hubDevices.Add(info);Write(new {kind="hub-rundown",version=version,device=Hex(info.Address),pdo=pdo,deviceInterface=deviceInterface,vendor=vendor,product=product,description=description});}
                }
                return;
            }
            if(id==3||id==5||id==6) {
                byte[] metadata=new byte[length];Marshal.Copy(data,metadata,0,length);
                Write(new {kind="rundown-record",id=id,version=version,pointerSize=ps,length=length,data=BitConverter.ToString(metadata).Replace("-","")});
            }
            if(version!=0&&!(id==27&&version==1)) { Interlocked.Increment(ref unsupportedVersions); return; }
            lock(gate) {
                if(id==2) { rundownDone.Set(); return; }
                if(id==3 && length>=ps+28) {
                    controllers.Add(new Controller { Address=Pointer(data,0,ps),
                        Bus=Marshal.ReadInt32(data,ps+4),Device=Marshal.ReadInt32(data,ps+8),Function=Marshal.ReadInt32(data,ps+12),
                        Vendor=Marshal.ReadInt32(data,ps+16),Product=Marshal.ReadInt32(data,ps+20) }); return;
                }
                if(id==5 && length>=3*ps+32) {
                    int depth=Marshal.ReadInt32(data,3*ps+4);
                    if(depth<1||depth>6) return;
                    int[] ports=new int[depth];for(int i=0;i<depth;i++)ports[i]=Marshal.ReadInt32(data,3*ps+8+i*4);
                    devices.Add(new Device {Controller=Pointer(data,0,ps),Address=Pointer(data,2*ps,ps),Ports=ports}); return;
                }
                if(id==6 && length>=3*ps+7) {
                    endpoints.Add(new Endpoint {Device=Pointer(data,0,ps),Pipe=Pointer(data,2*ps,ps),
                        Address=Marshal.ReadByte(data,3*ps+2),Attributes=Marshal.ReadByte(data,3*ps+3),
                        Maximum=unchecked((ushort)Marshal.ReadInt16(data,3*ps+4))}); return;
                }
                if(id==15 && length>=3*ps && targetDevice!=0 && Pointer(data,2*ps,ps)==targetDevice) {
                    capturing=false;targetLost.Set();return;
                }
                if(id==17 && length>=3*ps && targetDevice!=0 && Pointer(data,0,ps)==targetDevice) {
                    capturing=false;targetLost.Set();return;
                }
                if((id!=27&&id!=28&&id!=29) || !capturing)return;
                // Read only header scalars before target proof. Other devices' payloads
                // are NEVER copied, formatted, written, or included in an ETL file.
                bool withData=id!=27;
                Transfer t=Header(data,length,ps,withData);
                if(t.Device!=targetDevice) {otherDeviceReports++;return;}
                Endpoint ep;
                if(!targetPipes.TryGetValue(t.Pipe,out ep)||(t.Flags&1)==0||t.Count>ep.Maximum||t.Count>64) {unexpectedReports++;return;}
                if(!withData) {
                    matchedTransferHeaders++;
                    Write(new {kind="transfer-header",utc=DateTime.UtcNow.ToString("o"),timestamp=Marshal.ReadInt64(record,16),
                        eventId=id,eventVersion=version,manifestLength=length,endpoint="0x"+ep.Address.ToString("x2"),pipe=Hex(ep.Pipe),usbDevice=Hex(t.Device),
                        status=Hex(t.Status),ntStatus=Hex(t.NtStatus),transferLength=t.Length,payloadIncluded=false});
                    return;
                }
                byte[] bytes=new byte[t.Count];Marshal.Copy(IntPtr.Add(data,t.Offset),bytes,0,bytes.Length);
                matchedReports++;
                Write(new {kind="report",utc=DateTime.UtcNow.ToString("o"),timestamp=Marshal.ReadInt64(record,16),
                    endpoint="0x"+ep.Address.ToString("x2"),pipe=Hex(ep.Pipe),usbDevice=Hex(t.Device),
                    status=Hex(t.Status),ntStatus=Hex(t.NtStatus),transferLength=t.Length,
                    capturedLength=bytes.Length,data=BitConverter.ToString(bytes).Replace("-","")});
            }
        } catch(Exception e) { lock(gate) { callbackFailures++;if(e is InvalidDataException)malformedEvents++;if(callbackError==null)callbackError=e.GetType().Name+": "+e.Message; } }
    }
    void Check(uint result, string operation) {if(result!=0)throw new Win32Exception((int)result,operation);}
    void Start() {
        sessionName="G9UsbTrace-"+System.Diagnostics.Process.GetCurrentProcess().Id+"-"+Guid.NewGuid().ToString("N").Substring(0,8);
        byte[] name=System.Text.Encoding.Unicode.GetBytes(sessionName+"\0");
        properties=Marshal.AllocHGlobal(120+name.Length);for(int i=0;i<120+name.Length;i++)Marshal.WriteByte(properties,i,0);
        // EVENT_TRACE_PROPERTIES x64, real-time session with NO logfile name.
        Marshal.WriteInt32(properties,0,120+name.Length);Marshal.WriteInt32(properties,40,1);Marshal.WriteInt32(properties,44,0x20000);
        Marshal.WriteInt32(properties,48,64);Marshal.WriteInt32(properties,52,4);Marshal.WriteInt32(properties,56,64);
        Marshal.WriteInt32(properties,64,(int)RealTimeMode);Marshal.WriteInt32(properties,68,1);Marshal.WriteInt32(properties,116,120);
        Marshal.Copy(name,0,IntPtr.Add(properties,120),name.Length);
        Check(StartTrace(out session,sessionName,properties),"StartTrace");
        // EVENT_TRACE_LOGFILEW x64 is 448 bytes; only input fields below are set.
        // CurrentEvent at32=88bytes; LogfileHeader at120=280bytes; callback at424.
        logfile=Marshal.AllocHGlobal(448);for(int i=0;i<448;i++)Marshal.WriteByte(logfile,i,0);
        loggerName=Marshal.StringToHGlobalUni(sessionName);Marshal.WriteIntPtr(logfile,8,loggerName);
        Marshal.WriteInt32(logfile,28,unchecked((int)(RealTimeMode|EventRecordMode)));
        Marshal.WriteIntPtr(logfile,424,Marshal.GetFunctionPointerForDelegate(callback));
        consumer=OpenTrace(logfile);if(consumer==InvalidTraceHandle)throw new Win32Exception(Marshal.GetLastWin32Error(),"OpenTrace");
        consumerThread=new Thread(delegate(){ProcessTrace(new ulong[]{consumer},1,IntPtr.Zero,IntPtr.Zero);});consumerThread.IsBackground=true;consumerThread.Start();
        Guid provider=Provider;Check(EnableTraceEx2(session,ref provider,1,5,Rundown,0,0,IntPtr.Zero),"Enable UCX metadata rundown");
        Guid hub=HubProvider;Check(EnableTraceEx2(session,ref hub,1,5,Rundown,0,0,IntPtr.Zero),"Enable USBHUB3 metadata rundown");
    }
    void ProveTarget() {
        if(!rundownDone.WaitOne(10000))throw new InvalidDataException("UCX rundown did not complete; payload tracing was not enabled");
        if(!hubRundownDone.WaitOne(10000))throw new InvalidDataException("USBHUB3 rundown did not complete; payload tracing was not enabled");
        lock(gate) {
            var controllerDetails=new List<object>();foreach(var c in controllers)controllerDetails.Add(new {address=Hex(c.Address),bus=c.Bus,device=c.Device,function=c.Function,vendor=c.Vendor,product=c.Product});
            var deviceDetails=new List<object>();foreach(var d in devices)deviceDetails.Add(new {controller=Hex(d.Controller),address=Hex(d.Address),ports=d.Ports});
            var endpointDetails=new List<object>();foreach(var ep in endpoints)endpointDetails.Add(new {device=Hex(ep.Device),pipe=Hex(ep.Pipe),address=ep.Address,attributes=ep.Attributes,maximum=ep.Maximum});
            Write(new {kind="rundown-decoded",controllers=controllerDetails,devices=deviceDetails,endpoints=endpointDetails});
            var matchingControllers=new HashSet<ulong>();
            foreach(var c in controllers)if(c.Bus==target.PciBus&&c.Device==target.PciDevice&&c.Function==target.PciFunction&&c.Vendor==target.PciVendorId&&c.Product==target.PciDeviceId)matchingControllers.Add(c.Address);
            if(matchingControllers.Count!=1)throw new InvalidDataException("Controller proof is missing or ambiguous; payload tracing was not enabled");
            var matchingDevices=new HashSet<ulong>();
            foreach(var d in devices)if(matchingControllers.Contains(d.Controller)&&SamePorts(d.Ports,target.Ports))matchingDevices.Add(d.Address);
            if(matchingDevices.Count>1&&target.PdoName!=null) {
                var live=new HashSet<ulong>();foreach(var d in hubDevices)if(d.Vendor==target.VendorId&&d.Product==target.ProductId&&String.Equals(d.Pdo,target.PdoName,StringComparison.OrdinalIgnoreCase))live.Add(d.Address);
                matchingDevices.IntersectWith(live);
            }
            if(matchingDevices.Count!=1)throw new InvalidDataException("USB port proof is missing or ambiguous; payload tracing was not enabled");
            foreach(ulong value in matchingDevices)targetDevice=value;
            var addresses=new HashSet<byte>();
            foreach(var ep in endpoints)if(ep.Device==targetDevice&&(ep.Attributes&3)==3&&ep.Address>=0x81&&ep.Address<=0x84) {
                if(ep.Maximum==0||ep.Maximum>64)throw new InvalidDataException("Unexpected G9 endpoint maximum packet size");
                Endpoint old;if(targetPipes.TryGetValue(ep.Pipe,out old)&&old.Address!=ep.Address)throw new InvalidDataException("Conflicting pipe mapping");
                targetPipes[ep.Pipe]=ep;addresses.Add(ep.Address);
            }
            if(addresses.Count!=4)throw new InvalidDataException("Expected G9 interrupt IN endpoints 81,82,83,84 were not all proved");
            var map=new List<object>();foreach(var ep in targetPipes.Values)map.Add(new {endpoint="0x"+ep.Address.ToString("x2"),pipe=Hex(ep.Pipe),maximumPacket=ep.Maximum});
            Write(new {kind="target-proved",utc=DateTime.UtcNow.ToString("o"),usbDevice=Hex(targetDevice),
                controller=Hex(new List<ulong>(matchingControllers)[0]),target=target,endpoints=map,
                proof="Live PnP VID/PID and port chain matched to UCX PCI BDF/vendor/device rundown and interrupt endpoint rundown"});
        }
    }
    void Run(int seconds,bool headersOnly,string arm,string ready,bool partial) {
        armFile=arm;readyFile=ready;
        Write(new {kind="start",utc=DateTime.UtcNow.ToString("o"),mode=headersOnly?"metadata-only":"filtered-realtime",target=target});
        Start();ProveTarget();
        if(headersOnly) {Write(new {kind="metadata-complete",payloadEnabled=false});return;}
        if(armFile!=null) {
            Ready("waiting-arm",null);
            Write(new {kind="waiting-arm",utc=DateTime.UtcNow.ToString("o"),timeoutSeconds=180});
            DateTime deadline=DateTime.UtcNow.AddSeconds(180);
            bool armed=false;
            while(DateTime.UtcNow<deadline) {
                if(targetLost.WaitOne(0))throw new InvalidDataException("G9 disconnected while waiting for arm signal");
                if(File.Exists(armFile)) {
                    // A uniquely named file belongs to this capture. The explicit
                    // token avoids interpreting an unrelated/partial file as start.
                    try {armed=new FileInfo(armFile).Length<=32&&File.ReadAllText(armFile).Trim()=="ARM";}catch(IOException){}
                    if(armed)break;
                }
                Thread.Sleep(100);
            }
            if(!armed)throw new TimeoutException("No arm signal within180seconds; payload tracing was not enabled");
        }
        // Keep Default metadata enabled so a target deletion/endpoint reset stops
        // capture before a kernel device/pipe address can be recycled.
        Guid provider=Provider;Check(EnableTraceEx2(session,ref provider,1,5,(partial?0x80UL:FullData)|1,0,0,IntPtr.Zero),"Enable UCX realtime payload");
        lock(gate){capturing=true;}
        Ready("capturing",null);
        Write(new {kind="ready",utc=DateTime.UtcNow.ToString("o"),seconds=seconds,session=sessionName});
        Console.WriteLine("READY: filtered G9 capture for "+seconds+" seconds");
        bool lost=targetLost.WaitOne(seconds*1000);
        lock(gate){capturing=false;}
        Write(new {kind="capture-finished",utc=DateTime.UtcNow.ToString("o"),targetDisconnected=lost});
        Ready(lost?"target-lost":"finished",null);
    }
    void Stop() {
        if(disposed)return;disposed=true;
        if(session!=0) {Guid p=Provider;EnableTraceEx2(session,ref p,0,0,0,0,0,IntPtr.Zero);Guid hub=HubProvider;EnableTraceEx2(session,ref hub,0,0,0,0,0,IntPtr.Zero);ControlTrace(session,sessionName,properties,1);session=0;}
        if(consumer!=InvalidTraceHandle) {CloseTrace(consumer);consumer=InvalidTraceHandle;}
        if(consumerThread!=null)consumerThread.Join(3000);
        Write(new {kind="summary",utc=DateTime.UtcNow.ToString("o"),matchedReports=matchedReports,
            otherDeviceReportsDiscarded=otherDeviceReports,unexpectedTargetReportsDiscarded=unexpectedReports,
            matchedTransferHeaders=matchedTransferHeaders,unsupportedVersions=unsupportedVersions,
            malformedEvents=malformedEvents,callbackFailures=callbackFailures,callbackError=callbackError,
            eventsLost=properties==IntPtr.Zero?0:Marshal.ReadInt32(properties,88),
            realtimeBuffersLost=properties==IntPtr.Zero?0:Marshal.ReadInt32(properties,100)});
        if(loggerName!=IntPtr.Zero)Marshal.FreeHGlobal(loggerName);if(logfile!=IntPtr.Zero)Marshal.FreeHGlobal(logfile);if(properties!=IntPtr.Zero)Marshal.FreeHGlobal(properties);
        output.Dispose();GC.KeepAlive(callback);
    }
    static void SelfTest() {
        for(int ps=4;ps<=8;ps+=4) {
            int length=22+19*ps+8;IntPtr p=Marshal.AllocHGlobal(length);
            try {for(int i=0;i<length;i++)Marshal.WriteByte(p,i,0);
                if(ps==8){Marshal.WriteInt64(p,ps,0x12345678);Marshal.WriteInt64(p,2*ps,0xabcdef);}else{Marshal.WriteInt32(p,ps,0x12345678);Marshal.WriteInt32(p,2*ps,0xabcdef);}
                Marshal.WriteInt32(p,8+8*ps,1);Marshal.WriteInt32(p,12+8*ps,8);Marshal.WriteInt16(p,20+19*ps,8);
                Marshal.WriteByte(p,22+19*ps+2,0x27);Transfer t=Header(p,length,ps);
                if(t.Device!=0x12345678||t.Pipe!=0xabcdef||t.Flags!=1||t.Length!=8||t.Count!=8||Marshal.ReadByte(p,t.Offset+2)!=0x27)throw new Exception("UCX parser fixture failed");
                Transfer h=Header(p,20+19*ps,ps,false);if(h.Count!=0||h.Length!=8||h.Pipe!=0xabcdef)throw new Exception("UCX27v1 header fixture failed");
                bool rejected=false;try {Header(p,length-1,ps);}catch(InvalidDataException){rejected=true;}if(!rejected)throw new Exception("Truncated payload accepted");
            } finally {Marshal.FreeHGlobal(p);}
        }
        Console.WriteLine("PASS: x86/x64 UCX header offsets, target fields, keyboard payload and truncated-data rejection; no ETW session started");
    }
    static int Main(string[] args) {
        if(args.Length==0||Array.IndexOf(args,"--help")>=0) {Console.WriteLine("G9UsbTrace --target-json FILE --output FILE [--seconds 20] [--headers-only]\n  [--arm-file DIR\\g9-usb-arm-GUID.signal --ready-file DIR\\g9-usb-ready-GUID.json]\nRequires administrator. Metadata-only target proof precedes passive capture. Optionalarm signal: write ARM within180seconds after ready phase=waiting-arm; wait forphase=capturing before pressing test buttons. Control files must be new, in output directory, using the same32hex GUID. No other-device payload is copied or saved. --self-test starts no ETW session.");return 0;}
        if(Array.IndexOf(args,"--self-test")>=0){SelfTest();return 0;}
        G9UsbTrace trace=null;
        try {
            if(IntPtr.Size!=8)throw new InvalidOperationException("This ETW interop build requires Windows x64");
            if(!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))throw new UnauthorizedAccessException("Run the capture as administrator");
            string targetPath=null,outputPath=null,arm=null,ready=null;int seconds=20;bool headersOnly=false,partial=false;
            for(int i=0;i<args.Length;i++) {if(args[i]=="--target-json")targetPath=args[++i];else if(args[i]=="--output")outputPath=args[++i];else if(args[i]=="--seconds")seconds=int.Parse(args[++i]);else if(args[i]=="--headers-only")headersOnly=true;else if(args[i]=="--partial-data")partial=true;else if(args[i]=="--arm-file")arm=args[++i];else if(args[i]=="--ready-file")ready=args[++i];else throw new ArgumentException("Unknown argument "+args[i]);}
            if(targetPath==null||outputPath==null||seconds<1||seconds>30)throw new ArgumentException("Target/output paths required; duration must be1..30seconds");
            if((arm==null)!=(ready==null))throw new ArgumentException("Arm and ready files must be supplied together");
            if(arm!=null) {
                arm=Path.GetFullPath(arm);ready=Path.GetFullPath(ready);
                var match=System.Text.RegularExpressions.Regex.Match(Path.GetFileName(arm),"^g9-usb-arm-([0-9a-f]{32})\\.signal$");
                string folder=Path.GetDirectoryName(Path.GetFullPath(outputPath));
                if(!match.Success||Path.GetFileName(ready)!="g9-usb-ready-"+match.Groups[1].Value+".json"||!String.Equals(Path.GetDirectoryName(arm),folder,StringComparison.OrdinalIgnoreCase)||!String.Equals(Path.GetDirectoryName(ready),folder,StringComparison.OrdinalIgnoreCase)||File.Exists(arm)||File.Exists(ready))throw new ArgumentException("Control files must be new matchingGUID paths in the capture output directory");
            }
            Target t=new JavaScriptSerializer().Deserialize<Target>(File.ReadAllText(targetPath));
            if(t.VendorId!=0x1d57||t.ProductId!=0x2148||t.Ports==null||t.Ports.Length<1||t.Ports.Length>6||t.InstanceId==null||!t.InstanceId.ToUpperInvariant().StartsWith("USB\\VID_1D57&PID_2148\\"))throw new InvalidDataException("Unverified wired G9 target manifest");
            if((DateTime.UtcNow-DateTime.Parse(t.SnapshotUtc,null,System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime()).TotalMinutes>5)throw new InvalidDataException("PnP snapshot older than5minutes; prepare a fresh target");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath)));trace=new G9UsbTrace(t,outputPath);trace.Run(seconds,headersOnly,arm,ready,partial);return 0;
        } catch(Exception e) {Console.Error.WriteLine(e.GetType().Name+": "+e.Message);if(trace!=null){trace.Write(new {kind="error",utc=DateTime.UtcNow.ToString("o"),error=e.Message});trace.Ready("error",e.Message);}return 1;}
        finally {if(trace!=null)trace.Stop();}
    }
}
