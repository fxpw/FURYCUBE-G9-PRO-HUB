using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Furycube.G9.Oled;

internal sealed class RadioOledAcknowledgements {
    readonly object gate = new object();
    ushort expected;
    long generation;
    bool matched;

    internal Func<int,bool> Arm(byte[] pixels) {
        lock (gate) {
            long armed = ++generation;
            expected = OledProtocol.Crc16CcittFalse(pixels, 0, pixels.Length);
            matched = false;
            return milliseconds => WaitForMatch(armed, milliseconds);
        }
    }

    bool WaitForMatch(long armed, int milliseconds) {
        lock (gate) {
            if (milliseconds > 0 && generation == armed && !matched) Monitor.Wait(gate, milliseconds);
            return generation == armed && matched;
        }
    }

    internal void Observe(ushort product, byte[] report) {
        if (product != 0xFA60 || report.Length != 5 || report[0] != 3 || report[1] != 0xC9 || report[2] != 0xD1) return;
        lock (gate) {
            if (generation != 0 && (report[3] | (report[4] << 8)) == expected) {
                matched = true;
                Monitor.PulseAll(gate);
            }
        }
    }
}

internal sealed class ConfirmedRadioOledStream {
    const int ConfirmationMilliseconds = 2500;
    const int SameTickWaitMilliseconds = 120;
    const int DeltaRetryIntervalMilliseconds = 1200;
    const int MaximumDeltaRetries = 2;
    const int RadioFragmentMilliseconds = 500;
    const int FullRetryDelayMilliseconds = 4000;
    readonly RadioOledAcknowledgements acknowledgements;
    readonly bool replaySupported;
    byte[] rendered, pending, pendingDelta;
    Func<int,bool> received;
    long pendingAt, nextFullAt;
    int deltaRetries;

    internal ConfirmedRadioOledStream(RadioOledAcknowledgements acknowledgements, bool replaySupported = false) {
        this.acknowledgements = acknowledgements;
        this.replaySupported = replaySupported;
    }

    internal void Reset() { rendered = pending = pendingDelta = null; received = null; nextFullAt = 0; }

    bool AcceptReceipt(int waitMilliseconds) {
        if (!received(waitMilliseconds)) return false;
        rendered = pending;
        pending = pendingDelta = null;
        return true;
    }

    internal string Send(OledSender sender, byte[] frame, bool partial, Func<bool> idle, Action<byte[]> sendDelta,
        Func<long> now, out int reports, out string transfer, out int confirmations) {
        reports = 0;
        confirmations = 0;
        transfer = "await-ack";
        if (pending != null) {
            if (AcceptReceipt(0)) {
                confirmations++;
                if (rendered.SequenceEqual(frame)) return OledStreamCommand.Sent;
            } else if (replaySupported && pendingDelta != null && deltaRetries < MaximumDeltaRetries &&
                now() - pendingAt >= DeltaRetryIntervalMilliseconds) {
                // An applied delta can be acknowledged again on OLE. Keep its
                // exact payload and the original armed receipt through retries.
                sendDelta(pendingDelta);
                reports = 1;
                transfer = "delta-retry";
                deltaRetries++;
                pendingAt = now();
                if (AcceptReceipt(SameTickWaitMilliseconds)) {
                    confirmations++;
                    if (rendered.SequenceEqual(frame)) return OledStreamCommand.Sent;
                }
                return OledStreamCommand.Deferred;
            } else if (now() - pendingAt < ConfirmationMilliseconds || !idle()) {
                return OledStreamCommand.Deferred;
            } else {
                bool fullExpired = pendingDelta == null;
                Reset();
                if (fullExpired) nextFullAt = now() + FullRetryDelayMilliseconds;
            }
        }

        byte[] target = frame;
        byte[] delta = partial && rendered != null ? OledRadioDelta.EncodeStep(rendered, frame, out target) : null;
        if (delta != null && delta.Length == 0) {
            transfer = "unchanged";
            return OledStreamCommand.Unchanged;
        }
        // Distinct pixel buffers with the same CRC are ambiguous to OLD.
        if (rendered != null && !rendered.SequenceEqual(target) &&
            OledProtocol.Crc16CcittFalse(rendered, 0, rendered.Length) == OledProtocol.Crc16CcittFalse(target, 0, target.Length))
            return OledStreamCommand.Deferred;

        if (delta != null) {
            transfer = "delta";
            received = acknowledgements.Arm(target);
            sendDelta(delta);
            reports = 1;
        } else {
            transfer = "full";
            if (now() < nextFullAt) return OledStreamCommand.Deferred;
            var full = sender.WriteFrame(frame, idle, () => received = acknowledgements.Arm(frame), RadioFragmentMilliseconds);
            reports = full.FeatureReportsSent;
            if (full.Deferred) {
                if (reports > 0) {
                    Reset();
                    nextFullAt = now() + FullRetryDelayMilliseconds;
                }
                return OledStreamCommand.Deferred;
            }
        }
        pending = target;
        pendingDelta = delta;
        pendingAt = now();
        deltaRetries = 0;
        if (AcceptReceipt(SameTickWaitMilliseconds)) {
            confirmations++;
            return target.SequenceEqual(frame) ? OledStreamCommand.Sent : OledStreamCommand.Deferred;
        }
        return OledStreamCommand.Deferred;
    }
}

// This class never creates/reconnects/disposes a native device. The caller owns
// one transport for the complete stream, its lock, cancellation and timeout.
internal static class OledStreaming {
    const int MaximumLineLength = 1024;
    const int MaximumUnconfirmedDeltas = 8;
    const int FullRefreshMilliseconds = 15000;

    // Console.In uses the ambient OEM console code page on .NET Framework.
    // Process.StandardInput may also emit a UTF-8 BOM as soon as it starts.
    // Decode that transport once here; the command parser remains strict.
    internal static StreamReader OpenUtf8Input(Stream input) {
        return new StreamReader(input, new UTF8Encoding(false, true), true, 1024, true);
    }

    internal static int RunUtf8(Stream input, TextWriter output, IOledTransport transport, Action<int> delay = null,
        Func<bool> canSendRadioFragment = null, Action<byte[]> sendRadioDelta = null, TextWriter trace = null,
        Func<FirmwareCapabilities,RadioOledAcknowledgements> startRadioAcknowledgements = null,
        Func<bool> canSendRadioLive = null, bool allowLegacyPartial = true) {
        using (var reader = OpenUtf8Input(input)) return Run(reader, output, transport, delay, canSendRadioFragment, sendRadioDelta,
            trace:trace, startRadioAcknowledgements:startRadioAcknowledgements,
            canSendRadioLive:canSendRadioLive, allowLegacyPartial:allowLegacyPartial);
    }

    internal static int Run(TextReader input, TextWriter output, IOledTransport transport, Action<int> delay = null,
        Func<bool> canSendRadioFragment = null, Action<byte[]> sendRadioDelta = null, Func<long> elapsedMilliseconds = null,
        TextWriter trace = null, Func<FirmwareCapabilities,RadioOledAcknowledgements> startRadioAcknowledgements = null,
        Func<bool> canSendRadioLive = null, bool allowLegacyPartial = true) {
        if (input == null) throw new ArgumentNullException("input");
        if (output == null) throw new ArgumentNullException("output");
        if (transport == null) throw new ArgumentNullException("transport");
        var sender = new OledSender(transport, delay);
        FirmwareCapabilities capabilities = sender.Discover();
        bool radioDeltas = transport.ProductId == 0xFA60 && sendRadioDelta != null && canSendRadioFragment != null;
        bool supportsPartial = radioDeltas && allowLegacyPartial && capabilities.Extension == "OLB";
        var radioAcknowledgements = radioDeltas && capabilities.SupportsRadioReceipts && startRadioAcknowledgements != null
            ? startRadioAcknowledgements(capabilities) : null;
        var confirmed = radioAcknowledgements != null
            ? new ConfirmedRadioOledStream(radioAcknowledgements, capabilities.SupportsRadioDeltaReplay) : null;
        var clock = Stopwatch.StartNew();
        Func<long> now = elapsedMilliseconds ?? (() => clock.ElapsedMilliseconds);
        byte[] lastSubmittedFrame = null;
        OledSender.RadioRefresh refresh = null;
        int unconfirmedDeltas = 0;
        long firstDeltaAt = 0;
        // READY confirms the existing version-report gate, not boot/recovery
        // compatibility or a visible image. No SET is used for discovery.
        Reply(output, "OLED_STREAM_READY");
        long frameIndex = 0, statusIndex = 0;
        for (;;) {
            string line = ReadBoundedLine(input);
            if (line == null || line == "STOP") return 0;
            if (line == "STATUS") {
                if (!sender.Capabilities.SupportsRuntimeStatus) Reply(output, "OLED_STATUS_UNSUPPORTED");
                else Reply(output, "OLED_STATUS_DATA " + Convert.ToBase64String(sender.ReadRuntimeStatusFrame()));
                statusIndex = checked(statusIndex + 1);
                Reply(output, "OLED_STATUS_SENT " + statusIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));
                continue;
            }
            bool partial = line.StartsWith(OledStreamCommand.LiveDelta, StringComparison.Ordinal);
            bool live = partial || line.StartsWith(OledStreamCommand.Live, StringComparison.Ordinal);
            string command = partial ? OledStreamCommand.LiveDelta : live ? OledStreamCommand.Live : OledStreamCommand.Frame;
            byte[] frame = DecodeFrame(line, command);
            if(!partial && refresh!=null) {
                // Explicit full uploads replace any in-progress background frame.
                refresh=null;
                lastSubmittedFrame=null;
            }
            string reply;
            long elapsed = now();
            bool refreshDue = unconfirmedDeltas > 0 &&
                (unconfirmedDeltas >= MaximumUnconfirmedDeltas || elapsed - firstDeltaAt >= FullRefreshMilliseconds);
            byte[] delta = partial && supportsPartial && refresh == null && lastSubmittedFrame != null && !refreshDue
                ? OledRadioDelta.Encode(lastSubmittedFrame, frame) : null;
            bool stagedRefresh = partial && supportsPartial &&
                (refresh!=null || (delta==null && lastSubmittedFrame!=null));
            DateTimeOffset started=trace==null?default(DateTimeOffset):DateTimeOffset.UtcNow;
            int reports=0;
            int confirmations=0;
            string transfer = stagedRefresh ? "refresh" : delta == null ? "full" : delta.Length == 0 ? "unchanged" : "delta";
            // Keep the confirmed base and any pending receipt across a drag.
            // Radio feature writes can release a physically held button.
            if (live && transport.ProductId == 0xFA60 && canSendRadioLive != null && !canSendRadioLive()) {
                transfer = "button-held";
                reply = OledStreamCommand.Deferred;
            } else if (confirmed != null && live) {
                reply = confirmed.Send(sender, frame, partial, canSendRadioFragment, sendRadioDelta, now, out reports, out transfer, out confirmations);
            } else if(stagedRefresh) {
                reply=OledStreamCommand.Deferred;
                if(refresh==null && canSendRadioFragment())refresh=sender.PrepareRadioRefresh(frame);
                if(refresh!=null && sender.WriteRadioRefreshFragment(refresh,canSendRadioFragment)) {
                    reports=1;
                    if(refresh.Complete) {
                        lastSubmittedFrame=refresh.Pixels;
                        unconfirmedDeltas=0;
                        if(lastSubmittedFrame.SequenceEqual(frame))reply=OledStreamCommand.Sent;
                        refresh=null;
                    }
                } else if(refresh!=null && refresh.Next==0)refresh=null;
            } else if (delta != null && delta.Length == 0) {
                reply = OledStreamCommand.Unchanged;
            } else if (delta != null) {
                sendRadioDelta(delta);reports=1;
                if (unconfirmedDeltas++ == 0) firstDeltaAt = elapsed;
                lastSubmittedFrame = frame;
                reply = OledStreamCommand.Sent;
            } else {
                if (confirmed != null) confirmed.Reset();
                OledTransferResult result = sender.WriteFrame(frame, live ? canSendRadioFragment : null);
                reports=result.FeatureReportsSent;
                reply = result.Deferred ? OledStreamCommand.Deferred : OledStreamCommand.Sent;
                if (supportsPartial) {
                    if (!result.Deferred) {
                        lastSubmittedFrame = frame;
                        unconfirmedDeltas = 0;
                    } else if (result.FeatureReportsSent > 0) {
                        // Firmware still owns the partial full-frame upload.
                        lastSubmittedFrame = null;
                    }
                }
            }
            frameIndex = checked(frameIndex + 1);
            // Legacy replies count host submission. OLD experimental live replies
            // can also carry a receipt for writes completed by the mouse firmware.
            DateTimeOffset ended=trace==null?default(DateTimeOffset):DateTimeOffset.UtcNow;
            Reply(output, reply + frameIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if(trace!=null)Reply(trace,String.Format(System.Globalization.CultureInfo.InvariantCulture,
                "frame={0} type={1} reports={2} result={3} start={4:o} end={5:o}{6}",
                frameIndex,transfer,reports,reply.TrimEnd(),
                started,ended,confirmed == null ? "" : " confirmed="+confirmations));
        }
        // No catch/retry: malformed input, transport or output failure terminates
        // this call immediately. Later input lines are not read or transmitted.
    }

    static void Reply(TextWriter output, string line) { output.WriteLine(line); output.Flush(); }

    static byte[] DecodeFrame(string line, string prefix) {
        if (!line.StartsWith(prefix, StringComparison.Ordinal)) throw new InvalidDataException("Expected FRAME, LIVE or LIVE_DELTA <base64-480bytes>, STATUS or STOP");
        string encoded = line.Substring(prefix.Length);
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
