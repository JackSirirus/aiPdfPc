# SPDX-License-Identifier: AGPL-3.0-only
# Copyright (C) 2026 AIPDF contributors
"""翻译编排端到端（T10）：engine.dispatch 的 `translate` 操作。

抽取 → 分块 → prompt → LLM → 校验 → 回填 → mono/dual 输出全链路走通。所有 LLM
调用都经 monkeypatch 的 `translate.llm._open`，不发起任何真实网络请求；API Key
只从 stdin JSON 进入，本测试同时校验它不进入 argv/环境/请求正文。
"""
import hashlib
import json
import os
import sys
import tempfile
import unittest
import unittest.mock
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "backend"))
import engine
import pymupdf as fitz
from translate import llm

ENDPOINT = "https://api.example.test/v1"
API_KEY = "sk-orchestrator-secret-key-123456"
MODEL = "test-model"
TRANSLATION = "你好世界"


class FakeResponse:
    """`urllib.request.urlopen` 返回值的替身：支持上下文管理器与 read()。"""

    def __init__(self, body):
        self._body = json.dumps(body).encode("utf-8")

    def read(self):
        return self._body

    def __enter__(self):
        return self

    def __exit__(self, *exc):
        return False


def response_body(text=TRANSLATION):
    """一条 OpenAI 兼容响应；message.content 是逐段译文 JSON 数组。"""
    content = json.dumps([{"index": 0, "text": text}])
    return {"choices": [{"index": 0, "message": {"role": "assistant", "content": content},
                         "finish_reason": "stop"}]}


class TranslateOrchestratorTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.temp = tempfile.TemporaryDirectory(prefix="aipdf-translate-")
        cls.root = Path(cls.temp.name)
        cls.source = cls._make_pdf(cls.root / "hello.pdf", "Hello world")

    @classmethod
    def tearDownClass(cls):
        cls.temp.cleanup()

    @staticmethod
    def _make_pdf(path, text):
        doc = fitz.open()
        page = doc.new_page(width=595, height=842)
        page.insert_text((72, 100), text, fontsize=12)
        doc.save(path)
        doc.close()
        return path

    def dispatch(self, files=None, options=None, output_dir=None):
        request = {"operation": "translate", "files": [str(p) for p in (files or [self.source])],
                   "options": options or {}, "outputDir": str(output_dir or self.root)}
        return engine.dispatch(request)

    def default_options(self, **overrides):
        options = {"apiKey": API_KEY, "endpoint": ENDPOINT, "model": MODEL,
                   "targetLang": "zh", "sourceLang": "auto", "template": "generic"}
        options.update(overrides)
        return options

    def test_end_to_end_mono_and_dual(self):
        captured = []

        def fake_open(request, timeout):
            captured.append(request)
            return FakeResponse(response_body())

        with unittest.mock.patch.object(llm, "_open", side_effect=fake_open):
            result = self.dispatch(options=self.default_options())

        self.assertTrue(result["ok"])
        outputs = [Path(p) for p in result["outputs"]]
        self.assertEqual(len(outputs), 2)
        mono = next(p for p in outputs if p.name.endswith("-纯译文.pdf"))
        dual = next(p for p in outputs if p.name.endswith("-对照.pdf"))
        self.assertTrue(mono.is_file() and dual.is_file())

        with fitz.open(str(mono)) as doc:
            self.assertEqual(len(doc), 1)
            text = doc[0].get_text()
            self.assertIn(TRANSLATION, text)      # 译文真实文本、可搜索
            self.assertNotIn("Hello world", text)  # 原文被真实移除，非黑框遮盖
        with fitz.open(str(dual)) as doc:
            self.assertEqual(len(doc), 2)          # 原页 N + 译页 N

        # 报告 sidecar 与产物同目录落盘，且只含页码/原因/可搜索性。
        reports = list(Path(result["outputDir"]).glob("*-降级报告.json"))
        self.assertEqual(len(reports), 1)
        report = json.loads(reports[0].read_text(encoding="utf-8"))
        self.assertEqual(report["summary"]["total_pages"], 1)
        self.assertEqual(result["results"][0]["report"]["summary"]["total_pages"], 1)

        # 源文件保持只读不变。
        with fitz.open(str(self.source)) as doc:
            self.assertIn("Hello world", doc[0].get_text())

        # 密钥卫生：只出现在 Authorization 头，绝不进入请求正文/环境/argv。
        self.assertTrue(captured)
        sent = captured[0]
        self.assertEqual(sent.get_header("Authorization"), "Bearer " + API_KEY)
        self.assertNotIn(API_KEY, sent.data.decode("utf-8"))
        self.assertNotIn(API_KEY, " ".join(sys.argv))
        self.assertNotIn(API_KEY, " ".join(os.environ.values()))

    def test_mono_only_format(self):
        with unittest.mock.patch.object(llm, "_open", return_value=FakeResponse(response_body())):
            result = self.dispatch(options=self.default_options(format="mono"))
        self.assertTrue(result["ok"])
        self.assertEqual(len(result["outputs"]), 1)
        self.assertTrue(result["outputs"][0].endswith("-纯译文.pdf"))

    def test_batch_translates_multiple_files(self):
        second = self._make_pdf(self.root / "second.pdf", "Second document")
        with unittest.mock.patch.object(llm, "_open", return_value=FakeResponse(response_body())):
            result = self.dispatch(files=[self.source, second],
                                   options=self.default_options(format="mono"))
        self.assertTrue(result["ok"])
        self.assertEqual(len(result["outputs"]), 2)
        self.assertEqual(len(result["results"]), 2)

    def test_missing_config_is_reported(self):
        with self.assertRaisesRegex(engine.UserError, "翻译服务端点"):
            self.dispatch(options={"apiKey": API_KEY, "model": MODEL})
        with self.assertRaisesRegex(engine.UserError, "翻译模型"):
            self.dispatch(options={"apiKey": API_KEY, "endpoint": ENDPOINT})
        with self.assertRaisesRegex(engine.UserError, "API Key"):
            self.dispatch(options={"endpoint": ENDPOINT, "model": MODEL})

    def test_invalid_output_directory_is_rejected(self):
        with self.assertRaisesRegex(engine.UserError, "输出文件夹"):
            self.dispatch(options=self.default_options(), output_dir=self.root / "missing")

    def test_non_pdf_input_is_rejected(self):
        bad = self.root / "note.txt"
        bad.write_text("not a pdf", encoding="utf-8")
        with self.assertRaisesRegex(engine.UserError, "格式不适用于此工具"):
            self.dispatch(files=[bad], options=self.default_options())

    def test_model_refusal_degrades_to_original(self):
        # 模型返回非译文元描述 → 校验失败 → 该段保留原文并标 untranslated_echo。
        refusal = "I cannot translate this."
        with unittest.mock.patch.object(llm, "_open",
                                        return_value=FakeResponse(response_body(refusal))):
            result = self.dispatch(options=self.default_options(format="mono"))
        self.assertTrue(result["ok"])
        with fitz.open(result["outputs"][0]) as doc:
            self.assertIn("Hello world", doc[0].get_text())  # 保留原文
        reasons = {reason for page in result["results"][0]["report"]["pages"]
                   for reason in page["reasons"]}
        self.assertIn("untranslated_echo", reasons)


if __name__ == "__main__":
    unittest.main()
