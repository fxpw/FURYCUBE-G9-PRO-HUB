using System;
using System.IO;
using System.Text;
using Furycube.G9.Oled;

// This class never creates/reconnects/disposes a native device. The caller owns
// one transport for the complete stream, its lock, cancellation and timeout.
internal static class OledStreaming {
    const int MaximumLineLength = 1024;
    const string Prefix = "FRAME ";

    // Console.In uses the ambient OEM console code page on .NET Framework.
    // Process.StandardInput may also emit a UTF-8 BOM as soon as it starts.
    // Decode that transport once here; the command parser remains strict.
    internal static StreamReader OpenUtf8Input(Stream input) {
        return new StreamReader(input, new UTF8Encoding(false, true), true, 1024, true);
    }

    internal static int RunUtf8(Stream input, TextWriter output, IOledTransport transport) {
        return RunUtf8(input, output, transport, null);
    }

    internal static int RunUtf8(Stream input, TextWriter output, IOledTransport transport, Action<int> delay) {
        using (var reader = OpenUtf8Input(input)) return Run(reader, output, transport, delay);
    }

    internal static int Run(TextReader input, TextWriter output, IOledTransport transport) {
        return Run(input, output, transport, null);
    }

    internal static int Run(TextReader input, TextWriter output, IOledTransport transport, Action<int> delay) {
        if (input == null) throw new ArgumentNullException("input");
        if (output == null) throw new ArgumentNullException("output");
        if (transport == null) throw new ArgumentNullException("transport");
        var sender = new OledSender(transport, delay);
        sender.Discover();
        // READY confirms the existing version-report gate, not boot/recovery
        // compatibility or a visible image. No SET is used for discovery.
        Reply(output, "OLED_STREAM_READY");
        long frameIndex = 0;
        for (;;) {
            string line = ReadBoundedLine(input);
            if (line == null || line == "STOP") return 0;
            byte[] frame = DecodeFrame(line);
            // Build/validate all four pages before the first native transfer.
            // Sender keeps this same transport, caches discovery, and initializes
            // only the first frame. Its default pacing is 25ms between pages.
            sender.WriteFrame(frame);
            frameIndex = checked(frameIndex + 1);
            // This is a host submission ACK. It is not firmware acknowledgement
            // or proof that the user saw this frame on the physical display.
            Reply(output, "OLED_FRAME_SENT " + frameIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        // No catch/retry: malformed input, transport or output failure terminates
        // this call immediately. Later input lines are not read or transmitted.
    }

    static void Reply(TextWriter output, string line) { output.WriteLine(line); output.Flush(); }

    static byte[] DecodeFrame(string line) {
        if (!line.StartsWith(Prefix, StringComparison.Ordinal)) throw new InvalidDataException("Expected FRAME <base64-480bytes> or STOP");
        string encoded = line.Substring(Prefix.Length);
        // 480 is divisible by three, so its canonical Base64 length is exactly
        // 640 with no '=' padding. Reject whitespace/other data before decoding.
        if (encoded.Length != 640) throw new InvalidDataException("OLED frame Base64 must encode exactly 480 bytes");
        foreach (char value in encoded) {
            bool valid = (value >= 'A' && value <= 'Z') || (value >= 'a' && value <= 'z') ||
                         (value >= '0' && value <= '9') || value == '+' || value == '/';
            if (!valid) throw new InvalidDataException("Invalid OLED frame Base64 character");
        }
        byte[] pixels;
        try { pixels = Convert.FromBase64String(encoded); }
        catch (FormatException error) { throw new InvalidDataException("Invalid OLED frame Base64", error); }
        OledProtocol.ValidateFrame(pixels); // Includes the invisible last-page bits.
        return pixels;
    }

    static string ReadBoundedLine(TextReader input) {
        var line = new StringBuilder(646);
        for (;;) {
            int next = input.Read();
            if (next == -1) return line.Length == 0 ? null : line.ToString();
            if (next == '\n') return line.ToString();
            if (next == '\r') {
                if (input.Peek() == '\n') input.Read();
                return line.ToString();
            }
            if (line.Length == MaximumLineLength) throw new InvalidDataException("OLED stream line exceeds 1024 characters");
            line.Append((char)next);
        }
    }
}
