"""WhisperCpp GPU support (Avalonia addition).

The original backend assumes the Const-me whisper.exe on Windows and passes `--no-gpu`
to every other whisper.cpp build, so WhisperCpp always runs on the CPU. When a
Vulkan build of whisper-cli is installed (BIN_PATH/whisper-cpp-vulkan, or the path in
WhisperCpp.Program), this module patches WhisperCppASR to run it with:

  WhisperCpp.Model         ggml file name in MODEL_PATH ("" keeps Whisper.WhisperModel)
  WhisperCpp.Device        "auto" | "cpu" | Vulkan device name (e.g. "AMD Radeon RX 6800 XT")
  WhisperCpp.VadFilter     silero VAD (MODEL_PATH/ggml-silero-*.bin)
  WhisperCpp.VadThreshold

The device is stored by name rather than index because Vulkan indices shift when a
GPU is enabled or disabled. Without the Vulkan build the original behaviour is kept.
"""
import json
import os
import re
import subprocess
from pathlib import Path

from videocaptioner.config import BIN_PATH, MODEL_PATH, SETTINGS_PATH
from videocaptioner.core.asr import whisper_cpp as wc
from videocaptioner.core.utils.logger import setup_logger

from vc_bridge import command

logger = setup_logger("whisper_cpp_gpu")

VULKAN_CLI = BIN_PATH / "whisper-cpp-vulkan" / "whisper-cli.exe"
_NO_WINDOW = subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0
_DEVICE_RE = re.compile(r"ggml_vulkan: (\d+) = (.+?) \(")
_device_cache: dict = {}


def _settings() -> dict:
    try:
        data = json.loads(SETTINGS_PATH.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return {}
    section = data.get("WhisperCpp", {})
    return section if isinstance(section, dict) else {}


def _program(settings: dict):
    path = Path(settings.get("Program") or VULKAN_CLI)
    return path if path.is_file() else None


def list_devices(program: Path) -> list:
    """Vulkan device names in index order, parsed from whisper-cli's backend init log."""
    key = str(program)
    if key not in _device_cache:
        try:
            proc = subprocess.run(
                [key, "--help"], capture_output=True, text=True, encoding="utf-8",
                errors="replace", timeout=60, creationflags=_NO_WINDOW,
            )
            found = _DEVICE_RE.findall(proc.stdout + proc.stderr)
            _device_cache[key] = [name for _, name in sorted(found, key=lambda x: int(x[0]))]
        except (OSError, subprocess.SubprocessError) as e:
            logger.warning("Failed to list Vulkan devices: %s", e)
            return []
    return _device_cache[key]


def _device_args(program: Path, device: str) -> list:
    if device == "cpu":
        return ["--no-gpu"]
    if device and device != "auto":
        names = list_devices(program)
        if device in names:
            return ["--device", str(names.index(device))]
        logger.warning("Vulkan device %r not found in %s, using auto", device, names)
    return []


def _vad_model():
    models = sorted(Path(MODEL_PATH).glob("ggml-silero-*.bin"))
    return models[-1] if models else None


def whisper_models() -> list:
    return sorted(
        p.name for p in Path(MODEL_PATH).glob("ggml-*.bin") if not p.name.startswith("ggml-silero-")
    )


_original_init = wc.WhisperCppASR.__init__
_original_build_command = wc.WhisperCppASR._build_command


def _init(self, audio_input, language="en", whisper_cpp_path=None, whisper_model=None,
          use_cache=False, need_word_time_stamp=False):
    settings = _settings()
    program = _program(settings)
    if program is None:
        _original_init(self, audio_input, language, whisper_cpp_path, whisper_model,
                       use_cache, need_word_time_stamp)
        return

    model_file = Path(MODEL_PATH) / settings.get("Model", "")
    use_model_file = bool(settings.get("Model")) and model_file.is_file()
    if use_model_file:
        # the original glob is *ggml*{name}*.bin; pin the exact file afterwards
        whisper_model = model_file.stem.removeprefix("ggml-")
    _original_init(self, audio_input, language, str(program), whisper_model,
                   use_cache, need_word_time_stamp)
    if use_model_file:
        self.model_path = str(model_file)
    self._gpu_settings = settings


def _build_command(self, wav_path, output_path, is_const_me_version):
    settings = getattr(self, "_gpu_settings", None)
    if settings is None:
        return _original_build_command(self, wav_path, output_path, is_const_me_version)

    cmd = [
        str(self.whisper_cpp_path),
        "-m", str(self.model_path),
        "-f", str(wav_path),
        "-l", self.language or "auto",
        "--output-srt",
        "--output-file", str(output_path.with_suffix("")),
    ]
    cmd += _device_args(self.whisper_cpp_path, str(settings.get("Device", "auto")))

    vad = _vad_model()
    if settings.get("VadFilter", True) and vad:
        threshold = float(settings.get("VadThreshold", 0.4))
        cmd += ["--vad", "--vad-model", str(vad), "--vad-threshold", str(threshold)]

    if self.language == "zh":
        cmd += ["--prompt", "你好，我们需要使用简体中文，以下是普通话的句子。"]
    return cmd


_original_make_segments = wc.WhisperCppASR._make_segments
LOOP_MIN_REPEATS = 4


def _make_segments(self, resp_data):
    """Collapse decoder loops: without VAD, long stretches of moaning or breathing make whisper.cpp
    repeat the previous line dozens of times. A run of LOOP_MIN_REPEATS+ identical consecutive
    lines keeps only its first line."""
    segments = _original_make_segments(self, resp_data)
    if getattr(self, "_gpu_settings", None) is None:
        return segments
    kept, i = [], 0
    while i < len(segments):
        j = i
        while j + 1 < len(segments) and segments[j + 1].text.strip() == segments[i].text.strip():
            j += 1
        kept.extend(segments[i:j + 1] if j - i + 1 < LOOP_MIN_REPEATS else segments[i:i + 1])
        i = j + 1
    if len(kept) < len(segments):
        logger.info("Dropped %d looped whisper.cpp lines", len(segments) - len(kept))
    return kept


_original_get_key = wc.WhisperCppASR._get_key


def _get_key(self):
    """The ASR cache key must change with the VAD settings, or a VAD run is served for a non-VAD one."""
    key = _original_get_key(self)
    settings = getattr(self, "_gpu_settings", None)
    if settings is None:
        return key
    vad = settings.get("VadFilter", True) and _vad_model() is not None
    return f"{key}-vulkan-vad{settings.get('VadThreshold', 0.4) if vad else 'off'}"


class _NoWindowSubprocess:
    """wc.subprocess stand-in: Popen without a console window (the original omits the flag)."""

    def __getattr__(self, name):
        return getattr(subprocess, name)

    @staticmethod
    def Popen(*args, **kwargs):
        kwargs.setdefault("creationflags", _NO_WINDOW)
        kwargs.setdefault("errors", "replace")
        return subprocess.Popen(*args, **kwargs)


class _LenientPath(Path):
    """wc.Path stand-in: whisper.cpp can split a multi-byte character across two segments,
    leaving invalid UTF-8 in the .srt that the original strict read_text crashes on."""

    def read_text(self, encoding=None, errors=None):
        return super().read_text(encoding, errors or "replace")


wc.WhisperCppASR.__init__ = _init
wc.WhisperCppASR._build_command = _build_command
wc.WhisperCppASR._get_key = _get_key
wc.WhisperCppASR._make_segments = _make_segments
wc.subprocess = _NoWindowSubprocess()
wc.Path = _LenientPath


def _add_download_entries():
    """large-v3 / large-v3-turbo / silero VAD for the 管理模型 dialog (the original list stops at large-v2)."""
    from videocaptioner.ui.components.WhisperCppSettingWidget import WHISPER_CPP_MODELS

    hf = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/"
    extra = [
        ("large-v3", "ggml-large-v3.bin", "3.1 GB", hf),
        ("large-v3-turbo", "ggml-large-v3-turbo.bin", "1.6 GB", hf),
        ("silero-vad", "ggml-silero-v5.1.2.bin", "885 KB", "https://huggingface.co/ggml-org/whisper-vad/resolve/main/"),
    ]
    known = {m["value"] for m in WHISPER_CPP_MODELS}
    for label, value, size, base in extra:
        if value not in known:
            WHISPER_CPP_MODELS.append({
                "label": label, "value": value, "size": size,
                "downloadLink": base + value, "mirrorLink": base + value, "sha": "",
            })


_add_download_entries()


@command("whisper_cpp_gpu_status")
def whisper_cpp_gpu_status(job, args):
    """Vulkan program, its devices, downloaded ggml models and VAD model for the WhisperCpp page."""
    program = _program(_settings())
    job.result({
        "program": str(program) if program else "",
        "devices": list_devices(program) if program else [],
        "models": whisper_models(),
        "has_vad_model": _vad_model() is not None,
    })
