"""Launch the local desktop checkout with its preserved models and tools."""
import os
import sys
from pathlib import Path

root = Path(__file__).resolve().parent
os.chdir(root)
sys.path.insert(0, str(root))
runtime = Path(r"F:\dev\miniconda3\envs\videocaptioner")
tools = [
    root / "resource" / "bin",
    root / "resource" / "bin" / "Faster-Whisper-XXL",
    runtime,
    runtime / "Library" / "bin",
    runtime.parent.parent / "Library" / "bin",
]
os.environ["PATH"] = os.pathsep.join(str(path) for path in tools) + os.pathsep + os.environ.get("PATH", "")
logs = root / "AppData" / "logs"
logs.mkdir(parents=True, exist_ok=True)
log = (logs / "desktop-launch.log").open("a", encoding="utf-8", buffering=1)
sys.stdout = sys.stderr = log

from videocaptioner.ui.main import main

main()
