# SPDX-License-Identifier: AGPL-3.0-only
# Copyright (C) 2026 AIPDF contributors
"""翻译供应商 → 引擎请求目的地/协议 端到端集成（v1.2 T17）。

覆盖计划 §T17 的引擎侧链路：面板装配出的 options（endpoint / model / apiProtocol / apiKey）
→ `engine.translate_text_request`（划词）与 `engine.translate_request`（整篇）
→ `translate.llm.LLMClient` 传输层。

断言口径（与 C# 装配侧 `TranslationEngineOptions.Build` 同契约）：
- `apiProtocol="anthropic"` → `POST {base}/v1/messages` + `x-api-key` + `anthropic-version`，
  body 为 `{model, max_tokens, system?, messages}`（system 在顶层，messages 只含 user/assistant）；
- 缺省 / 未知协议 → `POST {base}/chat/completions` + `Authorization: Bearer`（与 v1.1 逐字一致）；
- 端点仍以装配侧原样传入（Anthropic base 不含 /v1，由引擎层拼 /v1/messages）。

边界（全部 mock，不发真实网络请求；密钥均为可识别假值）：
- 本地 loopback 空密钥不被引擎拦截，且不携带任何密钥材料；
- 协议不匹配（Anthropic base + openai 协议 → /chat/completions 被 404）非静默呈现；
- 网络超时按 llm.py 既有策略重试（共 3 次尝试）后转可读错误，错误文本不含密钥。
"""
import io
import json
import sys
import tempfile
import unittest
import unittest.mock
import urllib.error
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "backend"))
import engine
import pymupdf as fitz
from translate import llm

# 与 C# 预设表（TranslationProviderPreset）同源的端点：
#   kimi-code / glm-coding-plan 的 Anthropic base 不含 /v1（引擎拼 /v1/messages）；
#   openai 的 OpenAI 兼容 base 含 /v1；本地 llama.cpp 默认端口 8080。
KIMI_CODE_BASE = "https://api.kimi.com/coding"
GLM_ANTHROPIC_BASE = "https://open.bigmodel.cn/api/anthropic"
OPENAI_BASE = "https://api.openai.com/v1"
LOCAL_LLAMA_CPP_BASE = "http://127.0.0.1:8080/v1"

MODEL = "k3"
GLM_MODEL = "glm-5.3"
API_KEY = "sk-integration-test-key-4e7a"
SOURCE_TEXT = "Hello world"
TRANSLATION = "你好世界"


class FakeResponse:
    """`urllib.request.urlopen` 返回值的替身：支持上下文管理器与 read()。"""

    def __init__(self, body):
        if isinstance(body, (dict, list)):
            body = json.dumps(body)
        self._body = body.encode("utf-8") if isinstance(body, str) else bytes(body)

    def read(self):
        return self._body

    def __enter__(self):
        return self

    def __exit__(self, *exc):
        return False


def openai_response(text=TRANSLATION):
    """OpenAI 兼容响应：content 是逐段译文 JSON 数组（引擎 schema 校验要求 1 段）。"""
    content = json.dumps([{"index": 0, "text": text}], ensure_ascii=False)
    return {"choices": [{"index": 0, "message": {"role": "assistant", "content": content},
                         "finish_reason": "stop"}]}


def anthropic_response(text=TRANSLATION):
    """Anthropic Messages 响应：文本块内容同样是逐段译文 JSON 数组。"""
    content = json.dumps([{"index": 0, "text": text}], ensure_ascii=False)
    return {"content": [{"type": "text", "text": content}],
            "usage": {"input_tokens": 10, "output_tokens": 5},
            "stop_reason": "end_turn"}


def text_options(**overrides):
    """划词请求的 options（等价于 C# SelectionTranslation 装配出的字典）。"""
    options = {
        "text": SOURCE_TEXT,
        "endpoint": OPENAI_BASE,
        "model": MODEL,
        "apiKey": API_KEY,
        "sourceLang": "auto",
        "targetLang": "zh",
        "template": "generic",
    }
    options.update(overrides)
    return options


class ProtocolRoutingIntegrationTests(unittest.TestCase):
    """面板选择（协议）→ 引擎最终 method/path/header/body 的端到端一致性。"""

    def test_translate_text_anthropic_reaches_v1_messages(self):
        """kimi-code（Anthropic）全链路：/v1/messages + x-api-key + anthropic-version。"""
        captured = []

        def fake_open(request, timeout):
            captured.append(request)
            return FakeResponse(anthropic_response())

        with unittest.mock.patch.object(llm, "_open", side_effect=fake_open):
            result = engine.translate_text_request(
                {"options": text_options(endpoint=KIMI_CODE_BASE, apiProtocol="anthropic")})

        self.assertTrue(result["ok"])
        self.assertEqual(TRANSLATION, result["translation"])

        request = captured[0]
        self.assertEqual("POST", request.method)
        self.assertEqual(KIMI_CODE_BASE + "/v1/messages", request.full_url)
        headers = {name.lower(): value for name, value in request.header_items()}
        self.assertEqual(API_KEY, headers["x-api-key"])
        self.assertEqual("2023-06-01", headers["anthropic-version"])
        self.assertEqual("application/json", headers["content-type"])
        self.assertNotIn("authorization", headers)

        body = json.loads(request.data.decode("utf-8"))
        self.assertEqual(MODEL, body["model"])
        self.assertIsInstance(body["max_tokens"], int)
        self.assertGreater(body["max_tokens"], 0)
        self.assertIn("system", body)
        self.assertEqual(["user"], [item["role"] for item in body["messages"]])
        self.assertNotIn(API_KEY, request.data.decode("utf-8"))

    def test_translate_text_default_protocol_reaches_chat_completions(self):
        """缺省 apiProtocol → OpenAI 兼容路径与 v1.1 行为逐字一致。"""
        captured = []

        def fake_open(request, timeout):
            captured.append(request)
            return FakeResponse(openai_response())

        with unittest.mock.patch.object(llm, "_open", side_effect=fake_open):
            result = engine.translate_text_request({"options": text_options(endpoint=OPENAI_BASE)})

        self.assertTrue(result["ok"])
        request = captured[0]
        self.assertEqual(OPENAI_BASE + "/chat/completions", request.full_url)
        headers = {name.lower(): value for name, value in request.header_items()}
        self.assertEqual("Bearer " + API_KEY, headers["authorization"])
        self.assertNotIn("anthropic-version", headers)

        body = json.loads(request.data.decode("utf-8"))
        self.assertEqual(MODEL, body["model"])
        self.assertEqual(2, len(body["messages"]))
        self.assertNotIn(API_KEY, request.data.decode("utf-8"))

    def test_translate_request_full_document_anthropic_reaches_v1_messages(self):
        """整篇翻译（PDF → 产物）同样读取 apiProtocol 并打 Anthropic 端点。"""
        captured = []

        def fake_open(request, timeout):
            captured.append(request)
            return FakeResponse(anthropic_response())

        with tempfile.TemporaryDirectory(prefix="aipdf-provider-e2e-") as temp:
            root = Path(temp)
            source = root / "hello.pdf"
            doc = fitz.open()
            page = doc.new_page(width=595, height=842)
            page.insert_text((72, 100), SOURCE_TEXT, fontsize=12)
            doc.save(source)
            doc.close()

            request = {
                "operation": "translate",
                "files": [str(source)],
                "outputDir": str(root),
                "options": {
                    "endpoint": GLM_ANTHROPIC_BASE,
                    "model": GLM_MODEL,
                    "apiKey": API_KEY,
                    "apiProtocol": "anthropic",
                    "sourceLang": "auto",
                    "targetLang": "zh",
                    "template": "generic",
                    "format": "mono",
                },
            }
            with unittest.mock.patch.object(llm, "_open", side_effect=fake_open):
                result = engine.translate_request(request)

        self.assertTrue(result["ok"], result.get("error"))
        self.assertGreaterEqual(len(captured), 1)
        for sent in captured:
            self.assertEqual("POST", sent.method)
            self.assertTrue(sent.full_url.endswith("/v1/messages"),
                            f"整篇翻译必须打 /v1/messages，实际：{sent.full_url}")
            headers = {name.lower(): value for name, value in sent.header_items()}
            self.assertEqual(API_KEY, headers["x-api-key"])
            self.assertNotIn("authorization", headers)

    def test_local_loopback_empty_key_is_not_blocked(self):
        """本地 loopback（llama.cpp:8080）无密钥：引擎放行，且不携带任何密钥材料。"""
        self.assertTrue(engine._is_loopback_endpoint(LOCAL_LLAMA_CPP_BASE))

        captured = []

        def fake_open(request, timeout):
            captured.append(request)
            return FakeResponse(openai_response())

        with unittest.mock.patch.object(llm, "_open", side_effect=fake_open):
            result = engine.translate_text_request(
                {"options": text_options(endpoint=LOCAL_LLAMA_CPP_BASE, apiKey="")})

        self.assertTrue(result["ok"])
        request = captured[0]
        self.assertEqual(LOCAL_LLAMA_CPP_BASE + "/chat/completions", request.full_url)
        # 空密钥放行是既有 loopback 规则；llm.py 写入空 Bearer，不含任何真实密钥材料。
        authorization = request.get_header("Authorization") or ""
        self.assertEqual("Bearer ", authorization)
        self.assertNotIn("sk-", authorization)


class ProtocolBoundaryIntegrationTests(unittest.TestCase):
    """协议不匹配 / 超时重试：mock 传输层，断言尝试次数与可读错误。"""

    def test_protocol_mismatch_surfaces_error_without_silent_success(self):
        """Anthropic base + openai 协议 → 引擎打 /chat/completions，服务端 404。

        通用 HTTP 错误在 `_translate_one_segment` 按规格重译一次后降级：
        结果非静默（degraded + untranslated_echo），且 URL 形状证明协议不匹配。
        """
        captured = []

        def fake_open(request, timeout):
            captured.append(request)
            raise urllib.error.HTTPError(
                request.full_url, 404, "not found", {}, io.BytesIO(b'{"detail":"not found"}'))

        with unittest.mock.patch.object(llm, "_open", side_effect=fake_open):
            result = engine.translate_text_request(
                {"options": text_options(endpoint=GLM_ANTHROPIC_BASE, apiProtocol="openai")})

        # 404 是确定性答案：llm 层不重试；引擎按规格重译一次 → 共 2 个请求。
        self.assertEqual(2, len(captured))
        for request in captured:
            self.assertEqual(GLM_ANTHROPIC_BASE + "/chat/completions", request.full_url)

        self.assertTrue(result["ok"])
        self.assertTrue(result["degraded"])
        self.assertEqual("", result["translation"])
        self.assertIn("untranslated_echo", result["reasons"])
        self.assertNotIn(API_KEY, json.dumps(result, ensure_ascii=False))

    def test_timeout_retries_exactly_three_attempts_then_user_error(self):
        """网络超时按既有策略重试（1 次 + 2 次重试 = 3 次），再转可读错误。"""
        calls = []

        def fake_open(request, timeout):
            calls.append(request)
            raise TimeoutError("模拟超时")

        with unittest.mock.patch.object(llm, "_open", side_effect=fake_open):
            with self.assertRaises(engine.UserError) as caught:
                engine.translate_text_request({"options": text_options()})

        expected_attempts = len(llm.NETWORK_BACKOFFS) + 1
        self.assertEqual(3, expected_attempts)
        self.assertEqual(expected_attempts, len(calls))
        self.assertIn("无法连接翻译服务", str(caught.exception))
        self.assertNotIn(API_KEY, str(caught.exception))


if __name__ == "__main__":
    unittest.main()
