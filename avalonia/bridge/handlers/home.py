"""Home page (task creation + transcription tabs): video download, media info, transcription."""
from vc_bridge import command, run_qthread, to_jsonable


@command("video_download")
def video_download(job, args):
    """TaskCreationInterface._process_url -> VideoDownloadThread(url, work_dir).

    args: {url, work_dir?}  (work_dir defaults to cfg.work_dir)
    progress: yt-dlp download progress; finished: downloaded video path ("" when not found).
    """
    from videocaptioner.ui.common.config import cfg
    from videocaptioner.ui.thread.video_download_thread import VideoDownloadThread

    work_dir = args.get("work_dir") or str(cfg.work_dir.value)
    thread = VideoDownloadThread(args["url"], work_dir)
    run_qthread(job, thread, finished=lambda path: path or "")


@command("video_info")
def video_info(job, args):
    """TranscriptionInterface.update_info -> VideoInfoThread(file_path).

    args: {file_path}
    finished: all VideoInfo fields (file_name, file_path, width, height, fps, duration_seconds,
              bitrate_kbps, video_codec, audio_codec, audio_sampling_rate, thumbnail_path,
              audio_streams[{index, codec, language, title}]).
    """
    from videocaptioner.ui.thread.video_info_thread import VideoInfoThread

    thread = VideoInfoThread(args["file_path"])
    run_qthread(job, thread, finished=lambda info: to_jsonable(info))


@command("transcribe")
def transcribe(job, args):
    """VideoInfoCard.start_transcription -> TaskFactory.create_transcribe_task + TranscriptThread.

    args: {file_path, need_next_task?, task_id?, selected_audio_track_index?}
    events: task (the created TranscribeTask, emitted before the thread starts)
    finished: the TranscribeTask (output_path, file_path, need_next_task, ...).
    """
    from videocaptioner.ui.task_factory import TaskFactory
    from videocaptioner.ui.thread.transcript_thread import TranscriptThread

    task = TaskFactory.create_transcribe_task(
        args["file_path"],
        need_next_task=bool(args.get("need_next_task", False)),
        task_id=args.get("task_id"),
    )
    # 将选中的音轨索引传递给 task
    task.selected_audio_track_index = int(args.get("selected_audio_track_index") or 0)
    job.emit("task", to_jsonable(task))
    run_qthread(job, TranscriptThread(task), finished=lambda t: to_jsonable(t))

