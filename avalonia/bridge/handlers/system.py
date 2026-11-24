"""App-wide commands: paths, config reload, ffmpeg check, version check."""
import shutil

from vc_bridge import command


@command("ping")
def ping(job, args):
    job.result("pong")


@command("paths")
def paths(job, args):
    from videocaptioner import config as C

    names = ["VERSION", "APP_NAME", "ROOT_PATH", "RESOURCE_PATH", "ASSETS_PATH", "APPDATA_PATH",
             "WORK_PATH", "BIN_PATH", "SUBTITLE_STYLE_PATH", "FONTS_PATH", "LOG_PATH", "LLM_LOG_FILE",
             "SETTINGS_PATH", "CACHE_PATH", "MODEL_PATH", "FASTER_WHISPER_PATH",
             "GITHUB_REPO_URL", "RELEASE_URL", "FEEDBACK_URL", "HELP_URL", "YEAR", "AUTHOR"]
    job.result({n: str(getattr(C, n)) for n in names if hasattr(C, n)})


@command("reload_config")
def reload_config_cmd(job, args):
    # The dispatcher already reloaded cfg; re-apply the cache toggle like ui/main.py does.
    from videocaptioner.core.utils.cache import disable_cache, enable_cache
    from videocaptioner.ui.common.config import cfg

    enable_cache() if cfg.get(cfg.cache_enabled) else disable_cache()
    job.result(True)


@command("check_ffmpeg")
def check_ffmpeg(job, args):
    job.result(shutil.which("ffmpeg") is not None)


@command("version_check")
def version_check(job, args):
    """Events: new_version {version, update_required, update_info, download_url}, announcement {content}."""
    from PyQt5.QtCore import QThread

    from videocaptioner.ui.thread.version_checker_thread import VersionChecker

    checker = VersionChecker()
    thread = QThread()
    checker.moveToThread(thread)
    checker.newVersionAvailable.connect(
        lambda v, req, info, url: job.emit("new_version", {
            "version": v, "update_required": req, "update_info": info, "download_url": url}))
    checker.announcementAvailable.connect(lambda c: job.emit("announcement", {"content": c}))
    checker.checkCompleted.connect(lambda: (job.finished(None), thread.quit()))
    thread.started.connect(checker.perform_check)
    job.keep(checker)
    job.keep(thread)
    thread.start()
