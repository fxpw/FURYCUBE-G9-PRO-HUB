"""Summarize an archived Raw Input trace around radio OLED submissions."""

import argparse
import csv
from datetime import datetime, timezone
import json
from pathlib import Path
import re
import sys


TICKS_PER_MS = 10_000
TICKS_PER_SECOND = 1000 * TICKS_PER_MS
SUBMISSION = re.compile(
    r"^DELTA_HOST_SUBMITTED reports=1 start=(\S+) end=(\S+) "
    r"elapsedMs=([0-9]+(?:\.[0-9]+)?) display=unverified$",
    re.MULTILINE,
)
UTC_STAMP = re.compile(r"(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2})\.(\d{7})(?:\+00:00|Z)")
STREAM_FRAME = re.compile(
    r"frame=(\d+) type=(full|refresh|delta|delta-retry|unchanged|await-ack|button-held) reports=(\d+) "
    r"result=OLED_FRAME_(SENT|DEFERRED|UNCHANGED) start=(\S+) end=(\S+)(?: confirmed=([0-2]))?"
)


def parse_utc_ticks(value):
    match = UTC_STAMP.fullmatch(value)
    if match is None:
        raise ValueError("Expected a UTC timestamp with seven fractional digits.")
    whole = datetime.fromisoformat(match[1]).replace(tzinfo=timezone.utc)
    epoch = whole - datetime.min.replace(tzinfo=timezone.utc)
    return (epoch.days * 86_400 + epoch.seconds) * TICKS_PER_SECOND + int(match[2])


def parse_submission(log):
    matches = SUBMISSION.findall(log)
    if len(matches) != 1:
        raise ValueError("Expected exactly one submitted OLED delta marker.")
    start, end, host_ms = matches[0]
    start, end = parse_utc_ticks(start), parse_utc_ticks(end)
    if end < start:
        raise ValueError("Submission timestamps are out of order.")
    return start, end, float(host_ms)


def read_input(lines):
    previous = 0
    for number, row in enumerate(csv.reader(lines), 1):
        if len(row) != 3 or any(not value.isdecimal() for value in row):
            raise ValueError(f"Malformed Raw Input row {number}.")
        timestamp, motion, buttons = map(int, row)
        if timestamp < previous or motion not in (0, 1) or not 0 <= buttons <= 15:
            raise ValueError(f"Invalid Raw Input row {number}.")
        previous = timestamp
        yield timestamp, motion, buttons


def ms(ticks):
    return ticks / TICKS_PER_MS


def max_gap(samples):
    return max((ms(next_ - prev) for prev, next_ in zip(samples, samples[1:])), default=None)


def analyze(events, start, end, host_ms):
    window = [event for event in events if start - TICKS_PER_SECOND <= event[0] <= end + 2 * TICKS_PER_SECOND]
    motion = [timestamp for timestamp, moved, _ in window if moved]
    before = [timestamp for timestamp in motion if timestamp < start]
    during = [timestamp for timestamp in motion if start <= timestamp <= end]
    after = [timestamp for timestamp in motion if timestamp > end]
    overlaps = [ms(right - left) for left, right in zip(motion, motion[1:]) if left <= end and right >= start]
    bounded = bool(before and after)
    return {
        "hostMs": host_ms,
        "wallSpanMs": ms(end - start),
        "motionReportsBefore1s": len(before),
        "motionReportsDuringCall": len(during),
        "motionReportsAfter2s": len(after),
        "buttonReportsDuringCall": sum(bool(buttons) for timestamp, _, buttons in window if start <= timestamp <= end),
        "previousToCallMs": ms(start - before[-1]) if before else None,
        "callEndToNextMs": ms(after[0] - end) if after else None,
        "bracketingGapMs": ms(after[0] - before[-1]) if bounded else None,
        "maxObservedAdjacentGapMs": max(overlaps, default=None),
        "boundedByMotion": bounded,
        "baselineMotionSpanMs": ms(before[-1] - before[0]) if before else None,
        "maxBaselineGapMs": max_gap(before),
        "followupMotionSpanMs": ms(after[-1] - after[0]) if after else None,
        "maxFollowupGapMs": max_gap(after),
        "visible": None,
    }


def analyze_stream(events, log):
    frames = log.splitlines()
    if not frames:
        raise ValueError("OLED stream diagnostic log is empty.")
    frames_sent = {"full": 0, "delta": 0}
    deferred = unchanged = refresh_fragments = awaiting_ack = confirmed_later = confirmations = 0
    delta_retries = held_button_deferrals = 0
    deltas = []
    for index, line in enumerate(frames, 1):
        match = STREAM_FRAME.fullmatch(line)
        if match is None or int(match[1]) != index:
            raise ValueError(f"Invalid OLED stream frame line {index}.")
        kind, reports, result = match[2], int(match[3]), match[4]
        confirmations += int(match[7] or 0)
        start, end = parse_utc_ticks(match[5]), parse_utc_ticks(match[6])
        if end < start:
            raise ValueError(f"Invalid OLED stream time at frame {index}.")
        if kind == "button-held" and reports == 0 and result == "DEFERRED":
            held_button_deferrals += 1
        elif kind == "await-ack" and reports == 0 and result in ("DEFERRED", "SENT"):
            if result == "DEFERRED":
                awaiting_ack += 1
            else:
                confirmed_later += 1
        elif result == "DEFERRED" and kind == "full" and reports < 12:
            deferred += 1
        elif kind == "refresh" and (result, reports) in (("DEFERRED", 0), ("DEFERRED", 1), ("SENT", 1)):
            refresh_fragments += reports
        elif result == "UNCHANGED" and kind == "unchanged" and reports == 0:
            unchanged += 1
        elif kind == "delta-retry" and reports == 1 and result in ("SENT", "DEFERRED"):
            delta_retries += 1
            deltas.append({"frame": index, "retry": True, **analyze(events, start, end, None)})
        elif result in ("SENT", "DEFERRED") and (kind, reports) in (("full", 12), ("delta", 1)):
            frames_sent[kind] += 1
            if kind == "delta":
                deltas.append({"frame": index, **analyze(events, start, end, None)})
        else:
            raise ValueError(f"Inconsistent OLED stream result at frame {index}.")
    return {
        "fullFramesSubmitted": frames_sent["full"],
        "deltaFramesSubmitted": frames_sent["delta"],
        "fullFramesDeferred": deferred,
        "refreshFragmentsSubmitted": refresh_fragments,
        "unchangedFrames": unchanged,
        "awaitingConfirmation": awaiting_ack,
        "confirmedOnLaterTick": confirmed_later,
        "deviceConfirmations": confirmations,
        "deltaRetriesSubmitted": delta_retries,
        "heldButtonDeferrals": held_button_deferrals,
        "deltaTiming": deltas,
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("input_csv", type=Path)
    parser.add_argument("submission_log", type=Path)
    parser.add_argument("--stream", action="store_true", help="Use the HUB worker's per-frame diagnostic log")
    args = parser.parse_args()
    try:
        raw = args.submission_log.read_bytes()
        encoding = "utf-16" if raw.startswith((b"\xff\xfe", b"\xfe\xff")) else "utf-8-sig"
        log = raw.decode(encoding).replace("\r\n", "\n")
        with args.input_csv.open(encoding="utf-8-sig", newline="") as stream:
            events = read_input(stream)
            result = analyze_stream(list(events), log) if args.stream else analyze(events, *parse_submission(log))
    except (ValueError, OSError) as error:
        parser.exit(1, f"{error}\n")
    json.dump(result, sys.stdout, indent=2)
    print()


if __name__ == "__main__":
    main()
