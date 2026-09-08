# SPDX-License-Identifier: AGPL-3.0-only
# Copyright (C) 2026 AIPDF contributors
"""Synthetic extreme-input stress suite for the Windows engine (12 named cases).

Every case feeds corrupt, oversized, or hostile-but-synthetic PDFs straight to
backend.engine.dispatch() in-process (no subprocess). The suite pins the
engine's process contract on win32:

- stdout purity: dispatch must never leak a traceback; the outcome must map to
  the JSON protocol. Under engine.main() a success result is printed as
  {"ok": true, ...} (exit 0) and an engine.UserError as {"ok": false,
  "error": ...} (exit 1). In-process we assert on exactly the payload main()
  would print, plus the exit code that mapping implies. Real-byte stdout
  JSON purity over the subprocess protocol is pinned separately by
  tests/test_worker_json_protocol and tests/test_engine_utf8_windows.py.
- no crash: dispatch must either succeed or raise engine.UserError. Any other
  exception escaping dispatch is reported as a crash (the generic
  "处理引擎发生错误" fallback in main() would mask the real cause).
- no hang: each dispatch runs in a daemon thread joined with a timeout, so a
  stuck call fails the case instead of freezing the suite. The engine's own
  "timeout JSON" only applies to helper subprocesses; in-process fitz work is
  bounded by this guard (120 s for the page-count case, 150 s for the
  200 MB-class compress, which measures ~80 s on the QA reference machine).

All inputs are generated with PyMuPDF/Pillow inside tmp/; no personal
documents are read and nothing leaves the machine. Whole-suite budget is a
few minutes, well under the 10 minute ceiling.
"""
import hashlib
import io
import json
from contextlib import redirect_stderr, redirect_stdout
from pathlib import Path
import sys
import tempfile
import threading
import time
import unittest

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "backend"))
import engine
import pymupdf as fitz
from PIL import Image

GUARD_DEFAULT = 60      # any single dispatch longer than this counts as a hang
GUARD_PAGECOUNT = 120   # task bound for the 5000-page case
GUARD_200MB = 150       # headroom over the measured ~80 s compress of a ~190 MiB file
TARGET_200MB = 185 * 1048576   # "200MB-class": build until at least this many bytes
STRESS_PASSWORD = "stress-pw-2026"  # synthetic, never a real credential
GARBAGE = b"\xde\xad\xbe\xef\xca\xfe\xba\xbe" * 128  # 1024 deterministic pseudo-random bytes


@unittest.skipUnless(sys.platform == "win32", "Windows-only stress closure (in-process dispatch on win32)")
class StressWindowsTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        (ROOT / "tmp").mkdir(exist_ok=True)
        cls.temp = tempfile.TemporaryDirectory(prefix="aipdf-stress-", dir=ROOT / "tmp")
        cls.root = Path(cls.temp.name)
        cls.source = cls.root / "stress-source.pdf"
        # Six pages keeps the file comfortably above 1024 bytes so the
        # garbage-header and xref cases slice a real document, not a stub.
        doc = fitz.open()
        for i in range(6):
            page = doc.new_page(width=595, height=842)
            page.insert_text((50, 60), f"AIPDF stress page {i+1}", fontsize=14)
        doc.save(cls.source)
        doc.close()
        assert cls.source.stat().st_size > 1200, "stress source too small for header slicing"

    @classmethod
    def tearDownClass(cls):
        cls.temp.cleanup()

    # ---- plumbing ---------------------------------------------------------

    def guarded(self, operation, files, options=None, output_dir=None, timeout=GUARD_DEFAULT):
        """Run engine.dispatch under a join-timeout guard.

        Returns a dict with status ("ok" | "error" | "crash"), the result or
        UserError message, everything dispatch printed to stdout/stderr (the
        engine's progress channel must stay on stderr), and the elapsed time.
        A thread that outlives the timeout is reported as a hang.
        """
        request = {"operation": operation, "files": [str(p) for p in files],
                   "options": options or {}, "outputDir": str(output_dir or self.root)}
        outcome = {}

        def work():
            out, err = io.StringIO(), io.StringIO()
            try:
                with redirect_stdout(out), redirect_stderr(err):
                    result = engine.dispatch(request)
                outcome.update(status="ok", result=result, stdout=out.getvalue(), stderr=err.getvalue())
            except engine.UserError as error:
                outcome.update(status="error", error=str(error), stdout=out.getvalue(), stderr=err.getvalue())
            except BaseException as error:  # noqa: BLE001 - any escape is a contract breach
                outcome.update(status="crash", error=error, stdout=out.getvalue(), stderr=err.getvalue())

        worker = threading.Thread(target=work, name="dispatch-guard", daemon=True)
        started = time.monotonic()
        worker.start()
        worker.join(timeout)
        if worker.is_alive():
            self.fail(f"疑似挂起：dispatch({operation}) 超过 {timeout}s 未返回")
        outcome["elapsed"] = time.monotonic() - started
        return outcome

    def assert_contract(self, outcome, expect, label):
        """Universal per-case assertions; returns the exit code main() would use.

        expect: "ok" (must succeed, exit 0), "error" (must raise UserError,
        exit 1), or "graceful" (either is acceptable as long as the contract
        holds). Asserts no traceback on either captured stream and that the
        exact JSON payload main() would print parses cleanly.
        """
        self.assertNotIn("Traceback", outcome["stdout"], f"{label}: traceback leaked to stdout")
        self.assertNotIn("Traceback", outcome["stderr"], f"{label}: traceback leaked to stderr")
        status = outcome["status"]
        if status == "crash":
            self.fail(f"{label}: dispatch 抛出了 {outcome['error']!r}，不是约定的 UserError（未处理崩溃）")
        if status == "ok":
            if expect == "error":
                self.fail(f"{label}: 期望错误 JSON，却返回了成功结果")
            result = outcome["result"]
            self.assertTrue(result["ok"], label)
            for path in result["outputs"]:
                self.assertTrue(Path(path).is_file(), f"{label}: 输出文件缺失 {path}")
            payload = json.dumps(result, ensure_ascii=False)
            self.assertNotIn("Traceback", payload, f"{label}: traceback text inside success JSON")
            self.assertIsInstance(json.loads(payload), dict, f"{label}: 成功 JSON 无法解析")
            return 0
        if expect == "ok":
            self.fail(f"{label}: 期望成功，却得到错误 JSON：{outcome['error']}")
        message = outcome["error"]
        self.assertTrue(message.strip(), f"{label}: 错误 JSON 的 message 为空")
        payload = json.dumps({"ok": False, "error": message}, ensure_ascii=False)  # engine.main() 的输出
        self.assertNotIn("Traceback", payload, f"{label}: traceback text inside error JSON")
        self.assertFalse(json.loads(payload)["ok"], label)
        return 1

    def build_protected(self):
        """One encrypted synthetic PDF; returns the dispatch outcome (ok)."""
        protected = self.guarded("protect", [self.source], {"newPassword": STRESS_PASSWORD})
        self.assertEqual(protected["status"], "ok", str(protected.get("error")))
        return protected["result"]["outputs"]

    # ---- 12 named cases ---------------------------------------------------

    def test_zero_byte_file(self):
        """① 0-byte file through merge and compress: error JSON, never a crash."""
        zero = self.root / "zero.pdf"
        zero.write_bytes(b"")
        merged = self.guarded("merge", [zero, self.source])
        self.assertEqual(self.assert_contract(merged, "error", "merge zero-byte"), 1)
        compressed = self.guarded("compress", [zero])
        self.assertEqual(self.assert_contract(compressed, "error", "compress zero-byte"), 1)
        self.assertIn("无法读取", compressed["error"])

    def test_garbage_header(self):
        """② First 1024 bytes replaced with pseudo-random garbage: error JSON.

        MuPDF recovers zero usable pages from the mangled header, so the
        engine rejects the file instead of emitting a broken output.
        """
        target = self.root / "garbage-header.pdf"
        target.write_bytes(GARBAGE + self.source.read_bytes()[1024:])
        outcome = self.guarded("compress", [target])
        self.assertEqual(self.assert_contract(outcome, "error", "garbage-header"), 1)

    def test_truncated_tail(self):
        """③ Valid PDF truncated to 70% (xref/EOF cut off): graceful error JSON."""
        target = self.root / "truncated.pdf"
        data = self.source.read_bytes()
        target.write_bytes(data[: int(len(data) * 0.7)])
        outcome = self.guarded("compress", [target])
        self.assertEqual(self.assert_contract(outcome, "error", "truncated-tail"), 1)

    def test_corrupt_xref(self):
        """④ startxref pointed past EOF with a junk trailer: MuPDF auto-repairs.

        Graceful handling here means the engine recovers all 6 pages and
        produces a valid output, or rejects cleanly - measured behavior is
        full recovery, which we pin.
        """
        data = self.source.read_bytes()
        target = self.root / "corrupt-xref.pdf"
        target.write_bytes(data[: data.rfind(b"startxref")] + b"startxref\n999999999\ntrailer\n<< /Root 1 0 R >>\n%%EOF\n")
        outcome = self.guarded("compress", [target])
        code = self.assert_contract(outcome, "graceful", "corrupt-xref")
        self.assertEqual(code, 0, f"corrupt-xref: MuPDF 应能自动修复这份文件：{outcome.get('error')}")
        with fitz.open(outcome["result"]["outputs"][0]) as doc:
            self.assertEqual(len(doc), 6, "修复后应恢复全部 6 页")

    def test_encrypt_no_password(self):
        """⑤ Encrypted PDF sent to unlock with an empty password: error JSON."""
        protected = self.build_protected()
        outcome = self.guarded("unlock", protected)
        self.assertEqual(self.assert_contract(outcome, "error", "unlock no-password"), 1)
        self.assertIn("密码", outcome["error"])

    def test_encrypt_wrong_password(self):
        """⑥ Encrypted PDF with a wrong password: error JSON.

        Passwords travel only inside the request: over the subprocess protocol
        the JSON (with the password field) is read from stdin and nothing is
        logged; in-process the same field is options["password"]. The stdin
        byte-level path is pinned by tests/test_worker_json_protocol.
        """
        protected = self.build_protected()
        outcome = self.guarded("unlock", protected, {"password": "definitely-not-" + STRESS_PASSWORD})
        self.assertEqual(self.assert_contract(outcome, "error", "unlock wrong-password"), 1)
        self.assertIn("密码", outcome["error"])

    def test_large_page_count(self):
        """⑦ 5000-page synthetic PDF through split and merge within 120 s each."""
        big = self.root / "5000-pages.pdf"
        doc = fitz.open()
        for i in range(5000):
            page = doc.new_page(width=180, height=240)
            if i == 0:
                page.insert_text((10, 20), "first", fontsize=8)
        doc.save(big, garbage=4, deflate=True)
        doc.close()
        with fitz.open(big) as check:
            self.assertEqual(check.page_count, 5000)

        split = self.guarded("split", [big], {"ranges": "1-2500;2501-5000"}, timeout=GUARD_PAGECOUNT)
        self.assertEqual(self.assert_contract(split, "ok", "split 5000p"), 0)
        self.assertLessEqual(split["elapsed"], GUARD_PAGECOUNT)
        counts = []
        for path in split["result"]["outputs"]:
            with fitz.open(path) as part:
                counts.append(part.page_count)
        self.assertEqual(sorted(counts), [2500, 2500])

        merged = self.guarded("merge", [big, self.source], timeout=GUARD_PAGECOUNT)
        self.assertEqual(self.assert_contract(merged, "ok", "merge 5000p"), 0)
        self.assertLessEqual(merged["elapsed"], GUARD_PAGECOUNT)
        with fitz.open(merged["result"]["outputs"][0]) as doc:
            self.assertEqual(len(doc), 5006)  # 5000 + 6 页 stress-source

    def test_large_file_200mb(self):
        """⑧ 200MB-class image-filled PDF through compress: completes, output
        smaller than input, no temp-dir leakage, input untouched."""
        huge = self.root / "200mb-class.pdf"
        doc = fitz.open()
        accumulated, pages = 0, 0
        while accumulated < TARGET_200MB and pages < 40:
            # Unique noise per page: identical streams would dedupe into one xref.
            noise = Image.effect_noise((3000, 3000), 50 + (pages % 24))
            stream = io.BytesIO()
            noise.save(stream, format="JPEG", quality=92)
            payload = stream.getvalue()
            page = doc.new_page(width=595, height=842)
            page.insert_image(fitz.Rect(0, 0, 595, 842), stream=payload)
            accumulated += len(payload)
            pages += 1
        doc.save(huge, garbage=4, deflate=True)
        doc.close()
        self.assertGreaterEqual(huge.stat().st_size, 180 * 1048576, "生成的压力文件不足 200MB-class")
        digest = hashlib.sha256(huge.read_bytes()).hexdigest()

        system_temp = Path(tempfile.gettempdir())
        before = set(system_temp.glob("aipdf-*"))
        outcome = self.guarded("compress", [huge], {"quality": "small"}, timeout=GUARD_200MB)
        self.assertEqual(self.assert_contract(outcome, "ok", "compress 200MB"), 0)
        self.assertLessEqual(outcome["elapsed"], GUARD_200MB)
        result = outcome["result"]
        self.assertLess(result["outputBytes"], result["inputBytes"], "压缩后体积应当变小")
        self.assertEqual(set(system_temp.glob("aipdf-*")) - before, set(), "系统临时目录泄漏了 aipdf-* 残留")
        self.assertEqual(hashlib.sha256(huge.read_bytes()).hexdigest(), digest, "源文件被修改")

    def test_missing_font(self):
        """⑨ Text whose BaseFont points nowhere: render path stays graceful.

        /BaseFont is rewritten to a non-existent face, then pdf_to_images
        forces MuPDF to actually rasterize the text with a missing font.
        """
        target = self.root / "missing-font.pdf"
        doc = fitz.open(self.source)
        seen = set()
        for page in doc:
            for font in page.get_fonts(full=True):
                if font[0] not in seen:
                    seen.add(font[0])
                    doc.xref_set_key(font[0], "BaseFont", "/NoSuchFontXYZ")
        doc.save(target)
        doc.close()
        outcome = self.guarded("pdf_to_images", [target], {"format": "png", "pages": "1-2"})
        code = self.assert_contract(outcome, "graceful", "missing-font")
        self.assertEqual(code, 0, f"missing-font: 渲染应能容错替代字体：{outcome.get('error')}")
        self.assertEqual(len(outcome["result"]["outputs"]), 2)

    def test_negative_page_numbers(self):
        """⑩ Negative / out-of-range / malformed page params: error JSON."""
        for label, operation, options in [
            ("rotate negative pages", "rotate", {"pages": "-1", "angle": "90"}),
            ("extract zero page", "extract", {"pages": "0"}),
            ("remove beyond range", "remove", {"pages": "99999"}),
            ("extract malformed range", "extract", {"pages": "1,,2"}),
            ("rotate non-numeric angle", "rotate", {"angle": "abc"}),
        ]:
            outcome = self.guarded(operation, [self.source], options)
            self.assertEqual(self.assert_contract(outcome, "error", label), 1, label)

    def test_cp936_filename(self):
        """⑪ Chinese filename survives dispatch and the JSON payload intact.

        The cp936 console protocol (bytes-level) is pinned by
        tests/test_engine_utf8_windows.py; here the same name is checked
        through in-process dispatch: CJK preserved in the output path, no
        U+FFFD anywhere, strict UTF-8 round-trip of the payload, and the name
        stays representable in the legacy cp936 code page.
        """
        name = "中文压测文档"
        self.assertEqual(name.encode("cp936").decode("cp936"), name, "测试文件名应可被 cp936 表示")
        target = self.root / f"{name}.pdf"
        target.write_bytes(self.source.read_bytes())
        outcome = self.guarded("rotate", [target], {"angle": "90"})
        self.assertEqual(self.assert_contract(outcome, "ok", "cp936 filename"), 0)
        result = outcome["result"]
        output_name = Path(result["outputs"][0]).name
        self.assertIn(name, output_name)
        payload = json.dumps(result, ensure_ascii=False)
        self.assertNotIn("\ufffd", payload)
        self.assertEqual(payload.encode("utf-8").decode("utf-8"), payload)
        with fitz.open(result["outputs"][0]) as doc:
            self.assertEqual(doc[0].rotation, 90)

    def test_nonexistent_output_dir(self):
        """⑫ outputDir pointing at a missing path: error JSON, nothing created."""
        missing = self.root / "no-such-dir" / "deeper"
        outcome = self.guarded("rotate", [self.source], {}, output_dir=missing)
        self.assertEqual(self.assert_contract(outcome, "error", "nonexistent outputDir"), 1)
        self.assertIn("输出文件夹", outcome["error"])
        self.assertFalse(missing.exists(), "引擎不应替用户创建输出目录")


if __name__ == "__main__":
    if sys.platform == "win32":
        # Mirror engine.py: keep redirected output UTF-8 regardless of console code page.
        for stream in (sys.stdout, sys.stderr):
            try:
                stream.reconfigure(encoding="utf-8")
            except (AttributeError, ValueError):
                pass
    (ROOT / "tmp").mkdir(exist_ok=True)
    unittest.main(verbosity=2)
