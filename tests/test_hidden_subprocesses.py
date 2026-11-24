"""Windows desktop processes should not create transient console windows."""

import inspect
import subprocess
import unittest
from types import SimpleNamespace
from unittest.mock import patch

from videocaptioner.core.utils import subprocess_helper


class HiddenSubprocessTests(unittest.TestCase):
    def setUp(self):
        self.signature = inspect.signature(subprocess.Popen)
        self.calls = []
        # The unpatched application has no desktop policy, so exercise that
        # baseline as-is and fail on the missing Windows creation flag.
        self.activate_policy = getattr(subprocess_helper, "enable_hidden_subprocesses", lambda: None)
        calls, signature = self.calls, self.signature

        class RecordingPopen:
            __signature__ = signature

            def __init__(self, *args, **kwargs):
                bound = signature.bind(*args, **kwargs)
                bound.apply_defaults()
                calls.append(bound.arguments)

        self.recording_popen = RecordingPopen
        for context in (
            patch.object(subprocess, "Popen", RecordingPopen),
            patch.object(subprocess, "CREATE_NO_WINDOW", 0x08000000, create=True),
            patch.object(subprocess_helper, "sys", SimpleNamespace(platform="win32"), create=True),
        ):
            context.start()
            self.addCleanup(context.stop)

    def test_third_party_popen_alias_inherits_hidden_default(self):
        self.activate_policy()
        from subprocess import Popen as library_popen

        library_popen(["nvidia-smi", "--query-gpu=name"])
        self.assertTrue(self.calls[-1]["creationflags"] & subprocess.CREATE_NO_WINDOW)
        self.assertIsInstance(library_popen(["ffmpeg", "-version"]), self.recording_popen)

    def test_preserves_existing_flags_and_process_options(self):
        self.activate_policy()
        env = {"TEST_ENV": "kept"}
        startupinfo = object()
        subprocess.Popen(
            ["ffmpeg", "-version"],
            creationflags=0x200,
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            text=True,
            cwd="working-directory",
            env=env,
            startupinfo=startupinfo,
        )
        options = self.calls[-1]
        self.assertEqual(options["creationflags"], 0x200 | subprocess.CREATE_NO_WINDOW)
        self.assertEqual(options["stdout"], subprocess.PIPE)
        self.assertEqual(options["stderr"], subprocess.STDOUT)
        self.assertTrue(options["text"])
        self.assertEqual(options["cwd"], "working-directory")
        self.assertIs(options["env"], env)
        self.assertIs(options["startupinfo"], startupinfo)

    def test_supports_positional_creationflags(self):
        self.activate_policy()
        arguments = self.signature.bind(["ffprobe"], creationflags=0x200)
        arguments.apply_defaults()
        subprocess.Popen(*arguments.args, **arguments.kwargs)
        self.assertEqual(self.calls[-1]["creationflags"], 0x200 | subprocess.CREATE_NO_WINDOW)

    def test_repeated_installation_keeps_same_process_class(self):
        self.activate_policy()
        installed = subprocess.Popen
        self.activate_policy()
        self.assertIs(subprocess.Popen, installed)

    def test_non_windows_keeps_original_process_class(self):
        subprocess_helper.sys.platform = "linux"
        self.activate_policy()
        self.assertIs(subprocess.Popen, self.recording_popen)


if __name__ == "__main__":
    unittest.main()
