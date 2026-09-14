# SPDX-License-Identifier: AGPL-3.0-only
# Copyright (C) 2026 AIPDF contributors
"""翻译桥接传输层：NDJSON 分块、oversize 上限与原子输出。仅用合成数据。"""
import io
import json
import sys
import tempfile
import unittest
import unittest.mock
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "backend"))
from translate import bridge


def final_result(**extra):
    """引擎最终单行结果的最小形状（含 "ok"，宿主向后扫描的锚点）。"""
    return {"ok": True, "outputs": [], **extra}


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


class NdjsonRoundTripTests(TempDirTestCase):
    def test_text_stream_round_trip(self):
        stream = io.StringIO()
        chunks = [{"chunk_id": 0, "page": 0, "text": "第一段"},
                  {"chunk_id": 1, "page": 0, "text": "second"},
                  {"chunk_id": 2, "page": 1, "text": "第三段"}]
        for chunk in chunks:
            bridge.write_ndjson_chunk(stream, chunk)
        stream.seek(0)
        self.assertEqual(list(bridge.read_ndjson_chunks(stream)), chunks)

    def test_binary_stream_round_trip(self):
        stream = io.BytesIO()
        chunks = [{"chunk_id": 0, "text": "中文"}, {"chunk_id": 1, "text": "ascii"}]
        for chunk in chunks:
            bridge.write_ndjson_chunk(stream, chunk)
        stream.seek(0)
        self.assertEqual(list(bridge.read_ndjson_chunks(stream)), chunks)

    def test_path_round_trip_appends_each_chunk(self):
        path = self.root / "chunks.ndjson"
        chunks = [{"chunk_id": 0, "text": "一"}, {"chunk_id": 1, "text": "二"}]
        for chunk in chunks:
            bridge.write_ndjson_chunk(path, chunk)
        self.assertEqual(list(bridge.read_ndjson_chunks(path)), chunks)

    def test_returns_bytes_written_including_newline(self):
        stream = io.StringIO()
        chunk = {"chunk_id": 0, "text": "你好"}
        written = bridge.write_ndjson_chunk(stream, chunk)
        self.assertEqual(written, len(stream.getvalue().encode("utf-8")))
        self.assertEqual(stream.getvalue().count("\n"), 1)

    def test_each_chunk_is_one_line_with_utf8_cjk(self):
        stream = io.StringIO()
        bridge.write_ndjson_chunk(stream, {"chunk_id": 0, "text": "汉字"})
        line = stream.getvalue()
        self.assertTrue(line.endswith("\n"))
        self.assertEqual(line, '{"chunk_id":0,"text":"汉字"}\n')


class OversizeTests(unittest.TestCase):
    def test_oversize_chunk_becomes_sentinel(self):
        stream = io.StringIO()
        with unittest.mock.patch.object(bridge, "MAX_CHUNK_BYTES", 64):
            bridge.write_ndjson_chunk(stream, {"chunk_id": 7, "text": "x" * 200})
        stream.seek(0)
        self.assertEqual(list(bridge.read_ndjson_chunks(stream)),
                         [{"error": "extraction_chunk_oversize", "chunk_id": 7}])

    def test_stream_continues_after_oversize(self):
        stream = io.StringIO()
        with unittest.mock.patch.object(bridge, "MAX_CHUNK_BYTES", 64):
            bridge.write_ndjson_chunk(stream, {"chunk_id": 7, "text": "x" * 200})
            bridge.write_ndjson_chunk(stream, {"chunk_id": 8, "text": "ok"})
        stream.seek(0)
        self.assertEqual(list(bridge.read_ndjson_chunks(stream)),
                         [{"error": "extraction_chunk_oversize", "chunk_id": 7},
                          {"chunk_id": 8, "text": "ok"}])

    def test_default_cap_rejects_real_megabyte_line(self):
        stream = io.StringIO()
        huge = "x" * (bridge.MAX_CHUNK_BYTES + 10)
        bridge.write_ndjson_chunk(stream, {"chunk_id": 3, "text": huge})
        stream.seek(0)
        parsed = list(bridge.read_ndjson_chunks(stream))
        self.assertEqual(parsed, [{"error": "extraction_chunk_oversize", "chunk_id": 3}])

    def test_under_cap_chunk_is_written_verbatim(self):
        stream = io.StringIO()
        chunk = {"chunk_id": 1, "text": "small"}
        bridge.write_ndjson_chunk(stream, chunk)
        stream.seek(0)
        self.assertEqual(list(bridge.read_ndjson_chunks(stream)), [chunk])

    def test_oversize_without_chunk_id_reports_none(self):
        stream = io.StringIO()
        with unittest.mock.patch.object(bridge, "MAX_CHUNK_BYTES", 8):
            bridge.write_ndjson_chunk(stream, {"blob": "x" * 40})
        stream.seek(0)
        self.assertEqual(list(bridge.read_ndjson_chunks(stream)),
                         [{"error": "extraction_chunk_oversize", "chunk_id": None}])


class TailPreservationTests(TempDirTestCase):
    def test_single_line_result_tail_is_not_consumed(self):
        stream = io.StringIO()
        bridge.write_ndjson_chunk(stream, {"chunk_id": 0, "text": "一"})
        bridge.write_ndjson_chunk(stream, {"chunk_id": 1, "text": "二"})
        stream.write(json.dumps(final_result(outputs=["a.pdf"])) + "\n")
        stream.seek(0)

        chunks = list(bridge.read_ndjson_chunks(stream))
        self.assertEqual([c["chunk_id"] for c in chunks], [0, 1])
        # 终止行被回退，调用方仍能读到引擎最终结果。
        self.assertEqual(json.loads(stream.read().strip()), final_result(outputs=["a.pdf"]))

    def test_non_json_line_stops_iteration(self):
        stream = io.StringIO()
        bridge.write_ndjson_chunk(stream, {"chunk_id": 0, "text": "一"})
        stream.write("third-party notice - not json\n")
        stream.write(json.dumps({"chunk_id": 9}) + "\n")
        stream.seek(0)

        chunks = list(bridge.read_ndjson_chunks(stream))
        self.assertEqual(chunks, [{"chunk_id": 0, "text": "一"}])
        self.assertTrue(stream.read().startswith("third-party notice"))

    def test_non_object_json_line_stops_iteration(self):
        stream = io.StringIO()
        bridge.write_ndjson_chunk(stream, {"chunk_id": 0})
        stream.write("[1, 2, 3]\n")
        stream.seek(0)
        self.assertEqual(list(bridge.read_ndjson_chunks(stream)), [{"chunk_id": 0}])

    def test_path_stream_keeps_final_result_line(self):
        path = self.root / "with-tail.ndjson"
        bridge.write_ndjson_chunk(path, {"chunk_id": 0, "text": "一"})
        with open(path, "a", encoding="utf-8") as handle:
            handle.write(json.dumps(final_result()) + "\n")

        self.assertEqual(list(bridge.read_ndjson_chunks(path)), [{"chunk_id": 0, "text": "一"}])
        lines = path.read_text(encoding="utf-8").splitlines()
        self.assertTrue(json.loads(lines[-1])["ok"])


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
