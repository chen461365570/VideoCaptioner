"""Settings page + transcription engine setting widgets.

Ports the worker threads of:
  ui/view/setting_interface.py         LLMConnectionThread, WhisperConnectionThread
  ui/components/WhisperAPISettingWidget.py
  ui/components/WhisperCppSettingWidget.py   (WHISPER_CPP_MODELS, FileDownloadThread)
  ui/components/FasterWhisperSettingWidget.py (FASTER_WHISPER_*, FileDownloadThread,
                                              ModelscopeDownloadThread, UnzipThread)
"""
import os
import threading
from pathlib import Path

from vc_bridge import command, run_qthread


def _in_thread(job, fn):
    """Run fn() in a Python thread; its return value becomes the finished payload."""

    def _run():
        try:
            data = fn()
        except Exception as e:  # same as QThread error.emit(str(e))
            job.error(str(e))
            return
        job.finished(data)

    threading.Thread(target=_run, daemon=True).start()


# --------------------------------------------------------------------------- LLM / Whisper API
@command("settings_check_llm")
def check_llm(job, args):
    """LLMConnectionThread: finished {success, message, models}."""
    from videocaptioner.core.llm.check_llm import check_llm_connection, get_available_models

    api_base = args.get("api_base", "")
    api_key = args.get("api_key", "")
    model = args.get("model", "")

    def _work():
        is_success, message = check_llm_connection(api_base, api_key, model)
        models = get_available_models(api_base, api_key)
        return {"success": bool(is_success), "message": message or "", "models": list(models or [])}

    _in_thread(job, _work)


@command("settings_check_whisper")
def check_whisper(job, args):
    """WhisperConnectionThread: finished {success, result}."""
    from videocaptioner.core.llm import check_whisper_connection

    base_url = args.get("base_url", "")
    api_key = args.get("api_key", "")
    model = args.get("model", "")

    def _work():
        success, result = check_whisper_connection(base_url, api_key, model)
        return {"success": bool(success), "result": result or ""}

    _in_thread(job, _work)


@command("settings_set_cache")
def set_cache(job, args):
    """SettingInterface.__onCacheEnabledChanged: enable_cache() / disable_cache()."""
    from videocaptioner.core.utils.cache import disable_cache, enable_cache

    enable_cache() if args.get("enabled", True) else disable_cache()
    job.result(True)


# --------------------------------------------------------------------------- WhisperCpp
@command("whisper_cpp_models")
def whisper_cpp_models(job, args):
    """WHISPER_CPP_MODELS with a `downloaded` flag (MODEL_PATH / value exists)."""
    from videocaptioner.config import MODEL_PATH
    from videocaptioner.ui.components.WhisperCppSettingWidget import (
        WHISPER_CPP_MODELS,
        check_whisper_cpp_exists,
    )

    has_program, versions = check_whisper_cpp_exists()
    models = []
    for m in WHISPER_CPP_MODELS:
        d = dict(m)
        d["downloaded"] = (Path(MODEL_PATH) / m["value"]).exists()
        models.append(d)
    job.result({
        "has_program": has_program,
        "installed_versions": list(versions),
        "models": models,
        "model_path": str(MODEL_PATH),
    })


@command("whisper_cpp_download_model")
def whisper_cpp_download_model(job, args):
    """WhisperCppDownloadDialog._download_model(row): FileDownloadThread(mirrorLink, MODEL_PATH/value)."""
    from videocaptioner.config import MODEL_PATH
    from videocaptioner.ui.components.WhisperCppSettingWidget import WHISPER_CPP_MODELS
    from videocaptioner.ui.thread.file_download_thread import FileDownloadThread

    model = WHISPER_CPP_MODELS[int(args["row"])]
    thread = FileDownloadThread(model["mirrorLink"], os.path.join(MODEL_PATH, model["value"]))
    run_qthread(job, thread)


# --------------------------------------------------------------------------- FasterWhisper
def _faster_whisper_status():
    from videocaptioner.config import BIN_PATH, MODEL_PATH
    from videocaptioner.ui.components.FasterWhisperSettingWidget import (
        FASTER_WHISPER_MODELS,
        FASTER_WHISPER_PROGRAMS,
        check_faster_whisper_exists,
    )

    has_program, versions = check_faster_whisper_exists()
    models = []
    for m in FASTER_WHISPER_MODELS:
        d = dict(m)
        model_path = Path(MODEL_PATH) / m["value"]
        d["downloaded"] = (model_path / "model.bin").exists()
        d["dir_exists"] = model_path.exists()
        models.append(d)
    return {
        "has_program": has_program,
        "installed_versions": list(versions),
        "programs": [dict(p) for p in FASTER_WHISPER_PROGRAMS],
        "models": models,
        "model_path": str(MODEL_PATH),
        "bin_path": str(BIN_PATH),
    }


@command("faster_whisper_status")
def faster_whisper_status(job, args):
    job.result(_faster_whisper_status())


@command("faster_whisper_download_program")
def faster_whisper_download_program(job, args):
    """FasterWhisperDownloadDialog._start_download + _on_program_download_finished.

    Events: progress while downloading; "unzipping" before the GPU 7z is extracted;
    "download_error" / "install_error" {message} right before the job's error (so the UI can pick
    the InfoBar title 下载失败 / 安装失败). finished: installation done.
    """
    from videocaptioner.config import BIN_PATH
    from videocaptioner.ui.components.FasterWhisperSettingWidget import (
        FASTER_WHISPER_PROGRAMS,
        UnzipThread,
    )
    from videocaptioner.ui.thread.file_download_thread import FileDownloadThread

    label = args.get("label", "")
    program = next((p for p in FASTER_WHISPER_PROGRAMS if p["label"] == label), None)
    if not program:
        job.error("未找到对应的程序配置")
        return

    os.makedirs(BIN_PATH, exist_ok=True)
    save_path = os.path.join(BIN_PATH, program["value"])
    thread = job.keep(FileDownloadThread(program["downloadLink"], save_path))
    thread.progress.connect(lambda v, m="": job.progress(v, m))
    thread.error.connect(lambda msg: job.emit("download_error", {"message": msg}) or job.error(msg))

    def _on_downloaded():
        try:
            if save_path.endswith(".exe"):
                os.rename(save_path, os.path.join(BIN_PATH, "faster-whisper.exe"))
                job.finished(None)
            else:
                job.emit("unzipping", None)
                unzip = job.keep(UnzipThread(save_path, BIN_PATH))
                unzip.finished.connect(lambda: job.finished(None))
                unzip.error.connect(lambda msg: job.emit("install_error", {"message": msg}) or job.error(msg))
                unzip.start()
        except Exception as e:
            job.emit("install_error", {"message": str(e)})
            job.error(str(e))

    thread.finished.connect(_on_downloaded)
    job.on_cancel(thread.stop)
    thread.start()


@command("faster_whisper_download_model")
def faster_whisper_download_model(job, args):
    """FasterWhisperDownloadDialog._download_model(row): ModelscopeDownloadThread(modelScopeLink, MODEL_PATH/value).

    Cancel terminates the thread like _on_dialog_reject does.
    """
    from videocaptioner.config import MODEL_PATH
    from videocaptioner.ui.components.FasterWhisperSettingWidget import FASTER_WHISPER_MODELS
    from videocaptioner.ui.thread.modelscope_download_thread import ModelscopeDownloadThread

    model = FASTER_WHISPER_MODELS[int(args["row"])]
    thread = ModelscopeDownloadThread(model["modelScopeLink"], os.path.join(MODEL_PATH, model["value"]))
    # QThread.finished also fires after an error; the job has ended by then so it is ignored.
    run_qthread(job, thread, finished=lambda *a: None, stop=thread.terminate)


# --------------------------------------------------------------------------- generic download (tests / reuse)
@command("file_download")
def file_download(job, args):
    """FileDownloadThread(url, save_path) with progress / cancel."""
    from videocaptioner.ui.thread.file_download_thread import FileDownloadThread

    run_qthread(job, FileDownloadThread(args["url"], args["save_path"]))
