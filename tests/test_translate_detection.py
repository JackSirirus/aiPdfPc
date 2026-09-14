# SPDX-License-Identifier: AGPL-3.0-only
# Copyright (C) 2026 AIPDF contributors
"""结构化降级检测测试；只用内存合成 PDF，不读写任何用户文档。

覆盖《翻译-回填排版规格.md》§1/§2.4/§4.4 的四类结构化降级原因：

- `rotation_crop`：旋转 / 裁剪页回填需坐标归一化（§1）；
- `unsupported_script`：RTL / 竖排块保留原文并标注（§2.4）；
- `formula_fail`：公式块无法忠实回填（§4.4）；
- `table_fail`：表格结构无法保留（§4.4）。

逐层验证：检测启发式（`detect`）→ 抽取标注（`extract`）→ 回填落地（`reflow`）→
翻译跳过（`engine`）→ 报告 sidecar。LLM 调用全部 monkeypatch，不发起真实网络请求。
"""
import json
import hashlib
import sys
import tempfile
import unittest
import unittest.mock
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "backend"))

import engine
import pymupdf as fitz
from translate import chunk, detect, extract, geometry, llm, reflow, verify

ARABIC = "\u0627\u0644\u0639\u0631\u0628\u064a\u0629 \u0645\u0631\u062d\u0628\u0627"
HEBREW = "\u05e9\u05dc\u05d5\u05dd \u05e2\u05d5\u05dc\u05dd"
FORMULA = "f(x)=x^2+y^2/(a-b)"
PROSE = "The quick brown fox jumps over the lazy dog"
ENDPOINT = "https://api.example.test/v1"
API_KEY = "sk-detection-secret-key-123456"
MODEL = "test-model"


def block(rect, text, size=12.0, font="helv", page_no=1, script=""):
    """构造规范坐标块，独立于抽取器测试回填。"""
    return extract.TextBlock(
        page_no=page_no, rect=geometry.RectNorm(rect[0], rect[1], rect[2], rect[3], 0),
        text=text, size=size, font=font, unsupported_script=script)


# —— 检测启发式（detect） -----------------------------------------------------

class DetectTests(unittest.TestCase):
    def test_rtl_ratio_and_threshold(self):
        self.assertGreater(detect.rtl_ratio(ARABIC), detect.RTL_RATIO_THRESHOLD)
        self.assertEqual(detect.rtl_ratio(PROSE), 0.0)
        self.assertTrue(detect.is_rtl_text(ARABIC))
        self.assertTrue(detect.is_rtl_text(HEBREW))
        self.assertFalse(detect.is_rtl_text(PROSE))
        # 拉丁为主 + 少量 RTL → 不误判。
        mixed = "hello world " * 4 + ARABIC
        self.assertFalse(detect.is_rtl_text(mixed))

    def test_vertical_lines_and_classify(self):
        self.assertTrue(detect.is_vertical_lines([(0, -1), (0, -1)]))
        self.assertFalse(detect.is_vertical_lines([(1, 0), (1, 0)]))
        self.assertFalse(detect.is_vertical_lines([]))
        self.assertEqual(detect.classify_unsupported(PROSE, [(0, -1)]), "vertical")
        self.assertEqual(detect.classify_unsupported(ARABIC, [(1, 0)]), "rtl")
        self.assertEqual(detect.classify_unsupported(PROSE, [(1, 0)]), "")

    def test_vertical_detection_skips_rotated_pages(self):
        """F17：/Rotate 90/270 使内容坐标轴对调，dir 不可靠 → 不判竖排，避免误跳过。"""
        # 未旋转页：竖排正常判定。
        self.assertTrue(detect.is_vertical_lines([(0, -1), (0, -1)]))
        self.assertTrue(detect.is_vertical_lines([(0, -1), (0, -1)], rotation=0))
        # 180 不交换轴：竖排仍判定。
        self.assertTrue(detect.is_vertical_lines([(0, -1), (0, -1)], rotation=180))
        # 90/270 交换轴：不判竖排（扫描件侧放、靠 /Rotate 摆正即属此类）。
        self.assertFalse(detect.is_vertical_lines([(0, -1), (0, -1)], rotation=90))
        self.assertFalse(detect.is_vertical_lines([(0, -1), (0, -1)], rotation=270))
        self.assertFalse(detect.is_vertical_lines([(1, 0), (1, 0)], rotation=90))
        self.assertEqual(detect.classify_unsupported(PROSE, [(0, 1)], rotation=90), "")
        self.assertEqual(detect.classify_unsupported(PROSE, [(0, -1)], rotation=0), "vertical")

    def test_math_density(self):
        self.assertTrue(detect.is_math_dense(FORMULA))
        self.assertFalse(detect.is_math_dense(PROSE))
        self.assertFalse(detect.is_math_dense(""))  # 空文本不误判
        # 普通正文里偶有运算符，占比不足 → 不误判。
        self.assertFalse(detect.is_math_dense("The value is 3 plus 4 equals 7."))

    def test_table_grid_regular_and_irregular(self):
        cells = []
        for row in range(4):
            for col in range(2):
                cells.append(block((40 + col * 120, 60 + row * 30,
                                    100 + col * 120, 72 + row * 30), f"c{row}{col}", size=9))
        grid = detect.table_grid(cells)
        self.assertIsNotNone(grid)
        self.assertEqual((grid["rows"], grid["cols"]), (4, 2))
        self.assertFalse(grid["irregular"])
        # 少一个列位（合并单元格）→ 不规则，行内回填无法还原。
        merged = [c for i, c in enumerate(cells) if i != 3]
        self.assertTrue(detect.table_grid(merged)["irregular"])
        # 块太少 → 不误判为表格。
        self.assertIsNone(detect.table_grid(cells[:2]))

    def test_page_geometry_degraded(self):
        plain = fitz.open()
        self.addCleanup(plain.close)
        page = plain.new_page(width=400, height=400)
        self.assertFalse(detect.page_geometry_degraded(page))
        page.set_rotation(90)
        self.assertTrue(detect.page_geometry_degraded(page))
        cropped = plain.new_page(width=400, height=400)
        cropped.set_cropbox(fitz.Rect(40, 40, 360, 360))
        self.assertTrue(detect.page_geometry_degraded(cropped))

    def test_two_column_short_prose_not_table_without_grid_lines(self):
        """F18/F34：无表格线的两栏短文本不得判为表格（块级启发式会误报）。"""
        doc = fitz.open()
        self.addCleanup(doc.close)
        page = doc.new_page(width=400, height=400)
        # 两栏各 3 行短文本，左右基线错开，避免 PyMuPDF 把同行并入一个块。
        for row, y in enumerate((60, 120, 180)):
            page.insert_text((40, y), f"left{row}", fontsize=10)
            page.insert_text((220, y + 10), f"right{row}", fontsize=10)
        blocks = extract.extract_page(page)
        # 块级启发式本身会命中（短单行块排成网格）……
        self.assertIsNotNone(detect.table_grid(blocks))
        # ……但没有矢量表格线佐证 → 页面级不判为表格。
        self.assertIsNone(detect.table_structure(page, blocks))
        self.assertFalse(detect.has_grid_lines(page))

    def test_table_structure_still_detects_with_grid_lines(self):
        """有表格线时块级路径仍然生效（回归）。"""
        doc = fitz.open()
        self.addCleanup(doc.close)
        page = doc.new_page(width=420, height=420)
        for row, y in enumerate((60, 110, 160)):
            for col, x in enumerate((40, 170)):
                page.insert_text((x, y), f"c{row}{col}", fontsize=9)
        for x in (30, 160, 290):
            page.draw_line(fitz.Point(x, 40), fitz.Point(x, 190))
        for y in (45, 95, 145, 190):
            page.draw_line(fitz.Point(30, y), fitz.Point(290, y))
        blocks = extract.extract_page(page)
        self.assertIsNotNone(detect.table_structure(page, blocks))

    def test_has_grid_lines_is_cached_per_page(self):
        """F20：同一页重复询问命中缓存，避免多次扫描全部 drawings。"""
        doc = fitz.open()
        self.addCleanup(doc.close)
        page = doc.new_page(width=400, height=400)
        self.assertFalse(detect.has_grid_lines(page))       # 首次扫描并缓存
        for x in (30, 160, 290):
            page.draw_line(fitz.Point(x, 40), fitz.Point(x, 190))
        for y in (45, 95, 145, 190):
            page.draw_line(fitz.Point(30, y), fitz.Point(290, y))
        self.assertFalse(detect.has_grid_lines(page))       # 命中缓存：仍是旧值
        self.assertTrue(detect._scan_grid_lines(page, 2, 2, 0.5))  # 实际扫描为 True


# —— 抽取标注（extract） ------------------------------------------------------

class ExtractScriptTests(unittest.TestCase):
    def make_doc(self):
        doc = fitz.open()
        self.addCleanup(doc.close)
        return doc

    def test_rtl_flagged_from_text(self):
        """RTL 文本（含 OCR 块）在抽取阶段标注为 unsupported_script。"""
        doc = self.make_doc()
        page = doc.new_page(width=400, height=300)
        boxes = [{"text": ARABIC, "x": 0.1, "y": 0.1, "w": 0.5, "h": 0.05}]
        blocks = extract.extract_page(page, ocr_boxes=boxes)
        self.assertEqual(blocks[0].unsupported_script, "rtl")

    def test_vertical_flagged_from_line_direction(self):
        """竖排行方向（line['dir'] 非水平）标注为 vertical。"""
        doc = self.make_doc()
        page = doc.new_page(width=400, height=300)
        page.insert_text((180, 60), "VerticalText", fontsize=12, rotate=90)
        blocks = extract.extract_page(page)
        self.assertTrue(blocks, "竖排块未被抽取")
        self.assertEqual(blocks[0].unsupported_script, "vertical")

    def test_normal_text_not_flagged(self):
        doc = self.make_doc()
        page = doc.new_page(width=400, height=300)
        page.insert_text((40, 60), PROSE, fontsize=12)
        blocks = extract.extract_page(page)
        self.assertEqual(blocks[0].unsupported_script, "")

    def test_rotated_page_horizontal_text_not_flagged(self):
        """F17 负向：/Rotate 90 页上摆正的横排文本不得判为竖排（否则整块不翻译）。"""
        doc = self.make_doc()
        page = doc.new_page(width=300, height=200)
        # 内容在未旋转坐标里侧放（dir≈(0,1)），/Rotate 90 使其在可见页面摆正为横排。
        page.insert_text((100, 100), PROSE, fontsize=12, rotate=270)
        page.set_rotation(90)
        self.assertEqual(page.rotation, 90)
        blocks = extract.extract_page(page)
        self.assertTrue(blocks, "旋转页文本未被抽取")
        self.assertEqual(blocks[0].unsupported_script, "")


# —— 回填落地（reflow） -------------------------------------------------------

class ReflowDetectionTests(unittest.TestCase):
    def make_page(self, width=400, height=400, rotation=0):
        doc = fitz.open()
        self.addCleanup(doc.close)
        page = doc.new_page(width=width, height=height)
        if rotation:
            page.set_rotation(rotation)
        return page

    def report_reasons(self, report, page_no=1):
        return next((item["reasons"] for item in report.to_dict()["pages"]
                     if item["page"] == page_no), [])

    def report_detail(self, report, reason, page_no=1):
        item = next((entry for entry in report.to_dict()["pages"]
                     if entry["page"] == page_no), None)
        return None if item is None else item.get("detail", {}).get(reason)

    # unsupported_script ---------------------------------------------------

    def test_unsupported_flag_keeps_original_and_records_index(self):
        """标记为 RTL 的块不翻译：原文保留、不 redaction，报告含块下标。"""
        page = self.make_page()
        page.insert_text((40, 60), "Original kept text", fontsize=12)
        blocks = extract.extract_page(page)
        blocks[0].unsupported_script = "rtl"
        report = reflow.DegradationReport()
        reflow.backfill_page(page, blocks, {0: ARABIC}, report)
        self.assertIn("Original kept text", page.get_text())
        self.assertNotIn(ARABIC, page.get_text())
        self.assertEqual(self.report_reasons(report), ["unsupported_script"])
        self.assertEqual(self.report_detail(report, "unsupported_script")["blocks"], [0])
        self.assertEqual(len(list(page.annots() or [])), 0)  # 未 redaction/未铺黑框

    def test_rtl_text_detected_without_flag(self):
        """抽取未标注时，回填按块文本补判 RTL 并记 unsupported_script。"""
        page = self.make_page()
        page.insert_text((40, 60), "hebrew source", fontsize=12)
        blocks = [block((35, 45, 260, 80), HEBREW)]
        report = reflow.DegradationReport()
        reflow.backfill_page(page, blocks, {0: "should not be written"}, report)
        self.assertIn("unsupported_script", self.report_reasons(report))
        self.assertNotIn("should not be written", page.get_text())

    # formula_fail ---------------------------------------------------------

    def test_formula_fail_when_overflowing(self):
        page = self.make_page()
        page.insert_text((40, 60), FORMULA, fontsize=10)
        blocks = extract.extract_page(page)
        self.assertTrue(detect.is_math_dense(blocks[0].text))
        report = reflow.DegradationReport()
        reflow.backfill_page(page, blocks, {0: "translated " * 200}, report)
        self.assertIn("formula_fail", self.report_reasons(report))

    def test_formula_that_fits_not_flagged(self):
        page = self.make_page()
        page.insert_text((40, 60), FORMULA, fontsize=10)
        blocks = extract.extract_page(page)
        report = reflow.DegradationReport()
        reflow.backfill_page(page, blocks, {0: "译文"}, report)
        self.assertNotIn("formula_fail", self.report_reasons(report))

    # table_fail -----------------------------------------------------------

    def _table_page(self):
        page = self.make_page(width=420, height=420)
        xs, ys = [40, 170, 300], [60, 110, 160, 210]
        for row, y in enumerate(ys):
            for col, x in enumerate(xs):
                page.insert_text((x, y), f"r{row}c{col}", fontsize=9)
        for x in (30, 160, 290, 410):
            page.draw_line(fitz.Point(x, 40), fitz.Point(x, 230))
        for y in (45, 95, 145, 195, 230):
            page.draw_line(fitz.Point(30, y), fitz.Point(410, y))
        return page

    def test_table_fail_when_cells_overflow(self):
        page = self._table_page()
        blocks = extract.extract_page(page)
        self.assertIsNotNone(detect.table_structure(page, blocks))
        report = reflow.DegradationReport()
        reflow.backfill_page(page, blocks,
                             {i: "translated " * 200 for i in range(len(blocks))}, report)
        self.assertIn("table_fail", self.report_reasons(report))

    def test_table_with_fitting_cells_not_flagged(self):
        page = self._table_page()
        blocks = extract.extract_page(page)
        report = reflow.DegradationReport()
        reflow.backfill_page(page, blocks, {i: "表" for i in range(len(blocks))}, report)
        self.assertNotIn("table_fail", self.report_reasons(report))

    # rotation_crop --------------------------------------------------------

    def test_rotated_page_records_rotation_crop(self):
        page = self.make_page(rotation=90)
        page.insert_text((40, 60), "Rotated source", fontsize=12)
        blocks = extract.extract_page(page)
        report = reflow.DegradationReport()
        reflow.backfill_page(page, blocks, {0: "旋转译文"}, report)
        self.assertEqual(page.rotation, 90)
        self.assertIn("rotation_crop", self.report_reasons(report))

    def test_cropped_page_records_rotation_crop(self):
        page = self.make_page(width=595, height=842)
        page.set_cropbox(fitz.Rect(50, 100, 545, 600))
        page.insert_text((120, 260), "inside crop", fontsize=12)
        blocks = extract.extract_page(page)
        report = reflow.DegradationReport()
        reflow.backfill_page(page, blocks, {0: "裁剪译文"}, report)
        self.assertIn("rotation_crop", self.report_reasons(report))

    def test_plain_page_has_no_rotation_crop(self):
        page = self.make_page()
        page.insert_text((40, 60), "Plain source", fontsize=12)
        blocks = extract.extract_page(page)
        report = reflow.DegradationReport()
        reflow.backfill_page(page, blocks, {0: "普通译文"}, report)
        self.assertNotIn("rotation_crop", self.report_reasons(report))


# —— 翻译跳过（engine）与报告 sidecar ----------------------------------------

class EngineSkipTests(unittest.TestCase):
    def test_unsupported_block_not_sent_to_model(self):
        """标记 unsupported_script 的块不发送请求（决策 §2.4：保留原文不翻译）。"""

        class ExplodingClient:
            def translate(self, pair, validator=None):
                raise AssertionError("不支持的脚本块不应发送到模型")

        report = verify.DegradationReport()
        blocks = [block((35, 45, 260, 80), ARABIC, script="rtl")]
        translations = engine._translate_blocks(blocks, report, ExplodingClient(),
                                                "generic", None, "auto", "zh")
        self.assertEqual(len(translations), 1)
        self.assertEqual(translations[0].text, "")  # 未翻译 → 回填保留原文

    def test_rotated_page_horizontal_text_is_translated(self):
        """F17：旋转页横排文本（未旋转坐标 dir 竖排）不跳过，照常发送并回填译文。"""
        doc = fitz.open()
        self.addCleanup(doc.close)
        page = doc.new_page(width=300, height=200)
        page.insert_text((100, 100), "Hello rotated page", fontsize=12, rotate=270)
        page.set_rotation(90)
        blocks = extract.extract_page(page)
        self.assertEqual(blocks[0].unsupported_script, "")

        class FakeClient:
            def translate(self, pair, validator=None):
                content = json.dumps([{"index": 0, "text": "旋转页译文"}])
                if validator is not None:
                    validator(content)
                return content

        report = verify.DegradationReport()
        translations = engine._translate_blocks(blocks, report, FakeClient(),
                                                "generic", None, "auto", "zh")
        self.assertEqual(translations[0].text, "旋转页译文")
        self.assertFalse(translations[0].degraded)


class SegmentSplitReportTests(unittest.TestCase):
    """F2：超长段落被切分时，engine 必须把 segment_split 记入降级报告。"""

    class _LenClient:
        """返回与请求正文等长但内容不同的译文，使校验通过并覆盖每个切分片。"""

        def translate(self, pair, validator=None):
            text = pair["user"].split(">", 1)[1].rsplit("</untrusted>", 1)[0]
            content = json.dumps([{"index": 0, "text": "译" * max(1, len(text))}])
            if validator is not None:
                validator(content)
            return content

    def test_oversized_block_reports_segment_split(self):
        # 无标点超长串 → 超过硬上限被强制字符切分（chunk 标 segment_split）。
        text = "x" * 8000
        blocks = [block((35, 45, 260, 80), text)]
        report = verify.DegradationReport()
        engine._translate_blocks(blocks, report, self._LenClient(),
                                 "generic", None, "auto", "zh")
        reasons = {reason for item in report.to_dict()["pages"] for reason in item["reasons"]}
        self.assertIn("segment_split", reasons)

    def test_normal_block_reports_no_segment_split(self):
        blocks = [block((35, 45, 260, 80), "Short sentence.")]
        report = verify.DegradationReport()
        engine._translate_blocks(blocks, report, self._LenClient(),
                                 "generic", None, "auto", "zh")
        reasons = {reason for item in report.to_dict()["pages"] for reason in item["reasons"]}
        self.assertNotIn("segment_split", reasons)


class OverlapReassemblyTests(unittest.TestCase):
    """F1：多分块 + 上下文重叠时，每个块必须拿到自己的译文，不得错位/丢段。"""

    # 与源文字符数相近的中文填充，避免触发 verify 的长度比例校验。
    _FILLER = "这是译文内容。" * 60

    @classmethod
    def _translation(cls, source_text):
        # 以源文摘要作为译文前缀，既能通过 echo / 长度校验，又可逐块核对对位。
        digest = hashlib.md5(source_text.encode("utf-8")).hexdigest()[:12]
        return f"【{digest}】" + cls._FILLER

    @classmethod
    def _digest_client(cls):
        class DigestClient:
            def translate(self, pair, validator=None):
                text = pair["user"].split(">", 1)[1].rsplit("</untrusted>", 1)[0]
                content = json.dumps([{"index": 0, "text": cls._translation(text)}])
                if validator is not None:
                    validator(content)
                return content
        return DigestClient()

    def test_overlap_chunks_each_block_gets_own_translation(self):
        # 每块 ~250 token 且互不相同；8 块 >> 1200 → 至少 2 个分块且后续块带上下文重叠。
        blocks = [extract.TextBlock(
            page_no=1, rect=geometry.RectNorm(0, 0, 1, 1, 0),
            text=f"Block {index}. " + "Sentence one. Sentence two. " * 20)
            for index in range(8)]
        chunks = chunk.chunk_blocks(blocks)
        self.assertGreaterEqual(len(chunks), 2, "用例应产生多个分块")
        self.assertTrue(any(item.overlap_count for item in chunks), "用例应含上下文重叠")

        report = verify.DegradationReport()
        results = engine._translate_blocks(blocks, report, self._digest_client(),
                                            "generic", None, "auto", "zh")

        expected_order = [index for item in chunks for index in item.source_indices]
        self.assertEqual([result.index for result in results], expected_order)
        self.assertFalse(any(result.degraded for result in results))
        for result in results:
            self.assertEqual(result.text, self._translation(blocks[result.index].text))



class ReportSidecarTests(unittest.TestCase):
    """旋转页经 engine.dispatch 后，rotation_crop 应进入结果与 sidecar JSON。"""

    @classmethod
    def setUpClass(cls):
        cls.temp = tempfile.TemporaryDirectory(prefix="aipdf-detect-")
        cls.root = Path(cls.temp.name)

    @classmethod
    def tearDownClass(cls):
        cls.temp.cleanup()

    class FakeResponse:
        def __init__(self, body):
            self._body = json.dumps(body).encode("utf-8")

        def read(self):
            return self._body

        def __enter__(self):
            return self

        def __exit__(self, *exc):
            return False

    def test_rotation_crop_reaches_sidecar(self):
        source = self.root / "rotated.pdf"
        doc = fitz.open()
        page = doc.new_page(width=400, height=400)
        page.insert_text((60, 80), "Hello rotated world", fontsize=12)
        page.set_rotation(90)
        doc.save(source)
        doc.close()

        content = json.dumps([{"index": 0, "text": "旋转译文"}])
        body = {"choices": [{"index": 0, "message": {"role": "assistant", "content": content},
                             "finish_reason": "stop"}]}
        request = {"operation": "translate", "files": [str(source)],
                   "outputDir": str(self.root),
                   "options": {"apiKey": API_KEY, "endpoint": ENDPOINT, "model": MODEL,
                               "targetLang": "zh", "sourceLang": "auto",
                               "template": "generic", "format": "mono"}}
        with unittest.mock.patch.object(llm, "_open",
                                        return_value=self.FakeResponse(body)):
            result = engine.dispatch(request)

        self.assertTrue(result["ok"])
        reasons = {reason for entry in result["results"][0]["report"]["pages"]
                   for reason in entry["reasons"]}
        self.assertIn("rotation_crop", reasons)
        reports = list(Path(result["outputDir"]).glob("*-降级报告.json"))
        self.assertEqual(len(reports), 1)
        sidecar = json.loads(reports[0].read_text(encoding="utf-8"))
        sidecar_reasons = {reason for entry in sidecar["pages"] for reason in entry["reasons"]}
        self.assertIn("rotation_crop", sidecar_reasons)
        # 源文件只读不变。
        with fitz.open(str(source)) as reopened:
            self.assertIn("Hello rotated world", reopened[0].get_text())


if __name__ == "__main__":
    unittest.main()
