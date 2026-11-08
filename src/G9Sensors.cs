using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace Furycube.G9.Sensors {
    public sealed class SensorSnapshot {
        public DateTimeOffset SampledAt { get; internal set; }
        public double? CpuUsagePercent { get; internal set; }
        public double? CpuTemperatureCelsius { get; internal set; }
        public double? GpuUsagePercent { get; internal set; }
        public double? GpuTemperatureCelsius { get; internal set; }
        public double? RamUsagePercent { get; internal set; }
        public double? RamUsedGiB { get; internal set; }
        public double? RamTotalGiB { get; internal set; }
        public double? RamTemperatureCelsius { get { return null; } }
        public string CpuTemperatureSourceName { get; internal set; }
        public string CpuTemperatureSourceId { get; internal set; }
        public string CpuTemperatureProvider { get; internal set; }
        public string GpuSourceName { get; internal set; }
    }

    internal static class SensorMath {
        internal static double? CpuPercent(ulong oldIdle, ulong oldKernel, ulong oldUser, ulong idle, ulong kernel, ulong user) {
            if (idle < oldIdle || kernel < oldKernel || user < oldUser) return null;
            double total = (kernel - oldKernel) + (double)(user - oldUser), inactive = idle - oldIdle;
            if (total <= 0 || inactive > total) return null;
            return Percent(100.0 * (total - inactive) / total);
        }
        internal static double? RamPercent(ulong total, ulong available) {
            if (total == 0 || available > total) return null;
            return Percent(100.0 * (total - available) / total);
        }
        internal static double? Percent(double value) { return Finite(value) && value >= 0 && value <= 100 ? (double?)value : null; }
        internal static double? Temperature(double value) { return Finite(value) && value >= -20 && value <= 150 ? (double?)value : null; }
        internal static bool Finite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }
        internal static long UnixMilliseconds(DateTimeOffset time) { return (time.UtcTicks - 621355968000000000L) / TimeSpan.TicksPerMillisecond; }
        internal static bool Fresh(double sampledAtUnixms, DateTimeOffset now) {
            if (!Finite(sampledAtUnixms) || sampledAtUnixms < 0) return false;
            double age = UnixMilliseconds(now) - sampledAtUnixms;
            return age >= -5000 && age <= 20000;
        }
        internal sealed class CpuReading { internal double Temperature; internal string Name, SourceId, Provider; }
        static string Text(IDictionary<string, object> data, string key) {
            object value; string text = data.TryGetValue(key, out value) ? value as string : null;
            return text != null && text.Length <= 512 ? text : null;
        }
        internal static bool Number(IDictionary<string, object> data, string key, out double number) {
            number = 0; object value;
            if (!data.TryGetValue(key, out value) || value == null || value is string || value is bool) return false;
            try { number = Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture); return Finite(number); }
            catch (Exception) { return false; }
        }
        internal static CpuReading ParseCpuMetrics(string json, DateTimeOffset now) {
            try {
                var parser = new JavaScriptSerializer { MaxJsonLength = 65536, RecursionLimit = 32 };
                var root = parser.DeserializeObject(json) as IDictionary<string, object>; object raw;
                if (root == null || !root.TryGetValue("cpu", out raw)) return null;
                var cpu = raw as IDictionary<string, object>; if (cpu == null) return null;
                double temperature, timestamp;
                if (!Number(cpu, "temperature", out temperature) || !Temperature(temperature).HasValue) return null;
                if (!Number(cpu, "sampledAt", out timestamp) && !Number(cpu, "sampledAtUnixms", out timestamp) &&
                    !Number(root, "sampledAt", out timestamp) && !Number(root, "sampledAtUnixms", out timestamp)) return null;
                if (!Fresh(timestamp, now)) return null;
                // Preserve the actual sensor label; do not rename Tctl/Tdie as CPU Die average.
                return new CpuReading { Temperature = temperature, Name = Text(cpu, "name"), SourceId = Text(cpu, "source"), Provider = Text(cpu, "provider") };
            } catch (Exception) { return null; }
        }
    }

    internal sealed class ExistingCpuBridge {
        readonly string configuration;
        internal ExistingCpuBridge() {
            configuration = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "fxpw-seelen", "sensors", "bridge.json");
        }
        internal SensorMath.CpuReading Read(DateTimeOffset now) {
            try {
                var file = new FileInfo(configuration); if (!file.Exists || file.Length <= 0 || file.Length > 65536) return null;
                var parser = new JavaScriptSerializer { MaxJsonLength = 65536, RecursionLimit = 16 };
                var config = parser.DeserializeObject(File.ReadAllText(configuration, Encoding.UTF8)) as IDictionary<string, object>;
                double portValue; object credential;
                if (config == null || !SensorMath.Number(config, "port", out portValue) || portValue != Math.Floor(portValue) || portValue < 1 || portValue > 65535 || !config.TryGetValue("token", out credential)) return null;
                string token = credential as string;
                if (String.IsNullOrEmpty(token) || token.Length > 4096 || token.IndexOf('\r') >= 0 || token.IndexOf('\n') >= 0) return null;
                var request = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + ((int)portValue).ToString(System.Globalization.CultureInfo.InvariantCulture) + "/metrics");
                request.Method = "GET"; request.Proxy = null; request.AllowAutoRedirect = false; request.Timeout = 300; request.ReadWriteTimeout = 300; request.KeepAlive = false;
                request.Headers[HttpRequestHeader.Authorization] = "Bearer " + token;
                var elapsed = Stopwatch.StartNew();
                using (var deadline = new Timer(delegate { try { request.Abort(); } catch (Exception) { } }, null, 300, Timeout.Infinite))
                using (var response = (HttpWebResponse)request.GetResponse()) {
                    if (response.StatusCode != HttpStatusCode.OK || response.ContentLength > 65536) return null;
                    using (var stream = response.GetResponseStream())
                    using (var memory = new MemoryStream()) {
                        byte[] buffer = new byte[4096]; int count;
                        while ((count = stream.Read(buffer, 0, buffer.Length)) > 0) {
                            if (elapsed.ElapsedMilliseconds >= 300 || memory.Length + count > 65536) return null;
                            memory.Write(buffer, 0, count);
                        }
                        if (elapsed.ElapsedMilliseconds >= 300) return null;
                        return SensorMath.ParseCpuMetrics(Encoding.UTF8.GetString(memory.ToArray()), now);
                    }
                }
            } catch (Exception) { return null; } // Missing/stale sources are unavailable, never fabricated temperatures.
        }
    }

    internal sealed class NvidiaSensors : IDisposable {
        [StructLayout(LayoutKind.Sequential)] struct Utilization { internal uint Gpu, Memory; }
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int Initialize();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int Shutdown();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int DeviceByIndex(uint index, out IntPtr device);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int Temperature(IntPtr device, uint sensor, out uint temperature);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int Usage(IntPtr device, out Utilization rates);
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);
        [DllImport("kernel32.dll", CharSet=CharSet.Ansi)] static extern IntPtr GetProcAddress(IntPtr module, string name);
        [DllImport("kernel32.dll")] static extern bool FreeLibrary(IntPtr module);
        IntPtr module, device; Shutdown shutdown; Temperature temperature; Usage usage; bool initialized;
        Delegate Export(string name, Type type) {
            IntPtr address = GetProcAddress(module, name); if (address == IntPtr.Zero) throw new EntryPointNotFoundException(name);
            return Marshal.GetDelegateForFunctionPointer(address, type);
        }
        internal NvidiaSensors() {
            try {
                string library = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvml.dll");
                if (!File.Exists(library)) return;
                // Absolute System32 path and restricted dependency search, no PATH or app-local DLL loading.
                module = LoadLibraryEx(library, IntPtr.Zero, 0x900); if (module == IntPtr.Zero) return;
                var init = (Initialize)Export("nvmlInit_v2", typeof(Initialize));
                shutdown = (Shutdown)Export("nvmlShutdown", typeof(Shutdown));
                var byIndex = (DeviceByIndex)Export("nvmlDeviceGetHandleByIndex_v2", typeof(DeviceByIndex));
                temperature = (Temperature)Export("nvmlDeviceGetTemperature", typeof(Temperature));
                usage = (Usage)Export("nvmlDeviceGetUtilizationRates", typeof(Usage));
                if (init() != 0) { Dispose(); return; } initialized = true;
                if (byIndex(0, out device) != 0 || device == IntPtr.Zero) Dispose();
            } catch (Exception) { Dispose(); }
        }
        internal void Sample(out double? percent, out double? degrees) {
            percent = degrees = null; if (!initialized || device == IntPtr.Zero) return;
            uint value; if (temperature(device, 0, out value) == 0) degrees = SensorMath.Temperature(value);
            Utilization rates; if (usage(device, out rates) == 0) percent = SensorMath.Percent(rates.Gpu);
        }
        public void Dispose() {
            if (initialized && shutdown != null) { initialized = false; try { shutdown(); } catch (Exception) { } }
            device = IntPtr.Zero; if (module != IntPtr.Zero) { FreeLibrary(module); module = IntPtr.Zero; }
        }
    }

    public sealed class SensorProvider : IDisposable {
        [StructLayout(LayoutKind.Sequential)] struct FileTime { internal uint Low, High; internal ulong Value { get { return ((ulong)High << 32) | Low; } } }
        [StructLayout(LayoutKind.Sequential)] struct MemoryStatus {
            internal uint Size, Load; internal ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
        }
        [DllImport("kernel32.dll", SetLastError=true)] static extern bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);
        [DllImport("kernel32.dll", SetLastError=true)] static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
        readonly object gate = new object(); readonly ExistingCpuBridge bridge = new ExistingCpuBridge();
        NvidiaSensors nvidia; bool disposed, hasCpuBaseline; ulong oldIdle, oldKernel, oldUser;
        public SensorSnapshot Sample() {
            lock (gate) {
                if (disposed) throw new ObjectDisposedException("SensorProvider");
                var snapshot = new SensorSnapshot { SampledAt = DateTimeOffset.UtcNow };
                FileTime idle, kernel, user;
                if (GetSystemTimes(out idle, out kernel, out user)) {
                    if (hasCpuBaseline) snapshot.CpuUsagePercent = SensorMath.CpuPercent(oldIdle, oldKernel, oldUser, idle.Value, kernel.Value, user.Value);
                    oldIdle = idle.Value; oldKernel = kernel.Value; oldUser = user.Value; hasCpuBaseline = true;
                } else hasCpuBaseline = false;
                var memory = new MemoryStatus { Size = (uint)Marshal.SizeOf(typeof(MemoryStatus)) };
                if (GlobalMemoryStatusEx(ref memory)) {
                    snapshot.RamUsagePercent = SensorMath.RamPercent(memory.TotalPhysical, memory.AvailablePhysical);
                    if (snapshot.RamUsagePercent.HasValue) { snapshot.RamUsedGiB = (memory.TotalPhysical - memory.AvailablePhysical) / 1073741824.0; snapshot.RamTotalGiB = memory.TotalPhysical / 1073741824.0; }
                }
                if (nvidia == null) nvidia = new NvidiaSensors();
                double? gpu, gpuTemperature; nvidia.Sample(out gpu, out gpuTemperature);
                snapshot.GpuUsagePercent = gpu; snapshot.GpuTemperatureCelsius = gpuTemperature;
                if (gpu.HasValue || gpuTemperature.HasValue) snapshot.GpuSourceName = "NVIDIA NVML";
                SensorMath.CpuReading cpu = bridge.Read(snapshot.SampledAt);
                if (cpu != null) { snapshot.CpuTemperatureCelsius = cpu.Temperature; snapshot.CpuTemperatureSourceName = cpu.Name; snapshot.CpuTemperatureSourceId = cpu.SourceId; snapshot.CpuTemperatureProvider = cpu.Provider; }
                return snapshot;
            }
        }
        public void Dispose() { lock (gate) { if (disposed) return; disposed = true; if (nvidia != null) nvidia.Dispose(); } }
    }

    internal static class SensorSampleProgram {
        static int Main(string[] args) {
            if (args.Length != 1 || args[0] != "--sample") { Console.Error.WriteLine("G9Sensors --sample"); return 2; }
            using (var provider = new SensorProvider()) {
                Print(provider.Sample()); Thread.Sleep(1000); Print(provider.Sample()); return 0;
            }
        }
        static void Print(SensorSnapshot value) {
            Console.WriteLine(new JavaScriptSerializer().Serialize(new {
                sampledAt = value.SampledAt.ToString("o"), cpuUsagePercent = value.CpuUsagePercent, cpuTemperatureCelsius = value.CpuTemperatureCelsius,
                cpuTemperatureSourceName = value.CpuTemperatureSourceName, cpuTemperatureSourceId = value.CpuTemperatureSourceId, cpuTemperatureProvider = value.CpuTemperatureProvider,
                gpuUsagePercent = value.GpuUsagePercent, gpuTemperatureCelsius = value.GpuTemperatureCelsius, gpuSourceName = value.GpuSourceName,
                ramUsagePercent = value.RamUsagePercent, ramUsedGiB = value.RamUsedGiB, ramTotalGiB = value.RamTotalGiB, ramTemperatureCelsius = value.RamTemperatureCelsius
            }));
        }
    }
}
