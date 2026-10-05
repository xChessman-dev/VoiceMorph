"""Local, optional RVC v1/v2 inference worker. JSON lines in/out; no network.

Models are data, never executable Python: all checkpoint loads use weights_only
on a patched PyTorch version, with no allow-list or pickle fallback. The official
vocoder implementation and shared feature/pitch assets are pinned by the installer.
"""
from __future__ import annotations

import argparse
import base64
import contextlib
import gc
import hashlib
import importlib.util
import json
import math
import os
from pathlib import Path
import sys
import tempfile
import time
import zipfile

os.environ.setdefault("OMP_NUM_THREADS", "2")
os.environ.setdefault("MKL_NUM_THREADS", "2")
os.environ.setdefault("OPENBLAS_NUM_THREADS", "2")
os.environ.setdefault("TOKENIZERS_PARALLELISM", "false")
os.environ.setdefault("HF_HUB_OFFLINE", "1")
os.environ.setdefault("TRANSFORMERS_OFFLINE", "1")
os.environ.setdefault("RVC_CUDA_GRAPH", "0")

PROTOCOL_VERSION = 1
MAX_MODEL_BYTES = 768 * 1024 * 1024
MAX_INDEX_BYTES = 512 * 1024 * 1024
MAX_REQUEST_BYTES = 2 * 1024 * 1024
ASSETS = {
    "assets/hubert_base/config.json": "0346950779dfb7f9316fa74ed846e2b8a22a08eedfdc5387b73f327cb1a4a7cf",
    "assets/hubert_base/preprocessor_config.json": "7c1976a680fb7acc757cd36fb08eef878fa36c70b4c9d2d595df9c608bbbbf0e",
    "assets/hubert_base/pytorch_model.bin": "cc8c20f4b90a520757260197a3ff2505705a7adbd20ad9eeaa4e1a9b38442ef5",
    "assets/rmvpe/rmvpe.pt": "6d62215f4306e3ca278246188607209f09af3dc77ed4232efdd069798c4ec193",
}
VENDOR_FILES = ("LICENSE", "infer/module/models.py", "infer/module/modules.py", "infer/module/commons.py",
                "infer/module/attentions.py", "infer/module/transforms.py", "infer/rmvpe.py", "tools/cuda_graph.py")


class WorkerError(Exception):
    def __init__(self, code: str, message: str):
        super().__init__(message)
        self.code = code


def bounded_number(value, default, minimum, maximum):
    value = default if value is None else float(value)
    if not math.isfinite(value) or not minimum <= value <= maximum:
        raise WorkerError("invalid_option", f"Value must be between {minimum} and {maximum}.")
    return value


def checked_file(value, suffix, max_bytes):
    if not isinstance(value, str) or not value:
        raise WorkerError("missing_file", "A file path is required.")
    path = Path(value).resolve(strict=True)
    if not path.is_file() or path.suffix.lower() != suffix:
        raise WorkerError("invalid_file", f"Expected a {suffix} file.")
    if not 0 < path.stat().st_size <= max_bytes:
        raise WorkerError("file_too_large", "The file is empty or exceeds the safety limit.")
    return path


def render_paths(input_value, output_value):
    if not isinstance(input_value, str) or not input_value or not isinstance(output_value, str) or not output_value:
        raise WorkerError("invalid_output_path", "Separate input and output file paths are required.")
    input_path = Path(input_value).resolve(strict=True)
    output_path = Path(output_value).resolve()
    same_file = input_path == output_path or (output_path.exists() and input_path.samefile(output_path))
    if not input_path.is_file() or same_file or output_path.suffix.lower() != ".wav" or not output_path.parent.is_dir():
        raise WorkerError("invalid_output_path", "Use a separate WAV output in an existing directory.")
    return input_path, output_path


def write_atomic_wav(output_path, audio, sample_rate):
    import soundfile as sf
    descriptor, temporary_path = tempfile.mkstemp(prefix=".voicemorph-", suffix=".wav", dir=output_path.parent)
    os.close(descriptor)
    try:
        sf.write(temporary_path, audio, sample_rate, subtype="PCM_16")
        # Replace only after the complete WAV has been closed; preserve prior output on error.
        os.replace(temporary_path, output_path)
    finally:
        if os.path.exists(temporary_path):
            os.unlink(temporary_path)


def expand_rvc_features(features, frame_count):
    import torch.nn.functional as functional
    # ContentVec has a 20 ms stride, RVC uses 10 ms. Duplicate each frame at
    # exactly 2x rather than stretching the full utterance to fit the two-frame
    # convolution edge deficit. Replicated edge frames remain inside padding.
    expanded = functional.interpolate(features.transpose(1, 2), scale_factor=2., mode="nearest")
    missing = frame_count - expanded.shape[-1]
    if missing > 0:
        expanded = functional.pad(expanded, (0, missing), mode="replicate")
    return expanded[:, :, :frame_count].transpose(1, 2)


def safe_checkpoint(path):
    import torch
    version = tuple(int(p) for p in torch.__version__.split("+")[0].split(".")[:2])
    if version < (2, 10):
        raise WorkerError("unsafe_torch", "PyTorch 2.10 or newer is required for safe imported checkpoint loading.")
    # Modern tensor archives only. Do not accept legacy pickle-only checkpoints.
    if not zipfile.is_zipfile(path):
        raise WorkerError("legacy_checkpoint", "Legacy pickle checkpoints are not accepted. Re-export safely with a trusted RVC installation.")
    with zipfile.ZipFile(path) as archive:
        entries = archive.infolist()
        if len(entries) > 4096 or sum(e.file_size for e in entries) > MAX_MODEL_BYTES:
            raise WorkerError("invalid_checkpoint", "Checkpoint storage exceeds the safety limit.")
        if any(e.flag_bits & 1 or e.file_size > MAX_MODEL_BYTES for e in entries):
            raise WorkerError("invalid_checkpoint", "Unsupported checkpoint storage.")
    try:
        return torch.load(path, map_location="cpu", weights_only=True)
    except Exception as error:
        raise WorkerError("unsafe_checkpoint", "Safe tensor loading failed. Custom pickle objects are deliberately unsupported.") from error


def inspect_model(path):
    import torch
    checkpoint = safe_checkpoint(path)
    if not isinstance(checkpoint, dict):
        raise WorkerError("not_rvc", "This is not an exported RVC checkpoint.")
    weights, config = checkpoint.get("weight"), checkpoint.get("config")
    if not isinstance(weights, dict) or not isinstance(config, (list, tuple)) or len(config) != 18:
        raise WorkerError("not_rvc", "Expected RVC inference weights and an 18-field configuration, not a training checkpoint.")
    if not weights or len(weights) > 2500 or not all(isinstance(k, str) and isinstance(v, torch.Tensor) for k, v in weights.items()):
        raise WorkerError("not_rvc", "The RVC weight dictionary contains unsupported values.")
    if sum(v.numel() for v in weights.values()) > 150_000_000:
        raise WorkerError("invalid_configuration", "The model exceeds the supported parameter budget.")
    version = checkpoint.get("version", "v1")
    if version not in ("v1", "v2"):
        raise WorkerError("unsupported_version", "Only standard RVC v1 and v2 exports are supported.")
    uses_f0 = checkpoint.get("f0", 1)
    if uses_f0 not in (0, 1, False, True):
        raise WorkerError("invalid_configuration", "Invalid F0 flag.")
    embedding = weights.get("emb_g.weight")
    phone = weights.get("enc_p.emb_phone.weight")
    dimension = 256 if version == "v1" else 768
    if embedding is None or embedding.ndim != 2 or not 1 <= embedding.shape[0] <= 256:
        raise WorkerError("invalid_configuration", "Invalid speaker embedding.")
    if phone is None or phone.ndim != 2 or phone.shape[1] != dimension:
        raise WorkerError("invalid_configuration", "Feature dimensions do not match the RVC version.")
    # Strict bounds prevent malicious configuration-driven allocation before inference.
    bounds = {0: (128, 4097), 1: (1, 4096), 2: (16, 512), 3: (16, 512),
              4: (16, 2048), 5: (1, 16), 6: (1, 24), 7: (1, 31),
              13: (32, 1024), 15: (1, 256), 16: (1, 512)}
    for position, (low, high) in bounds.items():
        value = config[position]
        if not isinstance(value, int) or isinstance(value, bool) or not low <= value <= high:
            raise WorkerError("invalid_configuration", f"Unsupported RVC configuration field {position}.")
    if not isinstance(config[8], (int, float)) or not 0 <= config[8] <= 1 or str(config[9]) not in ("1", "2"):
        raise WorkerError("invalid_configuration", "Unsupported RVC dropout or residual block.")
    for position in (10, 12, 14):
        values = config[position]
        if not isinstance(values, (list, tuple)) or not 1 <= len(values) <= 8 or not all(isinstance(v, int) and 1 <= v <= 64 for v in values):
            raise WorkerError("invalid_configuration", "Unsupported RVC kernel/upsampling configuration.")
    dilations = config[11]
    if not isinstance(dilations, (list, tuple)) or len(dilations) != len(config[10]):
        raise WorkerError("invalid_configuration", "Unsupported RVC dilation configuration.")
    if any(not isinstance(row, (list, tuple)) or not 1 <= len(row) <= 8 or any(not isinstance(v, int) or not 1 <= v <= 64 for v in row) for row in dilations):
        raise WorkerError("invalid_configuration", "Unsupported RVC dilation configuration.")
    sample_rate = {"32k": 32000, "40k": 40000, "48k": 48000}.get(config[-1], config[-1])
    if sample_rate not in (32000, 40000, 48000) or math.prod(config[12]) != sample_rate // 100:
        raise WorkerError("invalid_configuration", "Unsupported sample rate or upsampling factor.")
    if len(config[12]) != len(config[14]) or config[16] != embedding.shape[1]:
        raise WorkerError("invalid_configuration", "Inconsistent RVC speaker or upsampling configuration.")
    config = list(config)
    config[-3] = embedding.shape[0]
    metadata = {"version": version, "sample_rate": sample_rate,
                "uses_f0": bool(uses_f0), "speaker_count": int(embedding.shape[0]),
                "feature_dimension": dimension, "parameter_count": sum(v.numel() for v in weights.values())}
    return checkpoint, config, metadata


class RvcSession:
    def __init__(self, root):
        self.root = Path(root).resolve()
        self.net = self.hubert = self.pitch = self.index = self.metadata = None
        self.history = None
        self.options = {}
        self.device = "cpu"
        self.verified_assets = False
        vendor = self.root / "vendor"
        # Only the application's pinned runtime, never a model directory, is importable.
        sys.path.insert(0, str(vendor))

    def info(self):
        packages = ["torch", "numpy", "scipy", "soundfile", "transformers", "librosa"]
        missing = [p for p in packages if importlib.util.find_spec(p) is None]
        files = [*ASSETS, *("vendor/" + f for f in VENDOR_FILES)]
        missing_files = [f for f in files if not (self.root / f).is_file()]
        result = {"protocol_version": PROTOCOL_VERSION, "ready": not missing and not missing_files,
                  "python_version": sys.version.split()[0], "missing_packages": missing,
                  "missing_files": missing_files, "runtime_root": str(self.root), "loaded": self.net is not None,
                  "cuda_available": False, "index_support": importlib.util.find_spec("faiss") is not None}
        if "torch" not in missing:
            import torch
            result["torch_version"] = torch.__version__
            result["cuda_available"] = torch.cuda.is_available()
            result["cuda_version"] = torch.version.cuda
            if result["cuda_available"]:
                result["gpu_name"] = torch.cuda.get_device_name(0)
                result["gpu_memory_bytes"] = torch.cuda.get_device_properties(0).total_memory
            if tuple(int(p) for p in torch.__version__.split("+")[0].split(".")[:2]) < (2, 10):
                result["ready"] = False
                result["missing_packages"].append("torch>=2.10 (security update)")
        return result

    def verify_assets(self):
        if self.verified_assets:
            return
        for relative, expected in ASSETS.items():
            path = self.root / relative
            with path.open("rb") as asset:
                digest = hashlib.file_digest(asset, "sha256").hexdigest()
            if digest != expected:
                raise WorkerError("asset_checksum", "A shared RVC asset failed its pinned SHA-256 verification.")
        self.verified_assets = True

    def unload(self):
        self.net = self.hubert = self.pitch = self.mel = self.index = self.metadata = self.history = None
        gc.collect()
        if importlib.util.find_spec("torch"):
            import torch
            if torch.cuda.is_available():
                torch.cuda.empty_cache()
        return {"loaded": False}

    def load(self, request):
        status = self.info()
        if not status["ready"]:
            raise WorkerError("runtime_missing", "Optional RVC runtime is not installed or is incomplete. Run tools/setup-rvc-runtime.ps1.")
        self.verify_assets()
        self.unload()
        import numpy as np
        import torch
        from transformers import HubertConfig, HubertModel
        from infer.module.models import (SynthesizerTrnMs256NSFsid, SynthesizerTrnMs256NSFsid_nono,
                                          SynthesizerTrnMs768NSFsid, SynthesizerTrnMs768NSFsid_nono)
        from infer.rmvpe import E2E, MelSpectrogram
        model_path = checked_file(request.get("model_path"), ".pth", MAX_MODEL_BYTES)
        checkpoint, config, metadata = inspect_model(model_path)
        selected = request.get("device", "auto")
        if selected not in ("auto", "cpu", "cuda"):
            raise WorkerError("invalid_device", "Device must be auto, cpu, or cuda.")
        if selected == "cuda" and not torch.cuda.is_available():
            raise WorkerError("cuda_unavailable", "CUDA is unavailable in this runtime.")
        self.device = "cuda:0" if selected != "cpu" and torch.cuda.is_available() else "cpu"
        torch.set_num_threads(2)
        # FP32 is deliberately the initial compatibility baseline for imported models.
        constructors = {("v1", True): SynthesizerTrnMs256NSFsid, ("v1", False): SynthesizerTrnMs256NSFsid_nono,
                        ("v2", True): SynthesizerTrnMs768NSFsid, ("v2", False): SynthesizerTrnMs768NSFsid_nono}
        net = constructors[metadata["version"], metadata["uses_f0"]](*config, is_half=False)
        del net.enc_q
        keys = net.load_state_dict(checkpoint["weight"], strict=False)
        if keys.missing_keys or any(not k.startswith("enc_q.") for k in keys.unexpected_keys):
            raise WorkerError("incompatible_weights", "Model tensors do not match the standard RVC architecture.")
        self.net = net.float().eval().to(self.device)
        self.net.remove_weight_norm()
        del checkpoint, net
        hubert_dir = self.root / "assets/hubert_base"
        hubert_config = HubertConfig.from_json_file(str(hubert_dir / "config.json"))
        if hubert_config.hidden_size != 768 or hubert_config.num_hidden_layers != 12:
            raise WorkerError("invalid_asset", "Unsupported shared ContentVec configuration.")
        class HubertWithProjection(HubertModel):
            def __init__(self, cfg):
                super().__init__(cfg)
                self.final_proj = torch.nn.Linear(cfg.hidden_size, cfg.classifier_proj_size)
        self.hubert = HubertWithProjection(hubert_config)
        self.hubert.load_state_dict(safe_checkpoint(hubert_dir / "pytorch_model.bin"), strict=True)
        self.hubert = self.hubert.float().eval().to(self.device)
        with (hubert_dir / "preprocessor_config.json").open(encoding="utf-8") as config_file:
            self.normalize_features = bool(json.load(config_file).get("do_normalize", False))
        if metadata["uses_f0"]:
            self.pitch = E2E(4, 1, (2, 2))
            self.pitch.load_state_dict(safe_checkpoint(self.root / "assets/rmvpe/rmvpe.pt"), strict=True)
            self.pitch = self.pitch.float().eval().to(self.device)
            self.mel = MelSpectrogram(False, 128, 16000, 1024, 160, None, 30, 8000).to(self.device)
            self.cents_mapping = np.pad(20 * np.arange(360) + 1997.3794084376191, (4, 4))
        self.metadata = metadata
        self.options = {
            "pitch_semitones": bounded_number(request.get("pitch_semitones"), 0, -24, 24),
            "index_rate": bounded_number(request.get("index_rate"), .5, 0, 1),
            "protect": bounded_number(request.get("protect"), .33, 0, .5),
            "speaker_id": int(bounded_number(request.get("speaker_id"), 0, 0, metadata["speaker_count"] - 1)),
            "trust_index": request.get("trust_index") is True,
        }
        index_path = request.get("index_path")
        if index_path and self.options["index_rate"] > 0 and self.options["trust_index"]:
            self.index = self.read_index(index_path, metadata["feature_dimension"])
        if self.index is None:
            self.options["index_rate"] = 0
        self.history = np.zeros(0, dtype=np.float32)
        return {"model": metadata, "device": self.device, "loaded": True, "index_loaded": self.index is not None}

    @staticmethod
    def read_index(path, dimension):
        import faiss
        index_path = checked_file(path, ".index", MAX_INDEX_BYTES)
        with index_path.open("rb") as binary:
            if binary.read(4) not in (b"IwFl", b"IxF2", b"IxFI", b"IxFp"):
                raise WorkerError("unsupported_index", "Only standard RVC IVF/Flat indices are supported.")
        index = faiss.read_index(str(index_path))
        if index.d != dimension or not 1 <= index.ntotal <= 1_000_000 or not index.is_trained:
            raise WorkerError("incompatible_index", "The index is untrained, too large, or has a different feature dimension.")
        # Imported files cannot select a GPU/faiss plugin. Retrieval stays CPU bounded.
        if hasattr(index, "nprobe"):
            index.nprobe = min(4, getattr(index, "nlist", 4))
        faiss.omp_set_num_threads(2)
        return index

    def pitch_track(self, audio, length):
        import numpy as np
        import torch
        import torch.nn.functional as functional
        mel = self.mel(torch.from_numpy(audio).to(self.device).unsqueeze(0), center=True)
        frames = mel.shape[-1]
        mel = functional.pad(mel, (0, (32 - frames % 32) % 32))
        salience = self.pitch(mel)[:, :frames].squeeze(0).float().cpu().numpy()
        center = np.argmax(salience, axis=1) + 4
        padded = np.pad(salience, ((0, 0), (4, 4)))
        bins = center[:, None] + np.arange(-4, 5)[None, :]
        local = np.take_along_axis(padded, bins, axis=1)
        cents = np.sum(local * self.cents_mapping[bins], axis=1) / np.maximum(np.sum(local, axis=1), 1e-8)
        f0 = 10 * 2 ** (cents / 1200)
        f0[np.max(salience, axis=1) <= .03] = 0
        # RMVPE already uses the same 160-sample/10 ms hop as RVC. Preserve
        # timestamps; centered STFT commonly returns one extra final frame.
        f0 = f0[:length]
        if len(f0) < length:
            f0 = np.pad(f0, (0, length - len(f0)))
        f0 *= 2 ** (self.options["pitch_semitones"] / 12)
        coarse = 1127 * np.log1p(f0 / 700)
        minimum, maximum = 1127 * np.log1p(50 / 700), 1127 * np.log1p(1100 / 700)
        coarse[coarse > 0] = (coarse[coarse > 0] - minimum) * 254 / (maximum - minimum) + 1
        coarse = np.rint(np.clip(coarse, 1, 255)).astype(np.int64)
        return torch.from_numpy(coarse).to(self.device).unsqueeze(0), torch.from_numpy(f0.astype(np.float32)).to(self.device).unsqueeze(0)

    def infer(self, audio):
        import numpy as np
        import torch
        import torch.nn.functional as functional
        from scipy.signal import butter, sosfilt
        if self.net is None:
            raise WorkerError("model_not_loaded", "Load a model before conversion.")
        audio = np.asarray(audio, dtype=np.float32)
        if audio.ndim != 1 or len(audio) < 400 or not np.all(np.isfinite(audio)):
            raise WorkerError("invalid_audio", "Expected finite mono audio with at least 25 ms of samples.")
        original_length = len(audio)
        audio = sosfilt(butter(5, 48, btype="highpass", fs=16000, output="sos"), audio).astype(np.float32)
        peak = float(np.max(np.abs(audio)))
        if peak > .95:
            audio *= .95 / peak
        # Round up (not down) to the vocoder's 10 ms frame: 25 ms blocks must not
        # lose their last 5 ms or acquire an artificial zero tail.
        audio = np.pad(audio, (1600, 1600 + (-original_length % 160)), mode="reflect")
        frame_count = len(audio) // 160
        with torch.inference_mode():
            source = torch.from_numpy(audio).to(self.device).unsqueeze(0)
            if self.normalize_features:
                source = functional.layer_norm(source, source.shape[-1:])
            outputs = self.hubert(input_values=source, output_hidden_states=self.metadata["version"] == "v1", return_dict=True)
            feats = self.hubert.final_proj(outputs.hidden_states[9]) if self.metadata["version"] == "v1" else outputs.last_hidden_state
            original_features = feats.clone()
            if self.index is not None and self.options["index_rate"] > 0:
                values = np.ascontiguousarray(feats[0].float().cpu().numpy())
                distances, indices, vectors = self.index.search_and_reconstruct(values, min(8, self.index.ntotal))
                valid = (indices >= 0) & np.isfinite(distances)
                weights = np.where(valid, 1 / np.maximum(distances, 1e-6) ** 2, 0)
                weights /= np.maximum(weights.sum(axis=1, keepdims=True), 1e-12)
                retrieved = np.sum(np.nan_to_num(vectors) * weights[:, :, None], axis=1)
                retrieved = torch.from_numpy(retrieved).to(self.device)
                rate = self.options["index_rate"]
                feats = retrieved.unsqueeze(0) * rate + feats * (1 - rate)
            feats = expand_rvc_features(feats, frame_count)
            lengths = torch.tensor([frame_count], device=self.device, dtype=torch.long)
            speaker = torch.tensor([self.options["speaker_id"]], device=self.device, dtype=torch.long)
            if self.metadata["uses_f0"]:
                pitch, pitch_f = self.pitch_track(audio, frame_count)
                if self.options["protect"] < .5 and self.index is not None:
                    dry = expand_rvc_features(original_features, frame_count)
                    mix = torch.where(pitch_f > 0, 1., self.options["protect"]).unsqueeze(-1)
                    feats = feats * mix + dry * (1 - mix)
                result = self.net.infer(feats, lengths, pitch, pitch_f, speaker)[0][0, 0]
            else:
                result = self.net.infer(feats, lengths, speaker)[0][0, 0]
            result = result.float().cpu().numpy()
        margin = self.metadata["sample_rate"] // 10
        expected = round(original_length * self.metadata["sample_rate"] / 16000)
        result = result[margin:margin + expected]
        if len(result) < expected:
            result = np.pad(result, (0, expected - len(result)))
        if not np.all(np.isfinite(result)):
            raise WorkerError("invalid_output", "The selected model produced non-finite audio.")
        return np.clip(result, -.98, .98).astype(np.float32)

    def render(self, request):
        import numpy as np
        import soundfile as sf
        from scipy.signal import resample_poly
        input_path, output_path = render_paths(request.get("input_path"), request.get("output_path"))
        if self.net is None or request.get("model_path"):
            try:
                self.load(request)
            except Exception:
                self.unload()
                raise
        else:
            self.update_options(request)
        file_info = sf.info(str(input_path))
        if not .025 <= file_info.duration <= 120 or not 8000 <= file_info.samplerate <= 192000 or file_info.channels > 8:
            raise WorkerError("invalid_audio", "Supported test files are mono/stereo audio up to 120 seconds.")
        audio, rate = sf.read(str(input_path), dtype="float32", always_2d=True)
        audio = audio.mean(axis=1)
        factor = math.gcd(rate, 16000)
        audio = resample_poly(audio, 16000 // factor, rate // factor).astype(np.float32)
        started = time.perf_counter()
        output_rate = self.metadata["sample_rate"]
        result = np.zeros(round(len(audio) * output_rate / 16000), dtype=np.float32)
        # Bounded ten-second slices with context; no whole-file GPU memory growth.
        chunk, margin = 160000, 4000
        for offset in range(0, len(audio), chunk):
            end = min(offset + chunk, len(audio))
            left, right = max(0, offset - margin), min(len(audio), end + margin)
            converted = self.infer(audio[left:right])
            crop = round((offset - left) * output_rate / 16000)
            begin, finish = round(offset * output_rate / 16000), round(end * output_rate / 16000)
            result[begin:finish] = converted[crop:crop + finish - begin]
        write_atomic_wav(output_path, result, output_rate)
        elapsed = time.perf_counter() - started
        return {"output_path": str(output_path), "sample_rate": output_rate, "duration_seconds": len(audio) / 16000,
                "processing_ms": elapsed * 1000, "real_time_factor": elapsed / (len(audio) / 16000), "device": self.device}

    def convert_samples(self, request):
        import numpy as np
        if self.net is None:
            raise WorkerError("model_not_loaded", "Load a model first.")
        self.update_options(request)
        if request.get("sample_rate", 16000) != 16000:
            raise WorkerError("invalid_audio", "Streaming input must be mono float32 at 16000 Hz.")
        encoded = request.get("samples_base64", "")
        if not isinstance(encoded, str) or len(encoded) > 90000:
            raise WorkerError("invalid_audio", "Streaming blocks must be between 25 ms and one second.")
        raw = base64.b64decode(encoded, validate=True)
        if len(raw) % 4:
            raise WorkerError("invalid_audio", "Float32 data length is invalid.")
        block = np.frombuffer(raw, dtype="<f4").copy()
        if not 400 <= len(block) <= 16000 or not np.all(np.isfinite(block)):
            raise WorkerError("invalid_audio", "Streaming blocks must be finite and between 25 ms and one second.")
        if request.get("reset"):
            self.history = np.zeros(0, dtype=np.float32)
        history_length = round(bounded_number(request.get("history_seconds"), 1., .25, 3.) * 16000)
        context = np.concatenate((self.history[-history_length:], block))
        started = time.perf_counter()
        converted = self.infer(context)
        output_samples = round(len(block) * self.metadata["sample_rate"] / 16000)
        converted = converted[-output_samples:]
        self.history = context[-history_length:].copy()
        elapsed = time.perf_counter() - started
        return {"samples_base64": base64.b64encode(converted.astype("<f4").tobytes()).decode("ascii"),
                "sample_rate": self.metadata["sample_rate"], "processing_ms": elapsed * 1000,
                "input_duration_ms": len(block) / 16, "real_time_factor": elapsed / (len(block) / 16000), "device": self.device}

    def handle(self, request):
        command = request.get("command")
        if command == "info":
            return self.info()
        if command == "inspect":
            model_path = checked_file(request.get("model_path"), ".pth", MAX_MODEL_BYTES)
            _, _, metadata = inspect_model(model_path)
            # Native FAISS parsing is NOT a safety validation. Never do it on import.
            index_path = request.get("index_path")
            if index_path:
                checked_file(index_path, ".index", MAX_INDEX_BYTES)
            return {"model": metadata, "index_loaded": False, "index_present": bool(index_path)}
        if command == "load":
            try:
                return self.load(request)
            except Exception:
                self.unload()
                raise
        if command == "unload":
            return self.unload()
        if command == "render":
            return self.render(request)
        if command in ("convertSamples", "convert_samples"):
            return self.convert_samples(request)
        if command == "setOptions":
            if self.net is None:
                raise WorkerError("model_not_loaded", "Load a model first.")
            self.update_options(request)
            return {"options": self.options}
        raise WorkerError("unknown_command", "Unknown worker command.")

    def update_options(self, request):
        candidate = dict(self.options)
        for key, limits in {"pitch_semitones": (-24, 24), "index_rate": (0, 1), "protect": (0, .5), "speaker_id": (0, self.metadata["speaker_count"] - 1)}.items():
            if key in request:
                value = bounded_number(request[key], self.options[key], *limits)
                candidate[key] = int(value) if key == "speaker_id" else value
        # Consent is a loaded-session capability, not a one-block rate override.
        # Revocation drops native index memory. Subsequent blocks cannot re-enable
        # retrieval merely by sending index_rate; fresh explicit load is required.
        if "trust_index" in request and request["trust_index"] is not True:
            candidate["trust_index"] = False
            self.index = None
        if self.index is None or not candidate["trust_index"]:
            candidate["index_rate"] = 0
        self.options = candidate


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--runtime-root", default=os.environ.get("VOICEMORPH_RUNTIME_ROOT", str(Path(__file__).resolve().parent / "rvc")))
    options = parser.parse_args()
    session = RvcSession(options.runtime_root)
    # Diagnostics from third-party packages belong on stderr, never the protocol.
    while True:
        line = sys.stdin.buffer.readline(MAX_REQUEST_BYTES + 1)
        if not line:
            break
        request_id = None
        try:
            if len(line) > MAX_REQUEST_BYTES or not line.endswith(b"\n"):
                raise WorkerError("request_too_large", "Request exceeds the protocol size limit.")
            request = json.loads(line)
            if not isinstance(request, dict):
                raise WorkerError("invalid_request", "Expected a JSON object.")
            request_id = request.get("id")
            with contextlib.redirect_stdout(sys.stderr):
                result = session.handle(request)
            response = {"id": request_id, "ok": True, **result}
        except Exception as error:
            response = {"id": request_id, "ok": False, "code": getattr(error, "code", "inference_error"), "error": str(error)}
        sys.stdout.write(json.dumps(response, ensure_ascii=True, allow_nan=False) + "\n")
        sys.stdout.flush()
    session.unload()


if __name__ == "__main__":
    main()
