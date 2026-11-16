using System;
using System.IO;

namespace Furycube.G9.Oled {
    // Implemented by one open native device session; there is no reopen/fallback between discovery and writes.
    internal interface IOledDeviceSession {
        ushort VendorId { get; }
        ushort ProductId { get; }
        byte[] ReadOledFirmware();
        void SendOledFeature(byte[] report);
    }
    internal interface IOledRuntimeStatusDeviceSession { byte[] ReadOledRuntimeStatus(); }
    internal sealed class OledFirmwareUnsupportedException : NotSupportedException {
        internal OledFirmwareUnsupportedException(string message) : base(message) { }
    }
    internal sealed class NativeOledTransport : IOledTransport, IOledRuntimeStatusTransport {
        readonly IOledDeviceSession session; bool discovered; FirmwareCapabilities capabilities; ushort discoveredProduct;
        internal NativeOledTransport(IOledDeviceSession connection) {
            if (connection == null) throw new ArgumentNullException("connection"); session = connection;
        }
        public ushort VendorId { get { return session.VendorId; } }
        public ushort ProductId { get { return session.ProductId; } }
        void RequireDevice() {
            if (VendorId != 0x1D57 || (ProductId != 0x2148 && ProductId != 0xFA60) || (discovered && ProductId != discoveredProduct)) {
                discovered = false; capabilities = null;
                throw new OledFirmwareUnsupportedException("OLED device identity changed or is unsupported; reopen and discover the normal G9 interface.");
            }
        }
        public byte[] ReadFirmwareVersion() {
            discovered = false; capabilities = null; RequireDevice(); ushort target = ProductId;
            byte[] response = session.ReadOledFirmware();
            try {
                var current = OledProtocol.DecodeFirmwareCapabilities(response);
                RequireDevice(); if (target != ProductId) throw new InvalidDataException("Device changed during OLED discovery");
                OledProtocol.ValidateDeviceFirmware(current, VendorId, target);
                capabilities = current;
            } catch (InvalidDataException error) { throw new OledFirmwareUnsupportedException(error.Message); }
            catch (NotSupportedException error) { throw new OledFirmwareUnsupportedException(error.Message); }
            discoveredProduct = target; discovered = true; return response;
        }
        public byte[] ReadRuntimeStatus() {
            RequireDevice();
            if (!discovered || capabilities == null) throw new InvalidOperationException("Native OLED transport has no verified discovery for this open session");
            if (!capabilities.SupportsRuntimeStatus) throw new OledFirmwareUnsupportedException("Runtime status requires OL3/OL4; OL2 never receives GET 0x22");
            var statusSession = session as IOledRuntimeStatusDeviceSession;
            if (statusSession == null) throw new OledFirmwareUnsupportedException("This native session does not implement guarded OL3 status");
            try {
                byte[] response = statusSession.ReadOledRuntimeStatus();
                if (response == null || response.Length != (capabilities.SupportsShortWrites ? 64 : 132))
                    throw new InvalidDataException("OLED status length does not match this session's advertised firmware");
                OledRuntimeStatusCodec.Decode(response, DateTimeOffset.UtcNow);
                return (byte[])response.Clone();
            } catch { discovered = false; capabilities = null; throw; }
        }
        public void SendFeature(byte[] report) {
            RequireDevice();
            if (!discovered) throw new InvalidOperationException("Native OLED transport has no verified discovery for this open session");
            if (capabilities == null || !capabilities.SupportsShortWrites)
                throw new OledFirmwareUnsupportedException("OLED images require OL4: OL2/OL3 long USB writes are disabled after a confirmed packet-assembly fault.");
            OledShortProtocol.ValidateWriteChunk(report);
            try { session.SendOledFeature(report); }
            catch { discovered = false; throw; }
        }
    }
}
