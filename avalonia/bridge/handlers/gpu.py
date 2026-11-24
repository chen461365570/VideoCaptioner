"""Unified GPU list for the transcription page (Avalonia addition).

A local transcription engine is tied to a GPU vendor: FasterWhisper (CTranslate2) only runs on
NVIDIA CUDA, the Vulkan whisper-cli (handlers/whisper_cpp_gpu.py) runs on any Vulkan GPU. The
frontend shows one "显卡" menu; each entry carries the engine and device setting to apply:

  {"name": "NVIDIA GeForce RTX 5070 Ti", "engine": "FasterWhisper ✨", "device": "cuda"}
  {"name": "AMD Radeon RX 6800 XT",      "engine": "WhisperCpp",       "device": "AMD Radeon RX 6800 XT"}

The first NVIDIA GPU goes to FasterWhisper (FasterWhisper.Device only knows "cuda"); every other
GPU goes to WhisperCpp, with its Vulkan device name stored in WhisperCpp.Device.
"""
import os
import subprocess
import sys

from vc_bridge import command

from handlers import whisper_cpp_gpu

_NO_WINDOW = subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0
FASTER_WHISPER = "FasterWhisper ✨"
WHISPER_CPP = "WhisperCpp"


def _nvidia_names() -> list:
    try:
        proc = subprocess.run(
            ["nvidia-smi", "--query-gpu=name", "--format=csv,noheader"],
            capture_output=True, text=True, timeout=15, creationflags=_NO_WINDOW,
        )
    except (OSError, subprocess.SubprocessError):
        return []
    return [line.strip() for line in proc.stdout.splitlines() if line.strip()] if proc.returncode == 0 else []


def gpu_entries(vulkan_names: list, nvidia_names: list, faster_whisper_available: bool) -> list:
    entries = []
    cuda_taken = not faster_whisper_available
    for name in vulkan_names:
        if not cuda_taken and name.upper().startswith("NVIDIA"):
            cuda_taken = True
            entries.append({"name": name, "engine": FASTER_WHISPER, "device": "cuda"})
        else:
            entries.append({"name": name, "engine": WHISPER_CPP, "device": name})
    if not cuda_taken and nvidia_names:
        # no Vulkan whisper-cli installed: CUDA is still usable
        entries.insert(0, {"name": nvidia_names[0], "engine": FASTER_WHISPER, "device": "cuda"})
    return entries


@command("gpu_list")
def gpu_list(job, args):
    program = whisper_cpp_gpu._program(whisper_cpp_gpu._settings())
    vulkan = whisper_cpp_gpu.list_devices(program) if program else []
    job.result({"gpus": gpu_entries(vulkan, _nvidia_names(), sys.platform != "darwin")})
