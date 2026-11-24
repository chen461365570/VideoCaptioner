"""Task-id helper shared by the home pipeline (HomeInterface)."""
from vc_bridge import command


@command("new_task_id")
def new_task_id(job, args):
    from videocaptioner.core.llm.context import generate_task_id

    job.result(generate_task_id())
