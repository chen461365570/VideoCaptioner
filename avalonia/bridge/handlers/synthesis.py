"""字幕视频合成 (VideoSynthesisInterface) commands."""
from vc_bridge import command, to_jsonable


@command("synthesis")
def synthesis(job, args):
    """Run VideoSynthesisThread on a task built by TaskFactory.create_synthesis_task.

    args: video_path, subtitle_path, need_next_task (bool), task_id (str|None)
    events: task {SynthesisTask} (right after creation, so the UI knows output_path),
            progress, finished {SynthesisTask}, error
    """
    from PyQt5.QtCore import QTimer

    from videocaptioner.ui.task_factory import TaskFactory
    from videocaptioner.ui.thread.video_synthesis_thread import VideoSynthesisThread

    task = TaskFactory.create_synthesis_task(
        args["video_path"],
        args["subtitle_path"],
        need_next_task=bool(args.get("need_next_task", False)),
        task_id=args.get("task_id") or None,
    )
    job.emit("task", to_jsonable(task))

    thread = VideoSynthesisThread(task)
    # The thread emits error(msg) followed by progress(100, "视频合成失败").
    # Delay the (job-ending) error so the final progress message still reaches the UI.
    thread.error.connect(lambda msg: QTimer.singleShot(100, lambda: job.error(msg)))
    job.keep(thread)
    thread.progress.connect(lambda v, m="": job.progress(v, m))
    thread.finished.connect(lambda t: job.finished(to_jsonable(t)))
    # VideoSynthesisThread has no stop(); ffmpeg children are killed when the backend exits.
    job.on_cancel(thread.terminate)
    thread.start()
