"""Dependency-free protocol/security regression tests; real inference is separate."""
import importlib.util
import json
import math
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest import mock

worker_file = Path(__file__).with_name("rvc_worker.py")
spec = importlib.util.spec_from_file_location("voicemorph_rvc_worker", worker_file)
worker = importlib.util.module_from_spec(spec)
spec.loader.exec_module(worker)


class WorkerBoundaries(unittest.TestCase):
    def test_bounds_accept_defaults(self):
        self.assertEqual(worker.bounded_number(None, .5, 0, 1), .5)

    def test_bounds_reject_nonfinite_and_outside(self):
        for value in (math.nan, math.inf, -math.inf, -1, 2):
            with self.assertRaises(worker.WorkerError):
                worker.bounded_number(value, .5, 0, 1)

    def test_checked_file_rejects_bad_extension(self):
        with self.assertRaises(worker.WorkerError):
            worker.checked_file(str(worker_file), ".pth", 1024 * 1024)

    def test_checked_file_rejects_empty(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "empty.pth"
            path.touch()
            with self.assertRaises(worker.WorkerError):
                worker.checked_file(str(path), ".pth", 1024)

    def test_info_is_truthful_for_empty_runtime(self):
        with tempfile.TemporaryDirectory() as directory:
            session = worker.RvcSession(directory)
            result = session.info()
            self.assertFalse(result["ready"])
            self.assertFalse(result["loaded"])
            self.assertTrue(result["missing_files"])
            self.assertEqual(result["protocol_version"], 1)

    def test_unknown_command(self):
        with tempfile.TemporaryDirectory() as directory:
            session = worker.RvcSession(directory)
            with self.assertRaises(worker.WorkerError) as captured:
                session.handle({"command": "execute_pickle"})
            self.assertEqual(captured.exception.code, "unknown_command")

    def test_options_without_model_are_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            with self.assertRaises(worker.WorkerError):
                worker.RvcSession(directory).handle({"command": "setOptions", "pitch_semitones": 1})

    def test_json_lines_echo_ids_and_continue_after_errors(self):
        messages = ["not JSON", json.dumps({"id": 2, "command": "unknown"}), json.dumps({"id": 3, "command": "info"})]
        with tempfile.TemporaryDirectory() as directory:
            result = subprocess.run([sys.executable, "-I", str(worker_file), "--runtime-root", directory],
                                    input="\n".join(messages) + "\n", text=True, capture_output=True, timeout=30)
        self.assertEqual(result.returncode, 0, result.stderr)
        responses = [json.loads(line) for line in result.stdout.splitlines()]
        self.assertEqual(len(responses), 3)
        self.assertFalse(responses[0]["ok"])
        self.assertEqual(responses[1]["id"], 2)
        self.assertEqual(responses[1]["code"], "unknown_command")
        self.assertEqual(responses[2]["id"], 3)
        self.assertTrue(responses[2]["ok"])
        self.assertFalse(responses[2]["ready"])

    def test_loader_source_has_no_unsafe_fallback(self):
        source = worker_file.read_text(encoding="utf-8")
        self.assertIn("weights_only=True", source)
        self.assertNotIn("weights_only=False", source)
        self.assertNotIn("add_safe_globals", source)
        self.assertNotIn("pickle.load", source)
        self.assertIn("version < (2, 10)", source)

    def test_index_loading_requires_explicit_trust(self):
        source = worker_file.read_text(encoding="utf-8")
        self.assertIn('request.get("trust_index") is True', source)
        self.assertIn('and self.options["trust_index"]', source)

    def test_index_revocation_cannot_be_undone_by_next_block(self):
        with tempfile.TemporaryDirectory() as directory:
            session = worker.RvcSession(directory)
            session.metadata = {"speaker_count": 1}
            session.options = {"pitch_semitones": 0, "index_rate": .5, "protect": .33, "speaker_id": 0, "trust_index": True}
            session.index = object()
            session.update_options({"trust_index": False})
            self.assertIsNone(session.index)
            self.assertFalse(session.options["trust_index"])
            session.update_options({"index_rate": .8, "trust_index": True})
            self.assertEqual(session.options["index_rate"], 0)
            self.assertFalse(session.options["trust_index"])

    def test_invalid_options_do_not_partially_mutate(self):
        with tempfile.TemporaryDirectory() as directory:
            session = worker.RvcSession(directory)
            session.metadata = {"speaker_count": 1}
            session.options = {"pitch_semitones": 0, "index_rate": .5, "protect": .33, "speaker_id": 0, "trust_index": True}
            session.index = object()
            original = dict(session.options)
            with self.assertRaises(worker.WorkerError):
                session.update_options({"pitch_semitones": 5, "index_rate": 4, "trust_index": False})
            self.assertEqual(session.options, original)
            self.assertIsNotNone(session.index)

    def test_render_rejects_same_input_even_via_hardlink(self):
        with tempfile.TemporaryDirectory() as directory:
            source = Path(directory) / "source.wav"
            alias = Path(directory) / "alias.wav"
            source.write_bytes(b"original")
            with self.assertRaises(worker.WorkerError):
                worker.render_paths(str(source), str(source))
            os.link(source, alias)
            with self.assertRaises(worker.WorkerError):
                worker.render_paths(str(source), str(alias))
            self.assertEqual(source.read_bytes(), b"original")

    def test_atomic_wav_write_preserves_previous_output_on_error(self):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / "output.wav"
            output.write_bytes(b"previous-output")
            fake_sf = mock.Mock()
            fake_sf.write.side_effect = OSError("simulated encoding failure")
            with mock.patch.dict(sys.modules, {"soundfile": fake_sf}):
                with self.assertRaises(OSError):
                    worker.write_atomic_wav(output, [0.], 48000)
            self.assertEqual(output.read_bytes(), b"previous-output")
            self.assertEqual(list(Path(directory).iterdir()), [output])

    def test_atomic_wav_write_replaces_only_completed_file(self):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / "output.wav"
            output.write_bytes(b"old")
            fake_sf = mock.Mock()
            def complete(path, *_args, **_kwargs):
                self.assertEqual(output.read_bytes(), b"old")
                Path(path).write_bytes(b"complete-new-wav")
            fake_sf.write.side_effect = complete
            with mock.patch.dict(sys.modules, {"soundfile": fake_sf}):
                worker.write_atomic_wav(output, [0.], 48000)
            self.assertEqual(output.read_bytes(), b"complete-new-wav")
            self.assertEqual(list(Path(directory).iterdir()), [output])

    @unittest.skipUnless(importlib.util.find_spec("numpy"), "numpy unavailable")
    def test_stream_lengths_history_reset_and_boundaries(self):
        import numpy as np
        import base64
        with tempfile.TemporaryDirectory() as directory:
            for sample_rate in (32000, 40000, 48000):
                session = worker.RvcSession(directory)
                session.net = object()
                session.metadata = {"sample_rate": sample_rate, "speaker_count": 1}
                session.options = {"pitch_semitones": 0, "index_rate": 0, "protect": .33, "speaker_id": 0, "trust_index": False}
                session.history = np.zeros(0, dtype=np.float32)
                contexts = []
                def fake_infer(context):
                    contexts.append(len(context))
                    return np.zeros(round(len(context) * sample_rate / 16000), dtype=np.float32)
                session.infer = fake_infer
                for length in (400, 16000, 512):
                    request = {"sample_rate": 16000, "samples_base64": base64.b64encode(np.zeros(length, dtype="<f4").tobytes()).decode()}
                    result = session.convert_samples(request)
                    self.assertEqual(len(base64.b64decode(result["samples_base64"])), round(length * sample_rate / 16000) * 4)
                    self.assertLessEqual(len(session.history), 16000)
                request["reset"] = True
                session.convert_samples(request)
                self.assertEqual(contexts[-1], 512)
                for bad in (399, 16001):
                    request["samples_base64"] = base64.b64encode(np.zeros(bad, dtype="<f4").tobytes()).decode()
                    with self.assertRaises(worker.WorkerError):
                        session.convert_samples(request)


if __name__ == "__main__":
    unittest.main(verbosity=2)
