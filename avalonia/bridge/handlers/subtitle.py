"""Commands for the 字幕优化与翻译 page (videocaptioner/ui/view/subtitle_interface.py).

Commands:
  subtitle_load        {path}                      -> result {path, data}   ASRData.from_subtitle_file(path).to_json()
  subtitle_task        {file_path, video_path?, need_next_task?, task_id?}
                                                   -> result SubtitleTask (TaskFactory.create_subtitle_task)
  subtitle_run         {file_path, video_path?, need_next_task?, task_id?, data?, custom_prompt_text?}
                       Runs SubtitleThread. When `data` is given the table state is first written back to
                       file_path as SRT (start_subtitle_optimization(need_create_task=True)).
                       Events: task (SubtitleTask), progress, update {key: text}, update_all {key: seg},
                               finished {video_path, output_path}, error
  subtitle_retranslate {data, file_path}           RetranslateThread on the selected rows.
                       Events: progress, finished {key: translated_text}, error
  subtitle_save        {data, path}                -> result path   (on_save_format_clicked: .ass uses the
                                                      current style, others ASRData.save; reveals the file)
  subtitle_open_folder {path}                      -> result True   (platform_utils.open_folder)
"""
from vc_bridge import command, run_qthread, to_jsonable


def _create_task(args):
    from videocaptioner.ui.task_factory import TaskFactory

    return TaskFactory.create_subtitle_task(
        file_path=args["file_path"],
        video_path=args.get("video_path") or None,
        need_next_task=bool(args.get("need_next_task", False)),
        task_id=args.get("task_id") or None,
    )


@command("subtitle_load")
def subtitle_load(job, args):
    from videocaptioner.core.asr.asr_data import ASRData

    path = args["path"]
    asr_data = ASRData.from_subtitle_file(path)
    job.result({"path": path, "data": asr_data.to_json()})


@command("subtitle_task")
def subtitle_task(job, args):
    job.result(to_jsonable(_create_task(args)))


@command("subtitle_run")
def subtitle_run(job, args):
    from videocaptioner.core.asr.asr_data import ASRData
    from videocaptioner.ui.thread.subtitle_thread import SubtitleThread

    data = args.get("data")
    if data:
        # 将当前表格状态写回原文件，保留用户的合并/删除/编辑
        ASRData.from_json(data).to_srt(save_path=args["file_path"])
    task = _create_task(args)
    job.emit("task", to_jsonable(task))

    thread = SubtitleThread(task)
    thread.set_custom_prompt_text(args.get("custom_prompt_text", ""))
    run_qthread(
        job,
        thread,
        finished=lambda video_path, output_path: {"video_path": video_path, "output_path": output_path},
        extra_signals={"update": lambda d: d, "update_all": lambda d: d},
    )


@command("subtitle_retranslate")
def subtitle_retranslate(job, args):
    from pathlib import Path

    from videocaptioner.ui.task_factory import TaskFactory
    from videocaptioner.ui.thread.subtitle_thread import RetranslateThread

    file_path = args.get("file_path") or ""
    # 获取当前翻译配置
    config = TaskFactory.create_subtitle_task(file_path=file_path).subtitle_config
    if not config:
        job.result(None)
        return
    file_name = Path(file_path).name if file_path else ""
    thread = RetranslateThread(args["data"], config, file_name)
    run_qthread(job, thread, finished=lambda result: result)


@command("subtitle_save")
def subtitle_save(job, args):
    from videocaptioner.core.asr.asr_data import ASRData
    from videocaptioner.core.subtitle import get_subtitle_style
    from videocaptioner.core.utils.platform_utils import reveal_in_explorer
    from videocaptioner.ui.common.config import cfg

    file_path = args["path"]
    asr_data = ASRData.from_json(args["data"])
    layout = cfg.subtitle_layout.value
    if file_path.endswith(".ass"):
        style_str = get_subtitle_style(cfg.subtitle_style_name.value)
        asr_data.to_ass(style_str, layout, file_path)
    else:
        asr_data.save(file_path, layout=layout)
    if args.get("reveal", True):
        reveal_in_explorer(file_path)
    job.result(file_path)


@command("subtitle_open_folder")
def subtitle_open_folder(job, args):
    from videocaptioner.core.utils.platform_utils import open_folder

    open_folder(args["path"])
    job.result(True)
