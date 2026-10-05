# Optional local RVC backend

The DSP mode remains independent of Python and GPU resources. RVC is a separate,
on-demand worker for standard exported RVC v1/v2 `.pth` models at 32/40/48 kHz.
It uses shared ContentVec and RMVPE assets and can optionally use the matching
FAISS `.index`. It does not train models, clone from a reference, upload audio,
or install a web server.

## Installation

Run `tools/setup-rvc-runtime.ps1 -RuntimeRoot 'D:/VoiceMorphRuntime'` and set
`VOICEMORPH_RUNTIME_ROOT` to that directory before launching the app.
All runtime files, package-install temporary files, and model downloads stay in
the selected runtime directory. GPU setup downloads about 3.2 GB and requires
9 GB free during installation; verified wheel/ZIP archives are removed afterward
unless `-KeepDownloads` is supplied. The CPU-only alternative is smaller, but is
not promised to keep up with live speech.

The installer pins Python 3.12.10, patched PyTorch 2.10.0, upstream RVC commit
`81eed5e8f68b6bed1789f682fe78cdd324495afc`, and shared asset revision
`e6d0c1a17da07c33557852f9dfa2bd44cc75737d`. Source/assets are verified with
SHA-256 before use. Upstream RVC source is MIT licensed; its LICENSE is retained
in the optional runtime vendor folder. Voice-model licensing remains separate.

## Security boundaries

Every checkpoint, including shared assets, uses `torch.load(weights_only=True)`
on PyTorch >= 2.10, followed by type/dimension/configuration bounds. Legacy
pickle-only files, custom classes, and training checkpoints are rejected. There
is no `weights_only=False`, `add_safe_globals`, or unsafe fallback.

An `.index` is **not** safe merely because its header looks correct. FAISS uses
native deserialization and explicitly warns that hostile files can cause memory
exhaustion or code execution. Import/inspection therefore never invokes FAISS.
Native index loading requires a separate explicit `trust_index: true` consent;
without it the voice model runs without retrieval. Only use indices from a
source you trust. The worker process is isolated from application crashes, not
a security sandbox for a malicious native file.

## JSON-lines protocol

Launch `python -I runtime/rvc_worker.py --runtime-root D:/VoiceMorphRuntime`.
Send one UTF-8 JSON object per line. Responses echo `id` and contain `ok`.
Failures contain `code` and `error`; stdout contains protocol only.

- `info`: dependency/asset availability, CUDA/GPU info; no model loaded.
- `inspect`: `model_path`, optional `index_path`; safe CPU checkpoint inspection.
- `load`: `model_path`, optional `index_path`, `device` (`auto/cpu/cuda`),
  `pitch_semitones`, `speaker_id`, `index_rate`, `protect`, `trust_index`.
- `render`: `input_path`, separate WAV `output_path`, optional load options;
  local audio up to 120 seconds, bounded ten-second inference slices. Output is
  written to a unique adjacent temporary WAV and atomically replaced only after
  successful encoding. Input-path aliases, including hard links, are rejected.
- `convertSamples`: `samples_base64` containing little-endian float32 mono
  at `sample_rate:16000`, 25–1000 ms per block, optional `reset`,
  `history_seconds` (0.25–3), optional mutable pitch/index/protect/speaker options.
  Returns float32 mono at the selected model's sample rate plus processing time.
- `setOptions`: update mutable pitch/index/protect/speaker options. A fresh load
  is required to enable an index that was not loaded with explicit consent.
  Setting `trust_index:false` revokes the current session and drops native index
  memory; a later block cannot restore it with `index_rate` or `trust_index:true`.
- `unload`: release model/GPU memory. EOF terminates the worker.

Live mode uses context plus block inference: its delay is not the DSP mode's
latency. `processing_ms` and `real_time_factor` are measured, not advertised
guarantees. Final latency includes capture/block/output buffering. Models should
first be tested on a recording and with the actual game running.

Voice naturalness, similarity and game FPS require testing with your own model,
microphone and workload. Pitch shifting is only supported by F0 models (the non-F0 architecture
has no explicit pitch input). Streaming uses reflected right-edge padding rather
than future audio and can have block-edge artifacts. Offline ten-second slices
have context but no overlap-add crossfade; long-file joins require a listening
test. These are testable constraints, not a promise of transparent conversion.

Primary upstream references:

- https://github.com/RVC-Project/Retrieval-based-Voice-Conversion-WebUI
- https://huggingface.co/lj1995/VoiceConversionWebUI
- https://github.com/pytorch/pytorch/security/advisories/GHSA-63cw-57p8-fm3p
- https://github.com/facebookresearch/faiss/wiki/Index-IO,-cloning-and-hyper-parameter-tuning
