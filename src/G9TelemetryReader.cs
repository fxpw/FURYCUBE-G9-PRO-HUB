using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using Furycube.G9.Telemetry;

// This reader deliberately has no feature/control-report or write API. It opens
// only G9's page000A input collection and consumes passive five-byte reports.
internal sealed class G9TelemetryReader : IDisposable
{
    readonly object gate = new object();
    bool started, disposed;
    InputSession session;
    TelemetryCache cache;
    string lastError, devicePath;
    ushort productId;

    internal TelemetrySnapshot Snapshot
    {
        get
        {
            lock (gate)
            {
                if (disposed || cache == null) return new TelemetrySnapshot { SampledAt = DateTimeOffset.UtcNow };
                return cache.Snapshot(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(90), TimeSpan.FromMinutes(5));
            }
        }
    }
    internal TelemetrySnapshot CurrentSnapshot { get { return Snapshot; } }
    internal string LastError { get { lock (gate) { return lastError; } } }
    internal string DevicePath { get { lock (gate) { return devicePath; } } }
    internal ushort ProductId { get { lock (gate) { return productId; } } }

    // Explicit and lazy. Construction/Snapshot never enumerate or open devices.
    internal void Start()
    {
        lock (gate)
        {
            if (disposed) throw new ObjectDisposedException("G9TelemetryReader");
            if (started) return;
            started = true;
        }
        ThreadPool.QueueUserWorkItem(delegate { Initialize(); });
    }

    void Initialize()
    {
        InputSession opened = null;
        try
        {
            lock (gate) { if (disposed) return; }
            string selected = SelectPath(Native.EnumeratePaths());
            if (selected == null) return;
            opened = InputSession.Open(selected, Accept, Failed);
            lock (gate)
            {
                if (disposed) { opened.Dispose(); return; }
                session = opened;
                cache = new TelemetryCache(selected);
                devicePath = selected;
                productId = opened.ProductId;
            }
            opened.Start();
        }
        catch (Exception error)
        {
            if (opened != null) opened.Dispose();
            Failed(error);
        }
    }

    void Accept(string path, ushort pid, byte[] report)
    {
        lock (gate)
        {
            if (disposed || cache == null) return;
            cache.AcceptInput(path, 0x1D57, pid, 0xA, 0, report, DateTimeOffset.UtcNow);
        }
    }

    void Failed(Exception error)
    {
        lock (gate)
        {
            if (disposed) return;
            lastError = error.Message;
            if (cache != null) cache.Reset();
            devicePath = null;
            productId = 0;
        }
    }

    public void Dispose()
    {
        InputSession close;
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            close = session;
            session = null;
            if (cache != null) cache.Reset();
            devicePath = null;
            productId = 0;
        }
        if (close != null) close.Dispose();
    }

    // Pure selection helper, also exercised by offline tests. Filter before
    // opening anything: keyboard/mouse/consumer/feature collections are excluded.
    internal static string SelectPath(IEnumerable<string> paths)
    {
        var wired = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var receiver = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in paths)
        {
            if (String.IsNullOrEmpty(path)) continue;
            if (path.IndexOf("vid_1d57&pid_2148&mi_02&col03#", StringComparison.OrdinalIgnoreCase) >= 0) wired.Add(path);
            else if (path.IndexOf("vid_1d57&pid_fa60&mi_02&col03#", StringComparison.OrdinalIgnoreCase) >= 0) receiver.Add(path);
        }
        if (wired.Count > 1) throw new InvalidOperationException("Multiple wired G9 PRO input collections; telemetry is unavailable.");
        if (wired.Count == 1) { foreach (string path in wired) return path; }
        if (receiver.Count > 1) throw new InvalidOperationException("Multiple G9 receivers; telemetry is unavailable.");
        foreach (string path in receiver) return path;
        return null;
    }

    sealed class InputSession : IDisposable
    {
        readonly object ioGate = new object();
        readonly string path;
        readonly Action<string, ushort, byte[]> accept;
        readonly Action<Exception> fail;
        SafeFileHandle handle;
        ManualResetEvent completed;
        RegisteredWaitHandle registration;
        Timer completionRetry;
        IntPtr buffer, overlapped;
        bool pending, stopped, released;
        long readGeneration;
        internal ushort ProductId { get; private set; }

        InputSession(string path, SafeFileHandle handle, ushort pid,
            Action<string, ushort, byte[]> accept, Action<Exception> fail)
        {
            this.path = path;
            this.handle = handle;
            this.ProductId = pid;
            this.accept = accept;
            this.fail = fail;
            try {
                completed = new ManualResetEvent(false);
                buffer = Marshal.AllocHGlobal(5);
                overlapped = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(Native.Overlapped)));
            } catch {
                if (overlapped != IntPtr.Zero) Marshal.FreeHGlobal(overlapped);
                if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
                if (completed != null) completed.Dispose();
                throw;
            }
        }

        internal static InputSession Open(string path, Action<string, ushort, byte[]> accept, Action<Exception> fail)
        {
            SafeFileHandle handle = Native.CreateFile(path, 0x80000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new Win32Exception(error, "Cannot open G9 passive input collection.");
            }
            try
            {
                var attributes = new Native.Attributes { Size = Marshal.SizeOf(typeof(Native.Attributes)) };
                if (!Native.HidD_GetAttributes(handle, ref attributes)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot identify G9 input collection.");
                IntPtr preparsed;
                if (!Native.HidD_GetPreparsedData(handle, out preparsed)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot inspect G9 input capabilities.");
                Native.Caps caps;
                try
                {
                    if (Native.HidP_GetCaps(preparsed, out caps) != 0x00110000) throw new InvalidOperationException("Invalid G9 HID capabilities.");
                }
                finally { Native.HidD_FreePreparsedData(preparsed); }
                ushort expectedPid = path.IndexOf("pid_2148&", StringComparison.OrdinalIgnoreCase) >= 0 ? (ushort)0x2148 : (ushort)0xFA60;
                if (attributes.ProductId != expectedPid ||
                    !TelemetryProtocol.IsSupportedInputCollection(attributes.VendorId, attributes.ProductId,
                    caps.UsagePage, caps.Usage, caps.InputReportByteLength))
                    throw new InvalidOperationException("G9 input identity/capabilities did not match the selected normal device.");
                return new InputSession(path, handle, attributes.ProductId, accept, fail);
            }
            catch { handle.Dispose(); throw; }
        }

        internal void Start()
        {
            Exception error = null;
            lock (ioGate)
            {
                if (stopped || released) return;
                try { BeginRead(); }
                catch (Exception caught) { StopAfterError(); error = caught; }
            }
            if (error != null) fail(error);
        }

        void BeginRead()
        {
            completed.Reset();
            var value = new Native.Overlapped { Event = completed.SafeWaitHandle.DangerousGetHandle() };
            Marshal.StructureToPtr(value, overlapped, false);
            long token = ++readGeneration;
            // Register before issuing the IRP: even an immediate completion is
            // observed, and a registration allocation failure cannot orphan IO.
            registration = ThreadPool.RegisterWaitForSingleObject(completed, Completed,
                token, Timeout.Infinite, true);
            uint bytes;
            pending = true;
            bool immediate = Native.ReadFile(handle, buffer, 5, out bytes, overlapped);
            int error = immediate ? 0 : Marshal.GetLastWin32Error();
            if (!immediate && error != 997)
            {
                pending = false;
                throw new Win32Exception(error, "G9 passive input read failed.");
            }
            // ReadFile signals the event for immediate and pending completion.
        }

        void Completed(object state, bool timedOut)
        {
            byte[] report = null;
            Exception error = null;
            lock (ioGate)
            {
                if (released || !(state is long) || (long)state != readGeneration) return;
                if (registration != null) { registration.Unregister(null); registration = null; }
                if (completionRetry != null) { completionRetry.Dispose(); completionRetry = null; }
                if (!pending) { if (stopped) Release(); return; }
                uint bytes;
                bool ok = Native.GetOverlappedResult(handle, overlapped, out bytes, false);
                int code = ok ? 0 : Marshal.GetLastWin32Error();
                if (!ok && code == 996)
                {
                    // Never release an OVERLAPPED/buffer while its IRP exists.
                    // Do not Reset here: completion between GetResult and Reset
                    // would lose the signal. A rare stale/incomplete notification
                    // is retried by a timer without blocking a worker thread.
                    completionRetry = new Timer(delegate(object token) { Completed(token, true); }, state, 10, Timeout.Infinite);
                    return;
                }
                pending = false;
                if (stopped) { Release(); return; }
                if (!ok || bytes != 5)
                {
                    error = !ok ? (Exception)new Win32Exception(code, "G9 passive input stopped.") :
                        new InvalidOperationException("G9 passive input report had an unexpected length.");
                    StopAfterError();
                }
                else
                {
                    report = new byte[5];
                    Marshal.Copy(buffer, report, 0, 5);
                }
            }
            // Never call reader/cache code with ioGate held (Dispose locks in
            // the opposite direction). Old completion is dropped after Dispose.
            if (report != null) accept(path, ProductId, report);
            if (report != null && error == null)
            {
                // Publish the completed report before a new read can complete,
                // so concurrent callbacks cannot reorder DPI-stage changes.
                lock (ioGate)
                {
                    if (!stopped && !released)
                    {
                        try { BeginRead(); }
                        catch (Exception caught) { StopAfterError(); error = caught; }
                    }
                }
            }
            if (error != null) fail(error);
        }

        void StopAfterError()
        {
            stopped = true;
            if (pending) Native.CancelIoEx(handle, overlapped);
            else Release();
        }

        public void Dispose()
        {
            lock (ioGate)
            {
                if (stopped) return;
                stopped = true;
                if (pending)
                {
                    // Completion callback retains the session, SafeFileHandle,
                    // event and unmanaged memory until cancellation completes.
                    // No blocking wait or thread join can stall the GUI here.
                    Native.CancelIoEx(handle, overlapped);
                }
                else Release();
            }
        }

        void Release()
        {
            if (released || pending) return;
            released = true;
            if (registration != null) { registration.Unregister(null); registration = null; }
            if (completionRetry != null) { completionRetry.Dispose(); completionRetry = null; }
            if (handle != null) { handle.Dispose(); handle = null; }
            if (completed != null) { completed.Dispose(); completed = null; }
            if (overlapped != IntPtr.Zero) { Marshal.FreeHGlobal(overlapped); overlapped = IntPtr.Zero; }
            if (buffer != IntPtr.Zero) { Marshal.FreeHGlobal(buffer); buffer = IntPtr.Zero; }
        }
    }

    static class Native
    {
        [StructLayout(LayoutKind.Sequential)] internal struct InterfaceData
        { internal int Size; internal Guid ClassGuid; internal int Flags; internal IntPtr Reserved; }
        [StructLayout(LayoutKind.Sequential)] internal struct Attributes
        { internal int Size; internal ushort VendorId, ProductId, Version; }
        [StructLayout(LayoutKind.Sequential)] internal struct Caps
        {
            internal ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] internal ushort[] Reserved;
            internal ushort LinkNodes, InputButtons, InputValues, InputData, OutputButtons,
                OutputValues, OutputData, FeatureButtons, FeatureValues, FeatureData;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct Overlapped
        { internal IntPtr Internal, InternalHigh; internal uint Offset, OffsetHigh; internal IntPtr Event; }

        [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid guid);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr SetupDiGetClassDevs(ref Guid guid, string enumerator, IntPtr window, uint flags);
        [DllImport("setupapi.dll", SetLastError = true)]
        static extern bool SetupDiEnumDeviceInterfaces(IntPtr info, IntPtr device, ref Guid guid, uint index, ref InterfaceData data);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr info, ref InterfaceData data, IntPtr detail, uint size, out uint required, IntPtr device);
        [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr info);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint mode, uint flags, IntPtr template);
        [DllImport("hid.dll", SetLastError = true)] internal static extern bool HidD_GetAttributes(SafeFileHandle handle, ref Attributes attributes);
        [DllImport("hid.dll", SetLastError = true)] internal static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out IntPtr data);
        [DllImport("hid.dll")] internal static extern bool HidD_FreePreparsedData(IntPtr data);
        [DllImport("hid.dll")] internal static extern int HidP_GetCaps(IntPtr data, out Caps caps);
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool ReadFile(SafeFileHandle handle, IntPtr data, uint length, out uint read, IntPtr overlapped);
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool GetOverlappedResult(SafeFileHandle handle, IntPtr overlapped, out uint bytes, bool wait);
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool CancelIoEx(SafeFileHandle handle, IntPtr overlapped);

        internal static List<string> EnumeratePaths()
        {
            Guid guid;
            HidD_GetHidGuid(out guid);
            IntPtr info = SetupDiGetClassDevs(ref guid, null, IntPtr.Zero, 0x12);
            if (info == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot enumerate HID input collections.");
            var paths = new List<string>();
            try
            {
                for (uint index = 0; ; index++)
                {
                    var data = new InterfaceData { Size = Marshal.SizeOf(typeof(InterfaceData)) };
                    if (!SetupDiEnumDeviceInterfaces(info, IntPtr.Zero, ref guid, index, ref data))
                    {
                        int code = Marshal.GetLastWin32Error();
                        if (code == 259) break;
                        throw new Win32Exception(code, "Cannot enumerate HID interface.");
                    }
                    uint required;
                    SetupDiGetDeviceInterfaceDetail(info, ref data, IntPtr.Zero, 0, out required, IntPtr.Zero);
                    if (required < 8 || required > 65536) throw new InvalidOperationException("Invalid HID interface path length.");
                    IntPtr detail = Marshal.AllocHGlobal((int)required);
                    try
                    {
                        Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                        if (!SetupDiGetDeviceInterfaceDetail(info, ref data, detail, required, out required, IntPtr.Zero))
                            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot read HID interface path.");
                        paths.Add(Marshal.PtrToStringUni(IntPtr.Add(detail, 4)));
                    }
                    finally { Marshal.FreeHGlobal(detail); }
                }
            }
            finally { SetupDiDestroyDeviceInfoList(info); }
            return paths;
        }
    }
}
