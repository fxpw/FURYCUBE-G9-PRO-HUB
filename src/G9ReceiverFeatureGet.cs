using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

// Used only by the isolated G9Device CLI. Unsettled requests stay rooted until process exit.
// Never releases request resources while completion remains unconfirmed.
internal sealed class G9ReceiverFeatureRequest {
    [StructLayout(LayoutKind.Sequential)] internal struct Overlapped {
        internal IntPtr Internal,InternalHigh;
        internal uint Offset,OffsetHigh;
        internal IntPtr Event;
    }
    internal static readonly List<G9ReceiverFeatureRequest> Retained=new List<G9ReceiverFeatureRequest>();
    internal readonly byte[] Data;
    internal readonly SafeFileHandle Handle;
    internal ManualResetEvent Event;
    internal IntPtr NativeOverlapped,Buffer;
    GCHandle pin;
    bool handleRef;
    internal bool Settled,Released;
    internal G9ReceiverFeatureRequest(SafeFileHandle handle,byte report,int length) {
        Handle=handle;Data=new byte[length];Data[0]=report;
        try {
            Handle.DangerousAddRef(ref handleRef);
            pin=GCHandle.Alloc(Data,GCHandleType.Pinned);Buffer=pin.AddrOfPinnedObject();
            Event=new ManualResetEvent(false);
            int size=Marshal.SizeOf(typeof(Overlapped));NativeOverlapped=Marshal.AllocHGlobal(size);
            Marshal.Copy(new byte[size],0,NativeOverlapped,size);
            var value=new Overlapped {Event=Event.SafeWaitHandle.DangerousGetHandle()};
            Marshal.StructureToPtr(value,NativeOverlapped,false);
            lock(Retained)Retained.Add(this);
        }catch {Settled=true;Release();throw;}
    }
    internal void Release() {
        if(!Settled)throw new InvalidOperationException("Cannot release an unsettled request");
        if(Released)throw new InvalidOperationException("Request released twice");
        Released=true;
        lock(Retained)Retained.Remove(this);
        if(NativeOverlapped!=IntPtr.Zero){Marshal.FreeHGlobal(NativeOverlapped);NativeOverlapped=IntPtr.Zero;}
        if(pin.IsAllocated)pin.Free();Buffer=IntPtr.Zero;
        if(Event!=null){Event.Dispose();Event=null;}
        if(handleRef){Handle.DangerousRelease();handleRef=false;}
    }
}
internal interface IG9ReceiverFeatureIo {
    bool Issue(G9ReceiverFeatureRequest op,out uint returned,out int error);
    bool Result(G9ReceiverFeatureRequest op,out uint returned,out int error);
    int Wait(G9ReceiverFeatureRequest op,int milliseconds);
    bool Cancel(G9ReceiverFeatureRequest op,out int error);
    void Pause(G9ReceiverFeatureRequest op,int milliseconds);
}
internal sealed class NativeReceiverFeatureIo : IG9ReceiverFeatureIo {
    internal static readonly NativeReceiverFeatureIo Instance=new NativeReceiverFeatureIo();
    [DllImport("kernel32.dll",SetLastError=true,ExactSpelling=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    static extern bool DeviceIoControl(SafeFileHandle handle,uint code,IntPtr input,uint inputLength,
        IntPtr output,uint outputLength,out uint returned,IntPtr overlapped);
    [DllImport("kernel32.dll",SetLastError=true,ExactSpelling=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    static extern bool GetOverlappedResult(SafeFileHandle handle,IntPtr overlapped,out uint returned,
        [MarshalAs(UnmanagedType.Bool)]bool wait);
    [DllImport("kernel32.dll",SetLastError=true,ExactSpelling=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    static extern bool CancelIoEx(SafeFileHandle handle,IntPtr overlapped);
    public bool Issue(G9ReceiverFeatureRequest op,out uint returned,out int error) {
        bool ok=DeviceIoControl(op.Handle,0xB0192,op.Buffer,(uint)op.Data.Length,
            op.Buffer,(uint)op.Data.Length,out returned,op.NativeOverlapped);
        error=ok?0:Marshal.GetLastWin32Error();return ok;
    }
    public bool Result(G9ReceiverFeatureRequest op,out uint returned,out int error) {
        bool ok=GetOverlappedResult(op.Handle,op.NativeOverlapped,out returned,false);
        error=ok?0:Marshal.GetLastWin32Error();return ok;
    }
    public int Wait(G9ReceiverFeatureRequest op,int milliseconds){return op.Event.WaitOne(milliseconds)?0:258;}
    public bool Cancel(G9ReceiverFeatureRequest op,out int error) {
        bool ok=CancelIoEx(op.Handle,op.NativeOverlapped);error=ok?0:Marshal.GetLastWin32Error();return ok;
    }
    public void Pause(G9ReceiverFeatureRequest op,int milliseconds){Thread.Sleep(milliseconds);}
}
internal static class G9ReceiverFeatureGet {
    static bool CheckResult(IG9ReceiverFeatureIo api,G9ReceiverFeatureRequest op,out uint returned,out int error) {
        bool ok=api.Result(op,out returned,out error);
        // Success and ERROR_OPERATION_ABORTED confirm the request has finished.
        // Unexpected errors are conservatively retained/cancelled rather than
        // treating an invalid API state as proof that kernel references ended.
        if(ok||error==995)op.Settled=true;
        return ok;
    }
    internal static byte[] Read(SafeFileHandle handle,byte report,int length) { return Read(handle,report,length,NativeReceiverFeatureIo.Instance); }
    internal static byte[] Read(SafeFileHandle handle,byte report,int length,IG9ReceiverFeatureIo api) {
        if(!((report==0xA0&&length==8)||(length==64&&(report==4||report==6||report==7||report==8||report==0x0B||report==0x0C))))throw new InvalidDataException("Unqualified receiver GET shape");
        var op=new G9ReceiverFeatureRequest(handle,report,length);
        try {
            uint returned;int error;
            bool issued=api.Issue(op,out returned,out error);
            if(!issued&&error!=997){op.Settled=true;throw new Win32Exception(error,"Overlapped GET was not queued; no retry");}
            bool done=CheckResult(api,op,out returned,out error);
            if(!done&&!op.Settled) {
                int wait=api.Wait(op,1500);
                if(wait==0)done=CheckResult(api,op,out returned,out error);
                if(!done&&!op.Settled) {
                    int cancelError;api.Cancel(op,out cancelError);
                    // Cancellation requests are not completion. Give the kernel
                    // 500ms to finish before aborting this CLI command. Never
                    // free an operation whose completion is still unknown.
                    for(int attempt=0;attempt<=25;attempt++) {
                        bool completed=CheckResult(api,op,out returned,out error);
                        if(completed||op.Settled)throw new TimeoutException("Receiver GET timed out; cancellation/completion confirmed. Stop this session.");
                        if(attempt<25)api.Pause(op,20);
                    }
                    throw new TimeoutException("Receiver GET cancellation did not complete; the isolated device helper must exit. Stop this session.");
                }
            }
            if(!done)throw new Win32Exception(error,"Overlapped GET completed cancelled; no retry");
            if(returned!=(uint)length)throw new InvalidDataException("Unexpected completed GET byte count; no retry");
            return op.Data;
        }finally {
            if(op.Settled)op.Release();
            // Retained and DangerousAddRef survive an unsettled failure.
            // The connection is poisoned and the isolated CLI exits on error.
        }
    }
}
