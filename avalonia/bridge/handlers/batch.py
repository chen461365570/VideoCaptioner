"""Batch processing (BatchProcessInterface): wraps ui/thread/batch_process_thread.BatchProcessThread.

batch_session           long-lived job owning one BatchProcessThread (like the interface's self.batch_thread).
                        Events: task_progress {file_path, progress, status}
                                task_error    {file_path, error}
                                task_completed {file_path}
                        Cancelling the job calls stop_all().
batch_add      {session, file_path, task_type}  -> batch_thread.add_task(BatchTask(file_path, BatchTaskType(task_type)))
batch_stop     {session, file_path}             -> batch_thread.stop_task(file_path)
batch_stop_all {session}                        -> batch_thread.stop_all()
"""
from vc_bridge import command

_SESSIONS = {}


def _thread(job, args):
    session = _SESSIONS.get(str(args.get("session", "")))
    if session is None:
        job.error(f"unknown batch session: {args.get('session')}")
    return session


@command("batch_session")
def batch_session(job, args):
    from videocaptioner.ui.thread.batch_process_thread import BatchProcessThread

    thread = BatchProcessThread()
    thread.task_progress.connect(
        lambda path, progress, status: job.emit("task_progress", {
            "file_path": path, "progress": progress, "status": status}))
    thread.task_error.connect(lambda path, error: job.emit("task_error", {"file_path": path, "error": error}))
    thread.task_completed.connect(lambda path: job.emit("task_completed", {"file_path": path}))
    job.keep(thread)
    _SESSIONS[job.id] = thread

    def _stop():
        thread.stop_all()
        _SESSIONS.pop(job.id, None)

    job.on_cancel(_stop)
    job.emit("ready", None)


@command("batch_add")
def batch_add(job, args):
    from videocaptioner.core.entities import BatchTaskType
    from videocaptioner.ui.thread.batch_process_thread import BatchTask

    thread = _thread(job, args)
    if thread is None:
        return
    thread.add_task(BatchTask(args["file_path"], BatchTaskType(args["task_type"])))
    job.result(True)


@command("batch_stop")
def batch_stop(job, args):
    thread = _thread(job, args)
    if thread is None:
        return
    thread.stop_task(args["file_path"])
    job.result(True)


@command("batch_stop_all")
def batch_stop_all(job, args):
    thread = _thread(job, args)
    if thread is None:
        return
    thread.stop_all()
    job.result(True)
