"""FasterWhisper compute type override (Avalonia addition).

The original backend hardcodes `--compute_type float16` on RTX 50 cards because the
cuBLAS bundled with Faster-Whisper-XXL could not run int8 on Blackwell. With a newer
cuBLAS in place int8 works, so the frontend exposes FasterWhisper.ComputeType
(auto | int8 | int8_float16 | float16 | float32) and this module applies it to the
command line of every FasterWhisper run (single and batch), without touching the
installed videocaptioner package.
"""
import json

from videocaptioner.config import SETTINGS_PATH
from videocaptioner.core.asr import faster_whisper as fw

_ALLOWED = {"int8", "int8_float16", "float16", "float32"}


def _configured_compute_type() -> str:
    try:
        data = json.loads(SETTINGS_PATH.read_text(encoding="utf-8"))
        value = str(data.get("FasterWhisper", {}).get("ComputeType", "auto"))
    except (OSError, ValueError):
        return "auto"
    return value if value in _ALLOWED else "auto"


_original_build_command = fw.FasterWhisperASR._build_command


def _build_command(self, audio_input):
    cmd = _original_build_command(self, audio_input)
    compute_type = _configured_compute_type()
    if compute_type == "auto" or getattr(self, "device", "") != "cuda":
        return cmd
    if "--compute_type" in cmd:
        i = cmd.index("--compute_type")
        del cmd[i:i + 2]
    cmd.extend(["--compute_type", compute_type])
    return cmd


fw.FasterWhisperASR._build_command = _build_command
