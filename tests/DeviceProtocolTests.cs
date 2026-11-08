using System;
using System.IO;

internal static class DeviceProtocolTests {
    static int count;
    static byte[] Hex(string value) { byte[] bytes = new byte[value.Length / 2]; for (int i = 0; i < bytes.Length; i++) bytes[i] = Convert.ToByte(value.Substring(i * 2, 2), 16); return bytes; }
    static byte[] Change(byte[] source, int at, byte value) { byte[] data = (byte[])source.Clone(); data[at] = value; return data; }
    static void Pass(bool value, string name) { if (!value) throw new Exception("FAIL " + name); count++; }
    static void Reject(Action action, string name) { try { action(); } catch (InvalidDataException) { count++; return; } throw new Exception("FAIL accepted " + name); }
    static int Main() { try {
        // Captured wired read after a real USB reconnect; independent of the C# implementation.
        byte[] zero = Hex("083b010200000300000400001100270d00003c00000500000600000100000100000100000100000100000100000100000100000a000009000000b0");
        byte[] original = Hex("083b0102000003000004000012000d0d00003c00000500000600000100000100000100000100000100000100000100000100000a00000900000097");
        Pass(G9Wire.Equal(G9Wire.DecodeKeys(zero), zero), "captured zero keymap");
        Pass(G9Wire.Equal(G9Wire.WithZero(original), zero), "original macro assignment becomes captured single zero");
        byte[] padded = new byte[64]; Buffer.BlockCopy(zero, 0, padded, 0, zero.Length);
        Pass(G9Wire.Equal(G9Wire.DecodeKeys(padded), zero), "receiver 64-byte transfer");
        Reject(delegate { G9Wire.DecodeKeys(new byte[58]); }, "truncated keymap");
        Reject(delegate { G9Wire.DecodeKeys(new byte[65]); }, "unexpected transfer length");
        Reject(delegate { G9Wire.DecodeKeys(Change(zero, 0, 9)); }, "stale macro header");
        Reject(delegate { G9Wire.DecodeKeys(Change(zero, 0, 11)); }, "stale version header");
        Reject(delegate { G9Wire.DecodeKeys(Change(zero, 1, 58)); }, "wrong logical length");
        Reject(delegate { G9Wire.DecodeKeys(Change(zero, 2, 2)); }, "wrong profile with otherwise valid checksum");
        Reject(delegate { G9Wire.DecodeKeys(Change(zero, 14, 0x26)); }, "corrupt payload");
        Reject(delegate { G9Wire.DecodeKeys(Change(zero, 58, 0xB1)); }, "corrupt checksum");
        byte[] idle = Hex("a000000000000000"), ready = Hex("a001000000000000");
        G9Wire.ValidateAck(idle, false); count++; G9Wire.ValidateAck(ready, true); count++;
        Reject(delegate { G9Wire.ValidateAck(ready, false); }, "pending read before a new transaction");
        Reject(delegate { G9Wire.ValidateAck(idle, true); }, "read not ready");
        Reject(delegate { G9Wire.ValidateAck(Change(ready, 7, 1), true); }, "unexpected ACK tail");
        byte[] active = Hex("0c080101fe01fe00");
        Pass(G9Wire.Equal(G9Wire.DecodeProfile(active), new byte[] { 1, 1 }), "captured active profile");
        Reject(delegate { G9Wire.DecodeProfile(Change(active, 2, 2)); }, "active-profile selector");
        Reject(delegate { G9Wire.DecodeProfile(Change(active, 4, 0xFF)); }, "profile complement");
        Reject(delegate { G9Wire.DecodeProfile(Change(active, 0, 8)); }, "profile report ID");
        Pass(G9Wire.Equal(G9Wire.ReadKeysRequest(), Hex("a0083b0001000000")), "captured read-keymap request");
        Pass(G9Wire.Equal(G9Wire.ReadProfileRequest(), Hex("a00c080001000000")), "captured read-profile request");
        G9Wire.ValidateSend(zero); count++; G9Wire.ValidateSend(G9Wire.SelectProfileOne()); count++;
        Reject(delegate { G9Wire.ValidateSend(Hex("1008000000000000")); }, "firmware-update entry command");
        Reject(delegate { G9Wire.ValidateSend(Change(zero, 58, 0)); }, "bad keymap write");
        Reject(delegate { G9Wire.ValidateSend(Hex("a0096f000d000000")); }, "macro query outside permitted operations");
        Console.WriteLine("PASS " + count + " captured-protocol and command-boundary fixtures; no HID APIs called."); return 0;
    } catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; } }
}
