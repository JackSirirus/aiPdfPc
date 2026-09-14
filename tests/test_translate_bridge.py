# SPDX-License-Identifier: AGPL-3.0-only
# Copyright (C) 2026 AIPDF contributors
"""翻译桥接传输层：原子输出。仅用合成数据。

NDJSON 分块协议（`write_ndjson_chunk` / `read_ndjson_chunks`）从未被 engine 接入，
已按 G 决策移除；本文件只保留确实在用的原子写/替换测试。
"""
import sys
import tempfile
import unittest
import unittest.mock
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "backend"))
from translate import bridge


class TempDirTestCase(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.temp = tempfile.TemporaryDirectory(prefix="aipdf-bridge-", dir=str(ROOT / "tmp"))
        cls.root = Path(cls.temp.name)

    @classmethod
    def tearDownClass(cls):
        cls.temp.cleanup()

    def leftovers(self):
        return sorted(p.name for p in self.root.glob(bridge.TEMP_PREFIX + "*"))


class AtomicWriteTests(TempDirTestCase):
    def test_atomic_write_bytes_creates_final_file(self):
        target = self.root / "out.bin"
        result = bridge.atomic_write(target, b"\x00\x01payload")
        self.assertEqual(result, target)
        self.assertEqual(target.read_bytes(), b"\x00\x01payload")

    def test_atomic_write_text_is_utf8_and_leaves_no_temp(self):
        target = self.root / "out.txt"
        bridge.atomic_write(target, "中文内容")
        self.assertEqual(target.read_text(encoding="utf-8"), "中文内容")
        self.assertEqual(self.leftovers(), [])

    def test_atomic_replace_overwrites_existing(self):
        target = self.root / "replace.txt"
        target.write_text("old", encoding="utf-8")
        temp = self.root / (bridge.TEMP_PREFIX + "abc")
        temp.write_text("new", encoding="utf-8")

        bridge.atomic_replace(temp, target)
        self.assertEqual(target.read_text(encoding="utf-8"), "new")
        self.assertFalse(temp.exists())

    def test_exception_before_replace_keeps_original_and_cleans_temp(self):
        target = self.root / "crash.txt"
        target.write_text("old", encoding="utf-8")
        with unittest.mock.patch("os.replace", side_effect=RuntimeError("crash")):
            with self.assertRaises(RuntimeError):
                bridge.atomic_write(target, "new")
        self.assertEqual(target.read_text(encoding="utf-8"), "old")
        self.assertEqual(self.leftovers(), [])

    def test_permission_error_surfaces_target_in_use_error_code(self):
        # 局部导入：非 venv 环境（无 PyMuPDF）不应在测试收集阶段就失败。
        import engine

        target = self.root / "locked.txt"
        target.write_text("old", encoding="utf-8")
        with unittest.mock.patch("os.replace", side_effect=PermissionError("locked")):
            with self.assertRaises(engine.TargetInUseError) as caught:
                bridge.atomic_write(target, "new")

        self.assertEqual(caught.exception.code, "target_in_use")
        self.assertEqual(target.read_text(encoding="utf-8"), "old")
        self.assertEqual(self.leftovers(), [])
        # 宿主据此 errorCode 走「另存为」回退，而非盲目重试。
        failure = engine._failure_result(caught.exception)
        self.assertFalse(failure["ok"])
        self.assertEqual(failure["errorCode"], "target_in_use")


if __name__ == "__main__":
    unittest.main()
