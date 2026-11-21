import unittest

from tools.analyze_radio_oled_motion import analyze, analyze_stream, parse_submission, read_input


class RadioOledMotionTests(unittest.TestCase):
    def test_max_gap_uses_motion_during_call(self):
        result = analyze(read_input(["990000,1,0", "1010000,1,0", "1020000,0,1",
                                     "1030000,1,0", "1060000,1,0"]), 1_000_000, 1_050_000, 5.0)
        self.assertEqual(result["bracketingGapMs"], 7.0)
        self.assertEqual(result["maxObservedAdjacentGapMs"], 3.0)
        self.assertEqual(result["motionReportsDuringCall"], 2)
        self.assertEqual(result["buttonReportsDuringCall"], 1)
        self.assertTrue(result["boundedByMotion"])
        self.assertIsNone(result["visible"])

    def test_incomplete_capture_stays_unbounded(self):
        result = analyze(read_input(["900000,1,0", "1030000,0,2"]), 1_000_000, 1_050_000, 5.0)
        self.assertIsNone(result["bracketingGapMs"])
        self.assertIsNone(result["maxObservedAdjacentGapMs"])
        self.assertFalse(result["boundedByMotion"])

    def test_keeps_seventh_fractional_digit_and_rejects_duplicate_marker(self):
        line = ("DELTA_HOST_SUBMITTED reports=1 start=2026-11-21T11:47:30.5023260+00:00 "
                "end=2026-11-21T11:47:30.5052074+00:00 elapsedMs=2.915 display=unverified")
        start, end, host = parse_submission(line)
        self.assertEqual(end - start, 28_814)
        self.assertEqual(host, 2.915)
        with self.assertRaises(ValueError):
            parse_submission(line + "\n" + line)

    def test_rejects_a_trace_with_reversed_timestamps(self):
        with self.assertRaises(ValueError):
            list(read_input(["1030000,1,0", "1000000,0,2"]))

    def test_stream_counts_wire_operations_and_keeps_visual_result_open(self):
        timestamp = " start=2026-11-21T11:47:30.5023260+00:00 end=2026-11-21T11:47:30.5052074+00:00"
        log = "\n".join(row + timestamp for row in (
            "frame=1 type=full reports=12 result=OLED_FRAME_SENT",
            "frame=2 type=delta reports=1 result=OLED_FRAME_SENT",
            "frame=3 type=unchanged reports=0 result=OLED_FRAME_UNCHANGED",
            "frame=4 type=full reports=2 result=OLED_FRAME_DEFERRED",
        )) + "\nframe=5 type=delta-retry reports=1 result=OLED_FRAME_SENT" + timestamp + " confirmed=1"
        log += "\nframe=6 type=button-held reports=0 result=OLED_FRAME_DEFERRED" + timestamp
        result = analyze_stream([], log)
        self.assertEqual(result["fullFramesSubmitted"], 1)
        self.assertEqual(result["deltaFramesSubmitted"], 1)
        self.assertEqual(result["fullFramesDeferred"], 1)
        self.assertEqual(result["unchangedFrames"], 1)
        self.assertEqual(result["deltaRetriesSubmitted"], 1)
        self.assertEqual(result["heldButtonDeferrals"], 1)
        self.assertEqual(result["deviceConfirmations"], 1)
        self.assertTrue(result["deltaTiming"][1]["retry"])
        self.assertEqual(result["deltaTiming"][0]["wallSpanMs"], 2.8814)
        self.assertIsNone(result["deltaTiming"][0]["visible"])
        with self.assertRaises(ValueError):
            analyze_stream([], log.replace("type=delta reports=1", "type=delta reports=0"))
