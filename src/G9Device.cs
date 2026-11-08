using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

// Pure packet checks are shared by the executable and offline fixture tests.
internal static class G9Wire {
    internal const int KeymapLength = 59;
    internal static byte[] ReadKeysRequest() { return new byte[] { 0xA0, 8, 59, 0, 1, 0, 0, 0 }; }
    internal static byte[] ReadProfileRequest() { return new byte[] { 0xA0, 12, 8, 0, 1, 0, 0, 0 }; }
    internal static byte[] SelectProfileOne() { return new byte[] { 12, 10, 1, 0xFE, 1, 0xFE, 0, 0, 0, 0 }; }
    static void Require(bool value, string message) { if (!value) throw new InvalidDataException(message); }
    internal static void ValidateAck(byte[] data, bool ready) {
        Require(data != null && data.Length == 8 && data[0] == 0xA0 && data[1] == (ready ? 1 : 0),
            ready ? "Keymap/profile read was not granted" : "A previous read is pending; reconnect the mouse before retrying");
        for (int i = 2; i < 8; i++) Require(data[i] == 0, "Unexpected read-state response");
    }
    internal static byte[] DecodeKeys(byte[] data) {
        Require(data != null && (data.Length == 59 || data.Length == 64), "Unexpected keymap transfer length");
        Require(data[0] == 8 && data[1] == 59 && data[2] == 1, "Unexpected keymap report, length or profile; no settings accepted");
        int sum = 0; for (int i = 3; i < 57; i++) sum += data[i];
        Require(sum == ((data[57] << 8) | data[58]), "Keymap checksum mismatch");
        byte[] result = new byte[59]; Buffer.BlockCopy(data, 0, result, 0, result.Length); return result;
    }
    internal static byte[] WithZero(byte[] current) {
        byte[] result = DecodeKeys(current);
        result[12] = 0x11; result[13] = 0; result[14] = 0x27;
        int sum = 0; for (int i = 3; i < 57; i++) sum += result[i];
        result[57] = (byte)(sum >> 8); result[58] = (byte)sum; return result;
    }
    internal static byte[] DecodeProfile(byte[] data) {
        Require(data != null && data.Length == 8 && data[0] == 12 && data[1] == 8 && data[2] == 1,
            "Unexpected active-profile report, length or selector");
        Require(data[3] > 0 && data[3] <= 8 && data[5] > 0 && data[5] <= 8 &&
            (data[3] ^ data[4]) == 255 && (data[5] ^ data[6]) == 255, "Invalid active-profile complement bytes");
        return new byte[] { data[3], data[5] };
    }
    internal static void ValidateSend(byte[] data) {
        Require(data != null, "Packet missing");
        if (data.Length == 59 && data[0] == 8) { DecodeKeys(data); return; }
        if (Equal(data, ReadKeysRequest()) || Equal(data, ReadProfileRequest()) || Equal(data, SelectProfileOne())) return;
        throw new InvalidDataException("Command is not permitted by the G9 configuration transport");
    }
    internal static bool Equal(byte[] a, byte[] b) {
        if (a == null || b == null || a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false; return true;
    }
    internal static string Hex(byte[] bytes) { return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant(); }
}

internal static class G9Hid {
    [StructLayout(LayoutKind.Sequential)] internal struct DeviceInterface {
        internal uint Size; internal Guid ClassGuid; internal uint Flags; internal IntPtr Reserved;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct Attributes {
        internal uint Size; internal ushort Vendor, Product, Version;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct Caps {
        internal ushort Usage, UsagePage, InputBytes, OutputBytes, FeatureBytes;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst=17)] internal ushort[] Reserved;
        internal ushort LinkNodes, InputButtons, InputValues, InputIndices, OutputButtons, OutputValues, OutputIndices,
            FeatureButtons, FeatureValues, FeatureIndices;
    }
    [DllImport("hid.dll")] internal static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("hid.dll", SetLastError=true)] internal static extern bool HidD_GetAttributes(SafeFileHandle handle, ref Attributes attributes);
    [DllImport("hid.dll", SetLastError=true)] internal static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out IntPtr data);
    [DllImport("hid.dll")] internal static extern bool HidD_FreePreparsedData(IntPtr data);
    [DllImport("hid.dll")] internal static extern int HidP_GetCaps(IntPtr data, out Caps caps);
    [DllImport("hid.dll", SetLastError=true)] internal static extern bool HidD_GetFeature(SafeFileHandle handle, byte[] data, int length);
    [DllImport("hid.dll", SetLastError=true)] internal static extern bool HidD_SetFeature(SafeFileHandle handle, byte[] data, int length);
    [DllImport("setupapi.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    internal static extern IntPtr SetupDiGetClassDevs(ref Guid guid, IntPtr enumerator, IntPtr parent, uint flags);
    [DllImport("setupapi.dll", SetLastError=true)]
    internal static extern bool SetupDiEnumDeviceInterfaces(IntPtr devices, IntPtr device, ref Guid guid, uint index, ref DeviceInterface item);
    [DllImport("setupapi.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    internal static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr devices, ref DeviceInterface item, IntPtr detail, uint bytes, out uint required, IntPtr device);
    [DllImport("setupapi.dll")] internal static extern bool SetupDiDestroyDeviceInfoList(IntPtr devices);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    internal static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    internal static SafeFileHandle Open(string path) {
        // Feature IOCTLs permit a metadata/zero-access handle; keyboard streams are never opened or read.
        SafeFileHandle result = CreateFile(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (result.IsInvalid) { int error = Marshal.GetLastWin32Error(); result.Dispose(); throw new Win32Exception(error, "Cannot open the G9 configuration interface"); }
        return result;
    }
    internal sealed class Candidate {
        internal string Path; internal ushort Product, Version, FeatureBytes;
    }
    internal static List<Candidate> Enumerate() {
        Guid guid; HidD_GetHidGuid(out guid);
        IntPtr devices = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, 0x12);
        if (devices == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot enumerate HID devices");
        var result = new List<Candidate>();
        try {
            for (uint index = 0; ; index++) {
                var item = new DeviceInterface { Size = (uint)Marshal.SizeOf(typeof(DeviceInterface)) };
                if (!SetupDiEnumDeviceInterfaces(devices, IntPtr.Zero, ref guid, index, ref item)) {
                    int error = Marshal.GetLastWin32Error(); if (error == 259) break; throw new Win32Exception(error, "HID interface enumeration failed");
                }
                uint required; SetupDiGetDeviceInterfaceDetail(devices, ref item, IntPtr.Zero, 0, out required, IntPtr.Zero);
                if (required < 8 || required > 65536) throw new InvalidDataException("Unexpected HID interface path length");
                IntPtr detail = Marshal.AllocHGlobal((int)required);
                try {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetail(devices, ref item, detail, required, out required, IntPtr.Zero))
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot read HID interface path");
                    string path = Marshal.PtrToStringUni(IntPtr.Add(detail, 4)); string lower = path.ToLowerInvariant();
                    if ((!lower.Contains("vid_1d57&pid_2148") && !lower.Contains("vid_1d57&pid_fa60")) || !lower.Contains("mi_02&col04")) continue;
                    using (SafeFileHandle handle = Open(path)) {
                        var attributes = new Attributes { Size = (uint)Marshal.SizeOf(typeof(Attributes)) };
                        if (!HidD_GetAttributes(handle, ref attributes)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot read HID attributes");
                        if (attributes.Vendor != 0x1D57 || (attributes.Product != 0x2148 && attributes.Product != 0xFA60)) continue;
                        IntPtr preparsed; if (!HidD_GetPreparsedData(handle, out preparsed)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot read HID capabilities");
                        Caps caps;
                        try { if (HidP_GetCaps(preparsed, out caps) != 0x00110000) throw new InvalidDataException("Invalid HID capabilities"); }
                        finally { HidD_FreePreparsedData(preparsed); }
                        if (caps.UsagePage == 0x0B && caps.Usage == 0 && caps.FeatureBytes >= 64)
                            result.Add(new Candidate { Path = path, Product = attributes.Product, Version = attributes.Version, FeatureBytes = caps.FeatureBytes });
                    }
                } finally { Marshal.FreeHGlobal(detail); }
            }
        } finally { SetupDiDestroyDeviceInfoList(devices); }
        return result;
    }
}

internal sealed class G9Connection : IDisposable {
    readonly SafeFileHandle handle;
    internal readonly G9Hid.Candidate Device;
    internal G9Connection(ushort requestedPid) {
        List<G9Hid.Candidate> all = G9Hid.Enumerate(); ushort wanted = requestedPid;
        if (wanted == 0) { wanted = 0xFA60; foreach (var item in all) if (item.Product == 0x2148) wanted = 0x2148; }
        var matches = all.FindAll(delegate(G9Hid.Candidate item) { return item.Product == wanted; });
        if (matches.Count != 1) throw new InvalidOperationException(matches.Count == 0 ? "G9 configuration interface is not connected" : "Multiple matching G9 devices; refusing an ambiguous transaction");
        Device = matches[0]; handle = G9Hid.Open(Device.Path);
        Console.WriteLine("TARGET VID=1d57 PID={0:x4} featureBytes={1} path={2}", Device.Product, Device.FeatureBytes, Device.Path);
    }
    byte[] Get(byte report, int length) {
        if (length > Device.FeatureBytes) throw new InvalidDataException("Feature request exceeds the device report buffer");
        byte[] data = new byte[length]; data[0] = report;
        if (!G9Hid.HidD_GetFeature(handle, data, data.Length))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "GetFeature " + report.ToString("x2") + " failed; stop and reconnect before another transaction");
        Console.WriteLine("GET id={0:x2} len={1} data={2}", report, data.Length, G9Wire.Hex(data)); return data;
    }
    void Send(byte[] packet) {
        G9Wire.ValidateSend(packet);
        if (packet.Length > Device.FeatureBytes) throw new InvalidDataException("Feature packet exceeds the device report buffer");
        if (!G9Hid.HidD_SetFeature(handle, packet, packet.Length))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SetFeature failed; no further commands sent");
        Console.WriteLine("SEND len={0} data={1}", packet.Length, G9Wire.Hex(packet));
    }
    byte[] Read(byte[] request, byte report, int length) {
        G9Wire.ValidateAck(Get(0xA0, 8), false); Thread.Sleep(100);
        Send(request); Thread.Sleep(500);
        G9Wire.ValidateAck(Get(0xA0, 8), true); Thread.Sleep(100);
        // A granted read is consumed exactly once, including on validation failure.
        byte[] response = Get(report, length); Thread.Sleep(150); return response;
    }
    internal byte[] ReadKeys() { return G9Wire.DecodeKeys(Read(G9Wire.ReadKeysRequest(), 8, Device.Product == 0x2148 ? 59 : 64)); }
    internal byte[] ReadProfile() { return G9Wire.DecodeProfile(Read(G9Wire.ReadProfileRequest(), 12, 8)); }
    internal void WriteKeys(byte[] expected) {
        expected = G9Wire.DecodeKeys(expected);
        Console.WriteLine("SELECTING PROFILE=1 MACRO PROFILE=1");
        Send(G9Wire.SelectProfileOne()); Thread.Sleep(500);
        Send(expected); Thread.Sleep(500);
        byte[] actual = ReadKeys();
        if (!G9Wire.Equal(actual, expected)) throw new InvalidDataException("Readback differs from requested settings; physical output is not verified");
    }
    public void Dispose() { handle.Dispose(); }
}

internal static class G9DeviceProgram {
    static string SaveBackup(byte[] keys, ushort pid, string directory) {
        keys = G9Wire.DecodeKeys(keys); Directory.CreateDirectory(directory);
        string name = "keymap-" + DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmssfffffff'Z'") + "-" + pid.ToString("x4") + "-" + Guid.NewGuid().ToString("N") + ".bin";
        string path = Path.Combine(directory, name);
        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { file.Write(keys, 0, keys.Length); file.Flush(true); }
        Console.WriteLine("BACKUP " + path); return path;
    }
    static int Main(string[] args) {
        Console.OutputEncoding = new UTF8Encoding(false);
        try {
            if (args.Length == 0) { Console.WriteLine("G9Device inspect | profile | zero | backup | restore <backup.bin> | caps [--pid 2148|fa60] [--backup-dir path]"); return 2; }
            string command = args[0];
            if (command != "inspect" && command != "profile" && command != "zero" && command != "backup" && command != "restore" && command != "caps") throw new ArgumentException("Unknown command");
            ushort pid = 0; string restore = null;
            string backupDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FURYCUBE-G9-PRO", "backups");
            for (int i = 1; i < args.Length; i++) {
                if (args[i] == "--pid" && i + 1 < args.Length) {
                    if (!ushort.TryParse(args[++i], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out pid) || (pid != 0x2148 && pid != 0xFA60)) throw new ArgumentException("Unsupported PID");
                } else if (args[i] == "--backup-dir" && i + 1 < args.Length) backupDirectory = Path.GetFullPath(args[++i]);
                else if (command == "restore" && restore == null) restore = Path.GetFullPath(args[i]);
                else throw new ArgumentException("Unexpected command argument");
            }
            byte[] restored = null;
            if (command == "restore") {
                if (restore == null) throw new ArgumentException("Restore requires a 59-byte keymap backup");
                byte[] bytes = File.ReadAllBytes(restore);
                if (bytes.Length != 59) throw new InvalidDataException("Backup must contain exactly 59 bytes"); restored = G9Wire.DecodeKeys(bytes);
            }
            if (command == "caps") {
                foreach (var candidate in G9Hid.Enumerate()) if (pid == 0 || candidate.Product == pid)
                    Console.WriteLine("CAPS VID=1d57 PID={0:x4} version={1:x4} featureBytes={2} path={3}", candidate.Product, candidate.Version, candidate.FeatureBytes, candidate.Path);
                Console.WriteLine("CAPS ONLY: no feature reports requested or settings written."); return 0;
            }
            using (var device = new G9Connection(pid)) {
                if (command == "profile") { byte[] profile = device.ReadProfile(); Console.WriteLine("ACTIVE PROFILE={0} MACRO PROFILE={1}", profile[0], profile[1]); return 0; }
                byte[] current = device.ReadKeys(); Console.WriteLine("BUTTON6 function={0:x2} modifier={1:x2} usage={2:x2}", current[12], current[13], current[14]);
                if (command == "inspect") { Console.WriteLine("KEYMAP VALIDATED. A physical keypress is a separate check."); return 0; }
                SaveBackup(current, device.Device.Product, backupDirectory);
                if (command == "backup") return 0;
                device.WriteKeys(command == "zero" ? G9Wire.WithZero(current) : restored);
                Console.WriteLine("CONFIG VERIFIED. Tool is exiting. Test the sixth button in a text field."); return 0;
            }
        } catch (Exception error) { Console.Error.WriteLine("ERROR: " + error.Message); return 1; }
    }
}
