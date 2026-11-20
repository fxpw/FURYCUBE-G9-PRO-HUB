using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using Microsoft.Win32.SafeHandles;
using Furycube.G9.Bindings;
using Furycube.G9.Oled;
using Furycube.G9.Settings;

// Pure packet checks are shared by the executable and offline fixture tests.
internal static class G9Wire {
    internal const int KeymapLength = 59;
    internal static void RequireQualifiedOledTransport(ushort product) {
        Require(product == 0x2148 || product == 0xFA60, "OLED requires a normal USB mouse or 2.4 GHz receiver interface.");
    }
    internal static byte[] ReadKeysRequest() { return ReadKeysRequest(1); }
    internal static byte[] ReadKeysRequest(int profile) { return ReadSettingsRequest(8, profile); }
    internal static void ValidateProfileNumber(int profile) { Require(profile >= 1 && profile <= 5, "G9 profile must be1..5"); }
    internal static int SettingsReadLength(byte report) {
        switch (report) { case 4:return 52; case 6:return 9; case 7:return 8; case 8:return 59; case 0x0A:return 120; case 0x0C:return 8; default:throw new InvalidDataException("Unsupported settings read report"); }
    }
    // Receiver USB transfers use a 64-byte envelope independently of the
    // requested logical record length. A0 itself remains an eight-byte ACK.
    // Long receiver records are deliberately excluded until reassembly is qualified.
    internal static int ConfigurationReadLength(ushort product, byte report, int logicalLength) {
        Require(product == 0x2148 || product == 0xFA60, "Unsupported configuration product");
        int expected = report == 0xA0 || report == 0x0B ? 8 : SettingsReadLength(report);
        Require(logicalLength == expected, "Unexpected logical configuration read length");
        if (product == 0x2148 || report == 0xA0) return logicalLength;
        Require(logicalLength <= 64, "Receiver long-read framing is not qualified; no query was sent");
        return 64;
    }
    internal static byte[] ConfigurationReply(ushort product, byte report, int logicalLength, byte[] response) {
        int transferLength = ConfigurationReadLength(product, report, logicalLength);
        Require(response != null && response.Length == transferLength, "Unexpected configuration transfer length");
        // Transport tail bytes may contain previous data. Never interpret them
        // as a second record or include them in logical checksums or OLED markers.
        byte[] result = new byte[logicalLength]; Buffer.BlockCopy(response, 0, result, 0, result.Length);
        return result;
    }
    internal static byte[] ConfigurationSendBuffer(ushort product, int featureBytes, byte[] logicalPacket) {
        ValidateSend(logicalPacket);
        Require(product == 0x2148 || product == 0xFA60, "Unsupported configuration product");
        Require(featureBytes >= logicalPacket.Length, "Configuration packet exceeds the device feature capacity");
        if(product == 0x2148) return (byte[])logicalPacket.Clone();
        Require(featureBytes == 134, "Unqualified receiver configuration feature capacity");
        // Match the OEM sender's USB envelope without changing logical lengths,
        // checksums or the configuration command allowlist.
        byte[] envelope = new byte[featureBytes];
        Buffer.BlockCopy(logicalPacket, 0, envelope, 0, logicalPacket.Length);
        return envelope;
    }
    // Discovery must already have verified OL9 for the receiver. The qualified
    // radio path uses a SET04 carrier and submits its exact 56-byte length.
    internal static byte[] OledSetBuffer(ushort product, int featureBytes, byte[] report) {
        RequireQualifiedOledTransport(product);
        if(product == 0xFA60) return RadioOledFeatureBuffer("OL9", featureBytes, report, true);
        OledShortProtocol.ValidateWriteChunk(report);
        Require(featureBytes >= 64, "OLED feature capacity is too small");
        return (byte[])report.Clone();
    }
    // Separate experimental SET04 carrier. Never admit this packet through
    // ValidateSend/SendSettings, and never enable it for stock/OL5.
    internal static void ValidateOl6RadioIdentity(ushort product, int featureBytes, byte[] firmware, byte[] profile) {
        Require(product==0xFA60 && featureBytes==134,"OL6 carrier requires the exact normal receiver interface");
        var caps=OledProtocol.DecodeFirmwareCapabilities(firmware);
        Require(caps.Extension=="OL6" || caps.Extension=="OL7" || caps.Extension=="OL8" || caps.Extension=="OL9","Reserved OLED carrier requires OL6..OL9 on this same handle; no frame was sent");
        byte[] active=DecodeProfile(profile);
        Require(active[0]==1,"Experimental OL6 carrier requires active profile1; no profile is selected automatically");
    }
    internal static byte[] Ol6RadioCarrier(byte[] chunk) {
        OledShortProtocol.ValidateWriteChunk(chunk);
        byte[] packet=new byte[56];
        byte[] header={4,56,1,0,1,0x3F,0xA5,0x5A,chunk[7],chunk[8],chunk[10],40};
        Buffer.BlockCopy(header,0,packet,0,header.Length);
        Buffer.BlockCopy(chunk,12,packet,12,12);
        // Byte24 is a deliberately INVALID native DPI stage. Valid settings
        // have1..8 there, even when their other flags are nonzero.
        Buffer.BlockCopy(chunk,24,packet,25,28);
        ushort crc=OledProtocol.Crc16CcittFalse(packet,0,53);
        packet[53]=(byte)crc;packet[54]=(byte)(crc>>8);packet[55]=6;
        return packet;
    }
    internal static byte[] RadioOledFeatureBuffer(string extension, int featureBytes, byte[] chunk, bool logicalFeatureLength) {
        Require(featureBytes==134, "Radio OLED requires the exact receiver feature capacity");
        Require(extension=="OL6" || extension=="OL7" || extension=="OL8" || extension=="OL9",
            "Reserved OLED carrier requires OL6..OL9");
        Require(!logicalFeatureLength || extension=="OL9", "The 56-byte feature experiment requires OL9");
        byte[] packet=extension=="OL9" ? Ol9RadioCarrier(chunk) : Ol6RadioCarrier(chunk);
        if(logicalFeatureLength) return packet;
        var buffer=new byte[featureBytes];
        Buffer.BlockCopy(packet,0,buffer,0,packet.Length);
        return buffer;
    }
    internal static byte[] Ol9RadioCarrier(byte[] chunk) {
        OledShortProtocol.ValidateWriteChunk(chunk);
        byte[] packet=new byte[56];
        byte[] header={4,56,1,0,1,0x3F,chunk[7],chunk[8],chunk[10],40};
        Buffer.BlockCopy(header,0,packet,0,header.Length);
        Buffer.BlockCopy(chunk,12,packet,10,14);Buffer.BlockCopy(chunk,26,packet,25,25);
        packet[54]=chunk[51];packet[55]=9;
        int sum=0;for(int i=3;i<50;i++)sum+=packet[i];packet[50]=(byte)(sum>>8);packet[51]=(byte)sum;
        byte[] input=new byte[53];Buffer.BlockCopy(packet,0,input,0,52);input[52]=packet[54];
        ushort crc=OledProtocol.Crc16CcittFalse(input,0,input.Length);packet[52]=(byte)crc;packet[53]=(byte)(crc>>8);
        return packet;
    }
    internal static byte[] ReadSettingsRequest(byte report, int profile) {
        ValidateProfileNumber(profile); return new byte[] { 0xA0, report, (byte)SettingsReadLength(report), 0, (byte)profile, 0, 0, 0 };
    }
    internal static byte[] ReadProfileRequest() { return new byte[] { 0xA0, 12, 8, 0, 1, 0, 0, 0 }; }
    internal static byte[] ReadFirmwareRequest() { return new byte[] { 0xA0, 11, 8, 0, 1, 0, 0, 0 }; }
    internal static byte[] SelectProfileOne() { return new byte[] { 12, 10, 1, 0xFE, 1, 0xFE, 0, 0, 0, 0 }; }
    static void Require(bool value, string message) { if (!value) throw new InvalidDataException(message); }
    internal static void ValidateAck(byte[] data, bool ready) {
        Require(data != null && data.Length == 8 && data[0] == 0xA0 && data[1] == (ready ? 1 : 0),
            ready ? "Keymap/profile read was not granted" : "A previous read is pending; reconnect the mouse before retrying");
        for (int i = 2; i < 8; i++) Require(data[i] == 0, "Unexpected read-state response");
    }
    internal static byte[] DecodeKeys(byte[] data) { return DecodeKeys(data, 1); }
    internal static byte[] DecodeKeys(byte[] data, int expectedProfile) {
        ValidateProfileNumber(expectedProfile);
        Require(data != null && (data.Length == 59 || data.Length == 64), "Unexpected keymap transfer length");
        Require(data[0] == 8 && data[1] == 59 && data[2] == expectedProfile, "Unexpected keymap report, length or profile; no settings accepted");
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
        Require(data[3] > 0 && data[3] <= 5 && data[5] > 0 && data[5] <= 5 &&
            (data[3] ^ data[4]) == 255 && (data[5] ^ data[6]) == 255, "Invalid active-profile complement bytes");
        Require(data[3] <= data[5] && data[7] == 0, "Invalid active-profile order or logical padding");
        return new byte[] { data[3], data[5] };
    }
    internal static void ValidateSend(byte[] data) {
        Require(data != null, "Packet missing");
        if (data.Length == 59 && data[0] == 8) { DecodeKeys(data, data[2]); return; }
        if (Equal(data, ReadFirmwareRequest())) return;
        if (data.Length == 8 && data[0] == 0xA0 && data[3] == 0 && data[4] >= 1 && data[4] <= 5 && data[5] == 0 && data[6] == 0 && data[7] == 0) {
            if (Equal(data, ReadSettingsRequest(data[1], data[4]))) return;
        }
        if (data.Length >= 3 && (data[0] == 4 || data[0] == 5 || data[0] == 6)) {
            try { MouseSettingsCodec.ValidateSetPacket(data, data[2]); return; }
            catch (Exception error) { throw new InvalidDataException("Invalid settings write packet", error); }
        }
        if ((data.Length == 8 || data.Length == 10) && data[0] == 12 && data[1] == data.Length) {
            DecodeProfileSet(data); return;
        }
        throw new InvalidDataException("Command is not permitted by the G9 configuration transport");
    }
    internal static byte[] BuildProfileSet(int active, int maximum) {
        ValidateProfileNumber(active); ValidateProfileNumber(maximum); Require(active <= maximum, "Active profile exceeds the configured profile count");
        return new byte[] { 12, 10, (byte)active, (byte)(active ^ 255), (byte)maximum, (byte)(maximum ^ 255), 0, 0, 0, 0 };
    }
    internal static byte[] DecodeProfileSet(byte[] frame) {
        Require(frame != null && (frame.Length == 8 || frame.Length == 10) && frame[0] == 12 && frame[1] == frame.Length, "Invalid profile-setting frame");
        ValidateProfileNumber(frame[2]); ValidateProfileNumber(frame[4]);
        Require(frame[2] <= frame[4] && (frame[2] ^ frame[3]) == 255 && (frame[4] ^ frame[5]) == 255, "Invalid profile-setting complement or profile count");
        for (int i = 6; i < frame.Length; i++) Require(frame[i] == 0, "Unexpected profile-setting padding");
        return new byte[] { frame[2], frame[4] };
    }
    internal static byte[] DecodeFirmware(byte[] data) {
        Require(data != null && data.Length == 8 && data[0] == 11 && (data[1] == 5 || data[1] == 8) && data[2] == 1,
            "Unexpected firmware-version report, length or selector");
        return (byte[])data.Clone();
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
    internal static SafeFileHandle Open(string path, bool receiverTransaction = false) {
        // Enumeration and wired features retain metadata access. The receiver
        // needs the OEM-compatible RW/OVERLAPPED configuration handle.
        SafeFileHandle result = CreateFile(path, receiverTransaction ? 0xC0000000u : 0u, 3, IntPtr.Zero, 3,
            receiverTransaction ? 0x40000000u : 0u, IntPtr.Zero);
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

internal sealed class G9Connection : IDisposable, IOledDeviceSession, IOledRuntimeStatusDeviceSession, IG9SettingsSession {
    readonly SafeFileHandle handle;
    readonly bool oledStream;
    readonly object oledGate = new object();
    bool oledAuthorized, oledRuntimeStatusAuthorized, oledShortReportsAuthorized, oledRadioAuthorized;
    bool receiverSessionFailed;
    internal readonly G9Hid.Candidate Device;
    public ushort VendorId { get { return 0x1D57; } }
    public ushort ProductId { get { return Device.Product; } }
    internal G9Connection(ushort requestedPid, string expectedPath = null, bool oledStream = false) {
        this.oledStream = oledStream;
        List<G9Hid.Candidate> all = G9Hid.Enumerate(); ushort wanted = requestedPid;
        if (wanted == 0) { wanted = 0xFA60; foreach (var item in all) if (item.Product == 0x2148) wanted = 0x2148; }
        var matches = all.FindAll(delegate(G9Hid.Candidate item) { return item.Product == wanted; });
        if (matches.Count != 1) throw new InvalidOperationException(matches.Count == 0 ? "G9 configuration interface is not connected" : "Multiple matching G9 devices; refusing an ambiguous transaction");
        Device = matches[0];
        if(expectedPath!=null&&!String.Equals(expectedPath,Device.Path,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("The selected mouse changed; reread the device before any operation.");
        if(Device.Product==0xFA60 && Device.FeatureBytes!=134)throw new InvalidDataException("Unqualified receiver feature capacity; no transaction opened");
        handle = G9Hid.Open(Device.Path,Device.Product==0xFA60);
        Console.WriteLine("TARGET VID=1d57 PID={0:x4} featureBytes={1} path={2}", Device.Product, Device.FeatureBytes, Device.Path);
    }
    byte[] Get(byte report, int length) {
        if(receiverSessionFailed)throw new InvalidOperationException("Receiver session failed; close it and reread the device before another operation");
        if (length > Device.FeatureBytes) throw new InvalidDataException("Feature request exceeds the device report buffer");
        byte[] data;
        if(Device.Product==0xFA60) {
            try { data=G9ReceiverFeatureGet.Read(handle,report,length); }
            catch { receiverSessionFailed=true;throw; }
        } else {
            data = new byte[length]; data[0] = report;
            if (!G9Hid.HidD_GetFeature(handle, data, data.Length))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "GetFeature " + report.ToString("x2") + " failed; stop and reconnect before another transaction");
        }
        Console.WriteLine("GET id={0:x2} len={1} data={2}", report, data.Length, G9Wire.Hex(data)); return data;
    }
    void Send(byte[] packet) {
        if(receiverSessionFailed)throw new InvalidOperationException("Receiver session failed; close it and reread the device before another operation");
        byte[] envelope=G9Wire.ConfigurationSendBuffer(Device.Product,Device.FeatureBytes,packet);
        if (!G9Hid.HidD_SetFeature(handle, envelope, envelope.Length)) {
            if(Device.Product==0xFA60)receiverSessionFailed=true;
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SetFeature failed; no further commands sent");
        }
        Console.WriteLine("SEND len={0} logicalBytes={1} data={2}", envelope.Length,packet.Length,G9Wire.Hex(envelope));
    }
    byte[] Read(byte[] request, byte report, int length) {
        try {
            int transferLength = G9Wire.ConfigurationReadLength(Device.Product, report, length);
            if (transferLength > Device.FeatureBytes) throw new InvalidDataException("Configuration read exceeds the device report buffer; no query was sent");
            Furycube.G9.Protocol.G9ReadHandshake.ValidateIdle(Get(0xA0, 8)); Thread.Sleep(100);
            Send(request);
            var deadline=System.Diagnostics.Stopwatch.StartNew();
            Furycube.G9.Protocol.G9ReadHandshake.WaitForReady(delegate {return Get(0xA0,8);},Thread.Sleep,delegate {return deadline.ElapsedMilliseconds;},2000,100,500);
            Thread.Sleep(100);
            // A granted read is consumed exactly once, including on validation failure.
            byte[] response = Get(report, transferLength); Thread.Sleep(150);
            return G9Wire.ConfigurationReply(Device.Product, report, length, response);
        } catch {
            if(Device.Product==0xFA60)receiverSessionFailed=true;
            throw;
        }
    }
    internal byte[] ReadKeys() { return ReadKeys(1); }
    internal byte[] ReadKeys(int profile) { return G9Wire.DecodeKeys(Read(G9Wire.ReadKeysRequest(profile), 8, 59),profile); }
    internal byte[] ReadProfile() { return G9Wire.DecodeProfile(Read(G9Wire.ReadProfileRequest(), 12, 8)); }
    internal byte[] ReadFirmware() { return G9Wire.DecodeFirmware(Read(G9Wire.ReadFirmwareRequest(), 11, 8)); }
    ushort IG9SettingsSession.SettingsProductId { get { return Device.Product; } }
    string IG9SettingsSession.SettingsDevicePath { get { return Device.Path; } }
    void EnsureSettingsIdentity() {
        if(receiverSessionFailed)throw new InvalidOperationException("Receiver session failed; close it and reread the device before another operation");
        if((Device.Product!=0x2148 && Device.Product!=0xFA60) || handle.IsInvalid || handle.IsClosed || Device.FeatureBytes<(Device.Product==0x2148?120:64))
            throw new InvalidOperationException("Settings require the same open G9 configuration interface.");
        var attributes=new G9Hid.Attributes {Size=(uint)Marshal.SizeOf(typeof(G9Hid.Attributes))};
        if(!G9Hid.HidD_GetAttributes(handle,ref attributes) || attributes.Vendor!=0x1D57 || attributes.Product!=Device.Product)
            throw new InvalidOperationException("Settings device identity changed; transaction stopped.");
    }
    byte[] IG9SettingsSession.QuerySettings(byte report,int profile) {
        EnsureSettingsIdentity(); return Read(G9Wire.ReadSettingsRequest(report,profile),report,G9Wire.SettingsReadLength(report));
    }
    void IG9SettingsSession.SendSettings(byte[] packet) {
        EnsureSettingsIdentity(); Send(packet); Thread.Sleep(500);
    }
    internal G9SettingsSnapshot ReadSettings(int? profile,string backupDirectory) {
        lock(oledGate) {
            oledAuthorized=oledRuntimeStatusAuthorized=false; EnsureSettingsIdentity();
            return G9SettingsTransactions.Read(this,profile,backupDirectory);
        }
    }
    internal Dictionary<string,object> ApplySettings(int? profile,string field,string value,string backupDirectory) {
        lock(oledGate) {
            oledAuthorized=oledRuntimeStatusAuthorized=false; EnsureSettingsIdentity();
            return G9SettingsTransactions.Apply(this,profile,field,value,backupDirectory);
        }
    }
    internal Dictionary<string,object> RestoreProfilePair(string snapshotFile,string backupDirectory) {
        lock(oledGate) {
            oledAuthorized=oledRuntimeStatusAuthorized=false;EnsureSettingsIdentity();
            return G9SettingsTransactions.RestoreProfilePair(this,snapshotFile,backupDirectory);
        }
    }
    internal void WriteOl6RadioExperiment(byte[] frame, bool logicalFeatureLength) {
        lock(oledGate) {
            OledProtocol.ValidateFrame(frame);
            if(Device.Product!=0xFA60 || Device.FeatureBytes!=134)throw new InvalidDataException("OL6 radio experiment requires receiver FA60/134; no feature command sent");
            EnsureSettingsIdentity();
            byte[] firmware=ReadFirmware();
            // Reject old firmware before any further query or reserved packet.
            string extension=OledProtocol.DecodeFirmwareCapabilities(firmware).Extension;
            if(extension!="OL6" && extension!="OL7" && extension!="OL8" && extension!="OL9")throw new InvalidDataException("OL6..OL9 firmware is required; no reserved carrier sent");
            if(logicalFeatureLength && extension!="OL9")throw new InvalidDataException("The 56-byte feature experiment requires OL9; no reserved carrier sent");
            byte[] profileReport=Read(G9Wire.ReadProfileRequest(),0x0C,8);
            G9Wire.ValidateOl6RadioIdentity(Device.Product,Device.FeatureBytes,firmware,profileReport);
            for(int index=0;index<12;index++) {
                EnsureSettingsIdentity();
                byte[] pixels=new byte[40];Buffer.BlockCopy(frame,index*40,pixels,0,40);
                byte[] chunk=OledShortProtocol.BuildWriteChunk(index/3,(index%3)*40,pixels,index==0);
                byte[] envelope=G9Wire.RadioOledFeatureBuffer(extension,Device.FeatureBytes,chunk,logicalFeatureLength);
                if(!G9Hid.HidD_SetFeature(handle,envelope,envelope.Length)) {
                    receiverSessionFailed=true;
                    throw new Win32Exception(Marshal.GetLastWin32Error(),"Radio OLED carrier failed; no remaining chunks sent");
                }
                var logicalPacket=new byte[56];
                Buffer.BlockCopy(envelope,0,logicalPacket,0,logicalPacket.Length);
                Console.WriteLine("OL6_RADIO_SEND index={0} firmware={2} len=56 data={1} usbLen={3}",index,G9Wire.Hex(logicalPacket),extension,envelope.Length);
                Thread.Sleep(500);
            }
            Console.WriteLine("OL6_RADIO_EXPERIMENT_SENT=12 DISPLAY_VERIFIED=false; no automatic follow-up query or retry");
        }
    }
    void EnsureOledIdentity(bool discovery = false) {
        if(receiverSessionFailed)throw new InvalidOperationException("Receiver session failed; close it and reread the device before another operation");
        if ((Device.Product != 0x2148 && Device.Product != 0xFA60) || handle.IsInvalid || handle.IsClosed || Device.FeatureBytes < OledShortProtocol.FeatureBytes) {
            oledAuthorized = oledRuntimeStatusAuthorized = false;
            throw new OledFirmwareUnsupportedException("OLED operations require the same open normal G9 configuration interface.");
        }
        var attributes = new G9Hid.Attributes { Size = (uint)Marshal.SizeOf(typeof(G9Hid.Attributes)) };
        if (!G9Hid.HidD_GetAttributes(handle, ref attributes) || attributes.Vendor != 0x1D57 || attributes.Product != Device.Product) {
            oledAuthorized = oledRuntimeStatusAuthorized = false; throw new OledFirmwareUnsupportedException("The opened device identity changed; OLED operations are disabled.");
        }
        if (Device.Product == 0xFA60 && !discovery && !oledRadioAuthorized)
            throw new OledFirmwareUnsupportedException("OLED over the receiver requires OL9 discovery on this same handle.");
    }
    public byte[] ReadOledFirmware() {
        lock (oledGate) {
            G9Wire.RequireQualifiedOledTransport(Device.Product);
            oledAuthorized = oledRuntimeStatusAuthorized = oledShortReportsAuthorized = oledRadioAuthorized = false; EnsureOledIdentity(true);
            byte[] reply = ReadFirmware();
            try {
                var capabilities = OledProtocol.DecodeFirmwareCapabilities(reply);
                OledProtocol.ValidateDeviceFirmware(capabilities, VendorId, ProductId);
                if(Device.Product == 0xFA60) RequireRadioOledProfile();
                oledRuntimeStatusAuthorized = capabilities.SupportsRuntimeStatus;
                oledShortReportsAuthorized = capabilities.SupportsShortWrites;
                oledRadioAuthorized = capabilities.SupportsRadioWrites;
            }
            catch (InvalidDataException error) { throw new OledFirmwareUnsupportedException(error.Message); }
            catch (NotSupportedException error) { throw new OledFirmwareUnsupportedException(error.Message); }
            oledAuthorized = true; return reply;
        }
    }
    void RequireRadioOledProfile() {
        if(ReadProfile()[0] != 1)
            throw new OledFirmwareUnsupportedException("OLED over the receiver requires active profile 1. No profile is selected automatically.");
    }
    public void SendOledFeature(byte[] report) {
        lock (oledGate) {
            if (!oledAuthorized) throw new InvalidOperationException("This device handle has not advertised supported OLED firmware");
            EnsureOledIdentity();
            if (!oledShortReportsAuthorized) throw new OledFirmwareUnsupportedException("OL4 or later is required for USB OLED writes.");
            byte[] envelope = G9Wire.OledSetBuffer(Device.Product, Device.FeatureBytes, report);
            // Stream discovery checks profile 1 once. OL9 checks the active
            // profile for each fragment, without a host GET between frames.
            if (Device.Product == 0xFA60 && !oledStream && report[7] == 0 && report[8] == 0) RequireRadioOledProfile();
            if (!G9Hid.HidD_SetFeature(handle, envelope, envelope.Length)) {
                if(Device.Product == 0xFA60) receiverSessionFailed = true;
                oledAuthorized = oledRuntimeStatusAuthorized = false; throw new Win32Exception(Marshal.GetLastWin32Error(), "OLED SetFeature failed; remaining pages were not sent");
            }
            Console.WriteLine("OLED_SEND len={0} usbLen={4} page={1} x={2} flags={3}", report.Length, report[7], report[8], report[10], envelope.Length);
        }
    }
    public byte[] ReadOledRuntimeStatus() {
        lock (oledGate) {
            if (!oledAuthorized || !oledRuntimeStatusAuthorized)
                throw new OledFirmwareUnsupportedException("OL3/OL4 discovery on this same open handle is required; stock/OL2 never receive GET 0x22.");
            EnsureOledIdentity();
            try {
                if(Device.Product == 0xFA60) {
                    // Direct report 22 remains unqualified on the receiver.
                    // Use the same checksum-verified sensor read as radio settings.
                    RequireRadioOledProfile();
                    var sensor = MouseSettingsCodec.DecodeSensorProfile(1, Read(G9Wire.ReadSettingsRequest(4, 1), 4, G9Wire.SettingsReadLength(4)));
                    byte stage = sensor.SelectedStage.Value;
                    return OledRuntimeStatusCodec.EncodeConfiguredDpi(1, stage, sensor.DpiValues[stage - 1]);
                }
                G9Wire.ValidateAck(Get(0xA0, 8), false); Thread.Sleep(100);
                EnsureOledIdentity();
                // This dedicated query is not admitted by the generic configuration
                // allowlist. Only OL3/OL4 on this verified handle can reach this send.
                byte[] query = OledRuntimeStatusCodec.BuildReadRequest(oledShortReportsAuthorized);
                if (!G9Hid.HidD_SetFeature(handle, query, query.Length))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "OLED runtime status query failed");
                Console.WriteLine("RUNTIME_QUERY len=8");
                var deadline=System.Diagnostics.Stopwatch.StartNew();
                Furycube.G9.Protocol.G9ReadHandshake.WaitForReady(delegate {return Get(0xA0,8);},Thread.Sleep,delegate {return deadline.ElapsedMilliseconds;},2000,100,500);
                Thread.Sleep(100);
                EnsureOledIdentity();
                byte[] response = Get(0x22, oledShortReportsAuthorized ? 64 : OledRuntimeStatusCodec.FeatureBytes); Thread.Sleep(150);
                OledRuntimeStatusCodec.Decode(response, DateTimeOffset.UtcNow);
                return response; // Consume exactly once even when validation fails.
            } catch { oledAuthorized = oledRuntimeStatusAuthorized = false; throw; }
        }
    }
    internal void WriteKeys(byte[] expected) { WriteKeys(expected,1,false); }
    internal void WriteKeys(byte[] expected,int profile,bool requireActive) {
        oledAuthorized = oledRuntimeStatusAuthorized = false;
        expected = G9Wire.DecodeKeys(expected,profile);
        BindingCatalog.EnsureLeftClick(expected,profile);
        byte[] before=ReadProfile();
        if(requireActive && before[0]!=profile) throw new InvalidDataException("Requested button profile is not active; no keymap was written.");
        Console.WriteLine("WRITING KEYMAP PROFILE={0}; active profile and profile count are preserved",profile);
        Send(expected); Thread.Sleep(500);
        byte[] actual = ReadKeys(profile);
        if (!G9Wire.Equal(actual, expected)) throw new InvalidDataException("Readback differs from requested settings; physical output is not verified");
        if(!G9Wire.Equal(before,ReadProfile())) throw new InvalidDataException("Active profile or profile count changed during keymap write; operation stopped.");
    }
    public void Dispose() { lock (oledGate) { oledAuthorized = oledRuntimeStatusAuthorized = false; handle.Dispose(); } }
}

internal static class G9DeviceProgram {
    static byte HexByte(string text) {
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) text = text.Substring(2);
        byte value; if (!byte.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value)) throw new ArgumentException("Invalid hexadecimal binding byte"); return value;
    }
    static void PrintMapping(byte[] keymap,int profile) {
        var rows = new List<object>();
        foreach (var button in BindingCatalog.Buttons) {
            int offset = 3 + button.Index * 3; BindingAction action = BindingCatalog.Resolve(keymap[offset], keymap[offset + 1], keymap[offset + 2]);
            rows.Add(new { index = button.Index, name = button.Name, physicalLabel = button.PhysicalLabel, visible = button.IsVisible,
                function = (int)keymap[offset], param1 = (int)keymap[offset + 1], param2 = (int)keymap[offset + 2], bindingId = action == null ? null : action.Id });
        }
        Console.WriteLine("MAPPING_JSON " + new JavaScriptSerializer().Serialize(new { profile = profile, keymapHex = G9Wire.Hex(keymap), buttons = rows }));
    }
    static string SaveBackup(byte[] keys, ushort pid, string directory) {
        keys = G9Wire.DecodeKeys(keys,keys[2]); Directory.CreateDirectory(directory);
        string name = "keymap-" + DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmssfffffff'Z'") + "-" + pid.ToString("x4") + "-" + Guid.NewGuid().ToString("N") + ".bin";
        string path = Path.Combine(directory, name);
        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { file.Write(keys, 0, keys.Length); file.Flush(true); }
        Console.WriteLine("BACKUP " + path); return path;
    }
    static int Main(string[] args) {
        Console.OutputEncoding = new UTF8Encoding(false);
        try {
            if (args.Length == 0) { Console.WriteLine("G9Device mapping [--active|--profile1..5] | assign --profile N --index N --function HEX --param1 HEX --param2 HEX | settings-read [--profile N] | settings-set --field ID --value VALUE [--profile N] | profile-restore --file <settings snapshot.json> | inspect | profile | firmware | zero | backup | restore <backup.bin> | oled-caps | oled-status | oled-frame --file <480-byte-frame.bin> | oled-stream | caps [--pid2148|fa60] [--backup-dir path]"); return 2; }
            string command = args[0];
            if (command != "inspect" && command != "mapping" && command != "profile" && command != "profile-restore" && command != "firmware" && command != "zero" && command != "backup" && command != "restore" && command != "assign" && command != "settings-read" && command != "settings-set" && command != "oled-caps" && command != "oled-status" && command != "oled-frame" && command != "oled-stream" && command != "ol6-radio-frame" && command != "caps") throw new ArgumentException("Unknown command");
            ushort pid = 0; string restore = null, expectedPath = null;
            string frameFile = null; byte[] oledFrame = null;
            int? assignmentIndex = null; byte? function = null; byte param1 = 0, param2 = 0;
            int? requestedProfile=null; bool activeMapping=false; string settingField=null,settingValue=null;
            bool logicalFeatureLength=false;
            string profileRestoreFile=null;
            string backupDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FURYCUBE-G9-PRO", "backups");
            for (int i = 1; i < args.Length; i++) {
                if (args[i] == "--pid" && i + 1 < args.Length) {
                    if (!ushort.TryParse(args[++i], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out pid) || (pid != 0x2148 && pid != 0xFA60)) throw new ArgumentException("Unsupported PID");
                } else if(args[i]=="--expected-path"&&i+1<args.Length&&expectedPath==null) {
                    expectedPath=args[++i];if(expectedPath.Length<20||expectedPath.Length>1024||expectedPath.IndexOfAny(new[]{'"','\r','\n','\0'})>=0)throw new ArgumentException("Invalid expected HID path");
                } else if (args[i] == "--backup-dir" && i + 1 < args.Length) backupDirectory = Path.GetFullPath(args[++i]);
                else if(args[i]=="--profile" && i+1<args.Length && (command=="mapping" || command=="assign" || command=="inspect" || command=="backup" || command=="zero" || command=="restore" || command=="settings-read" || command=="settings-set")) {
                    int parsed; if(requestedProfile.HasValue || !Int32.TryParse(args[++i],NumberStyles.None,CultureInfo.InvariantCulture,out parsed) || parsed<1 || parsed>5)
                        throw new ArgumentException("Profile must be1..5 and appear once"); requestedProfile=parsed;
                }
                else if(command=="mapping" && args[i]=="--active" && !activeMapping) activeMapping=true;
                else if(command=="settings-set" && args[i]=="--field" && i+1<args.Length && settingField==null) settingField=args[++i];
                else if(command=="settings-set" && args[i]=="--value" && i+1<args.Length && settingValue==null) settingValue=args[++i];
                else if(command=="profile-restore" && args[i]=="--file" && i+1<args.Length && profileRestoreFile==null) profileRestoreFile=Path.GetFullPath(args[++i]);
                else if (command == "assign" && args[i] == "--index" && i + 1 < args.Length) { int parsed; if (!int.TryParse(args[++i], out parsed) || parsed < 0 || parsed >= 18) throw new ArgumentException("Button index must be 0..17"); assignmentIndex = parsed; }
                else if (command == "assign" && args[i] == "--function" && i + 1 < args.Length) function = HexByte(args[++i]);
                else if (command == "assign" && args[i] == "--param1" && i + 1 < args.Length) param1 = HexByte(args[++i]);
                else if (command == "assign" && args[i] == "--param2" && i + 1 < args.Length) param2 = HexByte(args[++i]);
                else if(command=="ol6-radio-frame" && args[i]=="--logical-feature-length" && !logicalFeatureLength) logicalFeatureLength=true;
                else if ((command == "oled-frame" || command == "ol6-radio-frame") && args[i] == "--file" && i + 1 < args.Length && frameFile == null) frameFile = Path.GetFullPath(args[++i]);
                else if (command == "restore" && restore == null) restore = Path.GetFullPath(args[i]);
                else throw new ArgumentException("Unexpected command argument");
            }
            byte[] restored = null;
            if(command=="profile-restore") {
                if(profileRestoreFile==null)throw new ArgumentException("Profile-pair restore requires --file pointing to a settings snapshot JSON.");
                if(pid!=0&&pid!=0x2148)throw new ArgumentException("Profile-pair restore requires wired G9 PID2148.");
                if(!File.Exists(profileRestoreFile))throw new FileNotFoundException("Settings snapshot file was not found.",profileRestoreFile);
                pid=0x2148;
            }
            if(activeMapping && requestedProfile.HasValue) throw new ArgumentException("Choose either --active or --profile");
            if(command=="settings-set" && (String.IsNullOrEmpty(settingField) || settingValue==null)) throw new ArgumentException("Settings write requires --field and --value");
            if(logicalFeatureLength && (pid!=0xFA60 || expectedPath==null))
                throw new ArgumentException("The 56-byte feature experiment requires --pid fa60 and --expected-path");
            if (command == "oled-frame" || command == "ol6-radio-frame") {
                if (frameFile == null || new FileInfo(frameFile).Length != OledProtocol.BufferBytes) throw new InvalidDataException("OLED upload requires --file pointing to a 480-byte packed frame");
                oledFrame = File.ReadAllBytes(frameFile); OledProtocol.ValidateFrame(oledFrame);
            }
            if (command == "assign") { if (!assignmentIndex.HasValue || !function.HasValue) throw new ArgumentException("Assign requires --index and --function"); BindingCatalog.Validate(function.Value, param1, param2); }
            if (command == "restore") {
                if (restore == null) throw new ArgumentException("Restore requires a 59-byte keymap backup");
                byte[] bytes = File.ReadAllBytes(restore);
                if (bytes.Length != 59) throw new InvalidDataException("Backup must contain exactly 59 bytes"); restored = G9Wire.DecodeKeys(bytes,requestedProfile??1); BindingCatalog.EnsureLeftClick(restored,requestedProfile??1);
            }
            if (command == "caps") {
                foreach (var candidate in G9Hid.Enumerate()) if (pid == 0 || candidate.Product == pid)
                    Console.WriteLine("CAPS VID=1d57 PID={0:x4} version={1:x4} featureBytes={2} path={3}", candidate.Product, candidate.Version, candidate.FeatureBytes, candidate.Path);
                Console.WriteLine("CAPS ONLY: no feature reports requested or settings written."); return 0;
            }
            using (var device = new G9Connection(pid,expectedPath,command=="oled-stream")) {
                if(command=="ol6-radio-frame") { device.WriteOl6RadioExperiment(oledFrame,logicalFeatureLength);return 0; }
                if (command == "oled-stream") return OledStreaming.RunUtf8(Console.OpenStandardInput(), Console.Out, new NativeOledTransport(device));
                if (command == "oled-caps" || command == "oled-frame" || command == "oled-status") {
                    var uploader = new OledSender(new NativeOledTransport(device)); FirmwareCapabilities capabilities = uploader.Discover();
                    Console.WriteLine("OLED_CAPS_JSON " + new JavaScriptSerializer().Serialize(new { supported = true, extension = capabilities.Extension, width = capabilities.Width,
                        height = capabilities.VisibleHeight, bufferBytes = capabilities.BufferBytes, supportsRuntimeStatus = capabilities.SupportsRuntimeStatus,
                        supportsShortWrites = capabilities.SupportsShortWrites, supportsRadioWrites=capabilities.SupportsRadioWrites, physicalVerified = false }));
                    if (command == "oled-status") {
                        if (!capabilities.SupportsRuntimeStatus) { Console.WriteLine("OLED_STATUS_UNSUPPORTED"); return 4; }
                        Console.WriteLine("OLED_STATUS_DATA " + Convert.ToBase64String(uploader.ReadRuntimeStatusFrame()));
                        Console.WriteLine("OLED_STATUS_SENT 1"); return 0;
                    }
                    if (command == "oled-frame") {
                        OledTransferResult result = uploader.WriteFrame(oledFrame);
                        Console.WriteLine("OLED_REPORTS_SENT={0} DISPLAY_VERIFIED=false", result.FeatureReportsSent);
                    }
                    return 0;
                }
                if(command=="settings-read") {
                    var snapshot=device.ReadSettings(requestedProfile,backupDirectory);
                    Console.WriteLine("SETTINGS_BACKUP "+snapshot.ArchivePath);
                    Console.WriteLine("SETTINGS_JSON "+new JavaScriptSerializer().Serialize(snapshot.ToDto())); return 0;
                }
                if(command=="settings-set") {
                    Dictionary<string,object> result=device.ApplySettings(requestedProfile,settingField,settingValue,backupDirectory);
                    Console.WriteLine("SETTINGS_BACKUP "+(string)result["backupPath"]);
                    Console.WriteLine("SETTINGS_APPLIED_JSON "+new JavaScriptSerializer().Serialize(result)); return 0;
                }
                if(command=="profile-restore") {
                    Dictionary<string,object> result=device.RestoreProfilePair(profileRestoreFile,backupDirectory);
                    Console.WriteLine("PROFILE_RESTORE_BACKUP "+(string)result["archivePath"]);
                    Console.WriteLine("PROFILE_RESTORE_JSON "+new JavaScriptSerializer().Serialize(result));return 0;
                }
                if (command == "profile") { byte[] profile = device.ReadProfile(); Console.WriteLine("ACTIVE PROFILE={0} MAX PROFILES={1}", profile[0], profile[1]); return 0; }
                if (command == "firmware") { Console.WriteLine("FIRMWARE_RESPONSE " + G9Wire.Hex(device.ReadFirmware())); return 0; }
                int selectedProfile=requestedProfile??1; byte[] profileGuard=null;
                if(activeMapping || requestedProfile.HasValue) {
                    profileGuard=device.ReadProfile(); if(activeMapping) selectedProfile=profileGuard[0];
                    if(profileGuard[0]!=selectedProfile) throw new InvalidDataException("Requested profile is not currently active; select it explicitly before this operation.");
                }
                byte[] current = device.ReadKeys(selectedProfile); Console.WriteLine("BUTTON6 function={0:x2} modifier={1:x2} usage={2:x2}", current[12], current[13], current[14]);
                if(profileGuard!=null && !G9Wire.Equal(profileGuard,device.ReadProfile())) throw new InvalidDataException("Active profile changed during keymap read; operation stopped.");
                if (command == "inspect") { Console.WriteLine("KEYMAP VALIDATED. A physical keypress is a separate check."); return 0; }
                if (command == "mapping") { PrintMapping(current,selectedProfile); return 0; }
                byte[] expected = command == "assign" ? BindingCatalog.WithAssignment(current, assignmentIndex.Value, function.Value, param1, param2,selectedProfile) :
                    command == "zero" ? BindingCatalog.WithAssignment(current, 3, 0x11, 0, 0x27,selectedProfile) : restored;
                SaveBackup(current, device.Device.Product, backupDirectory);
                if (command == "backup") return 0;
                device.WriteKeys(expected,selectedProfile,requestedProfile.HasValue);
                Console.WriteLine("CONFIG VERIFIED. Tool is exiting. Test the sixth button in a text field."); return 0;
            }
        } catch (OledFirmwareUnsupportedException error) { Console.WriteLine("OLED_UNSUPPORTED " + error.Message); return 4; }
        catch (NotSupportedException error) { Console.Error.WriteLine("ERROR: " + error.Message); return 1; }
        catch (Exception error) {
            var nativeError = error as Win32Exception;
            Console.Error.WriteLine("ERROR: " + error.Message + (nativeError == null ? "" :
                " (Win32=" + nativeError.NativeErrorCode + ": " + new Win32Exception(nativeError.NativeErrorCode).Message + ")"));
            return 1;
        }
    }
}
