"""Regression coverage for importing subtitles and preserving their sources."""

import contextlib
import os
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

os.environ.setdefault("QT_QPA_PLATFORM", "offscreen")

from PyQt5.QtCore import QMimeData, QPointF, Qt, QUrl
from PyQt5.QtGui import QDropEvent
from PyQt5.QtWidgets import QApplication

from videocaptioner.core.asr.asr_data import ASRData, ASRDataSeg
from videocaptioner.core.entities import SubtitleConfig, SubtitleTask

with open(os.devnull, "w") as sink, contextlib.redirect_stdout(sink):
    from videocaptioner.ui.view.subtitle_interface import SubtitleInterface
    from videocaptioner.ui.thread.subtitle_thread import SubtitleThread


class SubtitleImportRegressionTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.app = QApplication.instance() or QApplication([])

    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        self.sample = ASRData([
            ASRDataSeg("This is the first subtitle sentence.", 1000, 2500),
            ASRDataSeg("This is another subtitle sentence.", 3000, 4500),
        ])
        self.widget = SubtitleInterface()
        self.addCleanup(self.widget.close)
        self.notifications = {}
        for level in ("success", "error", "warning", "info"):
            context = patch(f"videocaptioner.ui.view.subtitle_interface.InfoBar.{level}")
            self.notifications[level] = context.start()
            self.addCleanup(context.stop)

    def drop(self, path):
        mime = QMimeData()
        mime.setUrls([QUrl.fromLocalFile(str(path))])
        event = QDropEvent(QPointF(10, 10), Qt.CopyAction, mime, Qt.LeftButton, Qt.NoModifier)
        self.widget.dropEvent(event)

    def test_mislabeled_ass_import_keeps_all_srt_cues(self):
        path = self.root / "mislabeled.ass"
        self.sample.to_srt(save_path=str(path))
        data = ASRData.from_subtitle_file(str(path))
        self.assertEqual(data.to_json(), self.sample.to_json())

    def test_valid_ass_still_imports_normally(self):
        path = self.root / "valid.ass"
        self.sample.to_ass(save_path=str(path))
        self.assertEqual(ASRData.from_subtitle_file(str(path)).to_json(), self.sample.to_json())

    def test_dragged_mislabeled_ass_populates_the_table(self):
        path = self.root / "dragged.ass"
        self.sample.to_srt(save_path=str(path))
        self.drop(path)
        self.assertEqual(self.widget.model.rowCount(), 2)
        self.assertEqual(self.widget.model._data, self.sample.to_json())
        self.notifications["success"].assert_called_once()

    def test_empty_drag_does_not_clear_previously_loaded_subtitles(self):
        good = self.root / "good.srt"
        self.sample.to_srt(save_path=str(good))
        self.widget.load_subtitle_file(str(good))
        empty = self.root / "empty.ass"
        empty.write_text("[Script Info]\n[Events]\n", encoding="utf-8")
        self.drop(empty)
        self.assertEqual(self.widget.model._data, self.sample.to_json())
        self.assertEqual(self.widget.subtitle_path, str(good))
        self.notifications["success"].assert_not_called()
        self.assertTrue(self.notifications["warning"].called or self.notifications["error"].called)

    def test_file_picker_keeps_loaded_subtitles_when_new_file_has_no_cues(self):
        good = self.root / "good.srt"
        self.sample.to_srt(save_path=str(good))
        self.widget.load_subtitle_file(str(good))
        empty = self.root / "empty.ass"
        empty.write_text("[Script Info]\n[Events]\n", encoding="utf-8")
        with patch("videocaptioner.ui.view.subtitle_interface.QFileDialog.getOpenFileName",
                   return_value=(str(empty), "")):
            self.widget.on_file_select()
        self.assertEqual(self.widget.subtitle_path, str(good))
        self.assertEqual(self.widget.model.rowCount(), 2)

    def test_processing_uses_table_edits_and_preserves_original_ass_bytes(self):
        source = self.root / "original.ass"
        self.sample.to_ass(save_path=str(source))
        before = source.read_bytes()
        self.widget.load_subtitle_file(str(source))
        edited = "This edited subtitle must reach the processing thread."
        self.widget.model._data["1"]["original_subtitle"] = edited
        output = self.root / "processed.srt"
        task = SubtitleTask(subtitle_path=str(source), output_path=str(output),
                            subtitle_config=SubtitleConfig(need_split=False, need_optimize=False, need_translate=False))
        with patch("videocaptioner.ui.view.subtitle_interface.TaskFactory.create_subtitle_task", return_value=task), \
             patch.object(SubtitleThread, "start"):
            self.widget.start_subtitle_optimization()
        self.assertEqual(source.read_bytes(), before)
        worker = self.widget.subtitle_optimization_thread
        errors = []
        worker.error.connect(errors.append)
        worker.run()
        self.assertEqual(errors, [])
        self.assertIn(edited, output.read_text(encoding="utf-8"))
        self.assertEqual(source.read_bytes(), before)


if __name__ == "__main__":
    unittest.main()
