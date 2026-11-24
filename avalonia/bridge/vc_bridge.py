"""VideoCaptioner backend bridge for the Avalonia frontend.

Runs the original videocaptioner worker threads (QThread based) inside a
headless Qt event loop and talks to the frontend over stdio using JSON lines.

Request  (stdin):  {"id": "<job id>", "cmd": "<command>", "args": {...}}
Events   (stdout): {"id": "<job id>", "event": "<name>", "data": ...}

Standard events:
  result    one-shot reply of a request/response command (job ends)
  progress  {"value": int, "message": str}
  finished  job completed successfully (job ends), data is command specific
  error     {"message": str} (job ends)
  cancelled job was cancelled (job ends)
  <custom>  command specific streaming events

Special command: {"cmd": "cancel", "args": {"target": "<job id>"}}
Unsolicited events use id "" (e.g. "ready", "log").
"""

import os
import sys

# Keep the protocol channel clean: everything printed by libraries goes to stderr.
_PROTO_OUT = os.fdopen(os.dup(sys.stdout.fileno()), "w", encoding="utf-8", newline="\n")
sys.stdout = sys.stderr
# Same for the request channel: child processes (ffmpeg reads keys from a piped stdin on Windows)
# must not inherit it, or they swallow requests. fd 0 becomes NUL, requests come from _PROTO_IN.
_PROTO_IN = os.fdopen(os.dup(sys.stdin.fileno()), "rb")
os.dup2(os.open(os.devnull, os.O_RDONLY), 0)
os.environ.setdefault("QT_QPA_PLATFORM", "offscreen")

import dataclasses  # noqa: E402
import datetime  # noqa: E402
import enum  # noqa: E402
import importlib  # noqa: E402
import json  # noqa: E402
import pkgutil  # noqa: E402
import threading  # noqa: E402
import traceback  # noqa: E402
from pathlib import Path  # noqa: E402
from typing import Any, Callable, Dict, Optional  # noqa: E402

from PyQt5.QtCore import QObject, Qt, pyqtSignal  # noqa: E402
from PyQt5.QtWidgets import QApplication  # noqa: E402

_write_lock = threading.Lock()


def to_jsonable(obj: Any) -> Any:
    """Convert dataclasses / enums / paths / datetimes into JSON friendly values."""
    if obj is None or isinstance(obj, (str, int, float, bool)):
        return obj
    if isinstance(obj, enum.Enum):
        return to_jsonable(obj.value)
    if isinstance(obj, (datetime.datetime, datetime.date)):
        return obj.isoformat()
    if isinstance(obj, Path):
        return str(obj)
    if dataclasses.is_dataclass(obj) and not isinstance(obj, type):
        return {f.name: to_jsonable(getattr(obj, f.name)) for f in dataclasses.fields(obj)}
    if isinstance(obj, dict):
        return {str(k): to_jsonable(v) for k, v in obj.items()}
    if isinstance(obj, (list, tuple, set)):
        return [to_jsonable(v) for v in obj]
    if hasattr(obj, "__dict__"):
        return {k: to_jsonable(v) for k, v in vars(obj).items() if not k.startswith("_")}
    return str(obj)


def send(job_id: str, event: str, data: Any = None) -> None:
    line = json.dumps({"id": job_id, "event": event, "data": to_jsonable(data)}, ensure_ascii=False)
    with _write_lock:
        _PROTO_OUT.write(line + "\n")
        _PROTO_OUT.flush()


class Job:
    """Context handed to command handlers."""

    def __init__(self, job_id: str, cmd: str, args: dict):
        self.id = job_id
        self.cmd = cmd
        self.args = args
        self.done = False
        self._cancel_handlers = []
        self._refs = []  # keep QThreads etc. alive for the job lifetime

    # -- events -------------------------------------------------------------
    def emit(self, event: str, data: Any = None) -> None:
        if not self.done:
            send(self.id, event, data)

    def progress(self, value: int, message: str = "") -> None:
        self.emit("progress", {"value": int(value), "message": message})

    def result(self, data: Any = None) -> None:
        self.emit("result", data)
        self._end()

    def finished(self, data: Any = None) -> None:
        self.emit("finished", data)
        self._end()

    def error(self, message: str) -> None:
        self.emit("error", {"message": str(message)})
        self._end()

    # -- lifecycle ----------------------------------------------------------
    def keep(self, obj: Any) -> Any:
        self._refs.append(obj)
        return obj

    def on_cancel(self, fn: Callable[[], None]) -> None:
        self._cancel_handlers.append(fn)

    def cancel(self) -> None:
        for fn in self._cancel_handlers:
            try:
                fn()
            except Exception:
                traceback.print_exc()
        if not self.done:
            self.emit("cancelled")
            self._end()

    def _end(self) -> None:
        self.done = True
        JOBS.pop(self.id, None)
        # QThreads that are still winding down must outlive the job object.
        for r in self._refs:
            if hasattr(r, "isRunning") and r.isRunning():
                _ORPHANS.append(r)
        self._refs = []


COMMANDS: Dict[str, Callable[[Job, dict], None]] = {}
JOBS: Dict[str, Job] = {}
_ORPHANS: list = []


def command(name: str):
    """Decorator registering a handler: fn(job, args). Handlers run on the Qt main thread."""

    def deco(fn):
        COMMANDS[name] = fn
        return fn

    return deco


def reload_config() -> None:
    """Re-read settings.json written by the frontend into the shared cfg object."""
    from qfluentwidgets import qconfig

    from videocaptioner.config import SETTINGS_PATH
    from videocaptioner.ui.common.config import cfg

    qconfig.load(SETTINGS_PATH, cfg)


def run_qthread(job: Job, thread, finished: Optional[Callable[..., Any]] = None,
                extra_signals: Optional[Dict[str, Optional[Callable[..., Any]]]] = None,
                stop: Optional[Callable[[], None]] = None) -> None:
    """Wire a videocaptioner QThread worker to a job.

    finished: maps the thread's `finished` signal args to the payload of the job's finished event.
    extra_signals: {"signal_name": mapper} -> emitted as custom events named after the signal
                   (mapper None = list of raw args).
    """
    job.keep(thread)
    if hasattr(thread, "progress"):
        thread.progress.connect(lambda v, m="": job.progress(v, m))
    if hasattr(thread, "error"):
        thread.error.connect(lambda msg: job.error(msg))
    for sig_name, mapper in (extra_signals or {}).items():
        getattr(thread, sig_name).connect(
            lambda *a, _n=sig_name, _m=mapper: job.emit(_n, _m(*a) if _m else list(a))
        )
    thread.finished.connect(lambda *a: job.finished(finished(*a) if finished else list(a)))

    def _stop():
        if stop:
            stop()
        elif hasattr(thread, "stop"):
            thread.stop()
        else:
            thread.terminate()

    job.on_cancel(_stop)
    thread.start()


class _Dispatcher(QObject):
    request = pyqtSignal(str)

    def __init__(self):
        super().__init__()
        self.request.connect(self._handle, Qt.QueuedConnection)

    def _handle(self, line: str) -> None:
        if line == "__quit__":
            QApplication.instance().quit()
            return
        try:
            msg = json.loads(line)
        except json.JSONDecodeError as e:
            send("", "log", {"level": "error", "message": f"bad request: {e}"})
            return
        job_id = str(msg.get("id", ""))
        cmd = msg.get("cmd", "")
        args = msg.get("args") or {}

        if cmd == "cancel":
            target = JOBS.get(str(args.get("target", "")))
            if target:
                target.cancel()
            return

        job = Job(job_id, cmd, args)
        handler = COMMANDS.get(cmd)
        if handler is None:
            job.error(f"unknown command: {cmd}")
            return
        JOBS[job_id] = job
        try:
            reload_config()
            handler(job, args)
        except Exception as e:
            traceback.print_exc()
            job.error(f"{type(e).__name__}: {e}")


def _stdin_loop(dispatcher: _Dispatcher, app: QApplication) -> None:
    for raw in _PROTO_IN:
        line = raw.decode("utf-8", errors="replace").strip()
        if line:
            dispatcher.request.emit(line)
    # Frontend closed the pipe: exit (quit must run on the Qt thread).
    dispatcher.request.emit("__quit__")


def _load_handlers() -> None:
    here = Path(__file__).parent
    sys.path.insert(0, str(here))
    sys.modules.setdefault("vc_bridge", sys.modules[__name__])

    for mod in pkgutil.iter_modules([str(here / "handlers")]):
        try:
            importlib.import_module(f"handlers.{mod.name}")
        except Exception:
            traceback.print_exc()
            send("", "log", {"level": "error", "message": f"failed to load handler module {mod.name}"})


def main() -> None:
    app = QApplication(sys.argv[:1])
    from videocaptioner.core.utils.cache import disable_cache, enable_cache
    from videocaptioner.core.utils.logger import setup_logger
    from videocaptioner.ui.common.config import cfg

    setup_logger("VideoCaptioner")
    enable_cache() if cfg.get(cfg.cache_enabled) else disable_cache()

    _load_handlers()
    dispatcher = _Dispatcher()
    threading.Thread(target=_stdin_loop, args=(dispatcher, app), daemon=True).start()
    send("", "ready", {"commands": sorted(COMMANDS)})
    code = app.exec_()
    # Kill child processes (ffmpeg / whisper) like MainWindow.stop does.
    try:
        import psutil

        for child in psutil.Process(os.getpid()).children(recursive=True):
            child.kill()
    except Exception:
        pass
    os._exit(code)


if __name__ == "__main__":
    main()
