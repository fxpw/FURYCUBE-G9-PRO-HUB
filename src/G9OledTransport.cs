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
    internal sealed class OledFirmwareUnsupportedException : NotSupportedException {
        internal OledFirmwareUnsupportedException(string message) : base(message) { }
    }
    internal sealed class NativeOledTransport : IOledTransport {
        readonly IOledDeviceSession session; bool discovered;
        internal NativeOledTransport(IOledDeviceSession connection) {
            if (connection == null) throw new ArgumentNullException("connection"); session = connection;
        }
        public ushort VendorId { get { return session.VendorId; } }
        public ushort ProductId { get { return session.ProductId; } }
        void RequireWired() {
            if (VendorId != 0x1D57 || ProductId != 0x2148) {
                discovered = false;
                throw new OledFirmwareUnsupportedException("OLED uploads require the wired G9 1D57:2148; receiver and bootloader traffic is disabled.");
            }
        }
        public byte[] ReadFirmwareVersion() {
            discovered = false; RequireWired();
            byte[] response = session.ReadOledFirmware();
            try { OledProtocol.DecodeFirmwareCapabilities(response); }
            catch (InvalidDataException error) { throw new OledFirmwareUnsupportedException(error.Message); }
            discovered = true; return response;
        }
        public void SendFeature(byte[] report) {
            RequireWired();
            if (!discovered) throw new InvalidOperationException("Native OLED transport has no verified OL2 discovery for this open session");
            OledProtocol.DecodeWritePage(report);
            try { session.SendOledFeature(report); }
            catch { discovered = false; throw; }
        }
    }
}
