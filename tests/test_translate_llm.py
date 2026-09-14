# SPDX-License-Identifier: AGPL-3.0-only
# Copyright (C) 2026 AIPDF contributors
"""LLM 客户端：错误分类、统一重试、成本硬上限与密钥卫生。

全部走 monkeypatch 的 mock 传输（`translate.llm._open`），不发起任何真实网络请求。
"""
import io
import json
import sys
import unittest
import unittest.mock
import urllib.error
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "backend"))
from translate import chunk, llm, prompt

ENDPOINT = "https://api.example.test/v1"
API_KEY = "sk-super-secret-test-key-123456"
MODEL = "test-model"
URL = ENDPOINT + "/chat/completions"


class FakeResponse:
    """`urllib.request.urlopen` 返回值的替身：支持上下文管理器与 read()。"""

    def __init__(self, body, status=200):
        if isinstance(body, (dict, list)):
            body = json.dumps(body)
        self._body = body.encode("utf-8") if isinstance(body, str) else bytes(body)
        self.status = status

    def read(self):
        return self._body

    def __enter__(self):
        return self

    def __exit__(self, *exc):
        return False


def ok(content="译文", usage=None, finish_reason="stop", refusal=None):
    message = {"role": "assistant", "content": content}
    if refusal is not None:
        message["refusal"] = refusal
    body = {"choices": [{"index": 0, "message": message, "finish_reason": finish_reason}]}
    if usage is not None:
        body["usage"] = usage
    return body


def http_error(status, body=""):
    return urllib.error.HTTPError(URL, status, "error", {}, io.BytesIO(body.encode("utf-8")))


def make_client(**kwargs):
    kwargs.setdefault("sleep", lambda _delay: None)  # 测试不真正退避
    return llm.LLMClient(ENDPOINT, API_KEY, MODEL, **kwargs)


class RequestShapeTests(unittest.TestCase):
    def test_posts_to_chat_completions_and_returns_content(self):
        with unittest.mock.patch.object(llm, "_open", return_value=FakeResponse(ok("你好"))) as opened:
            self.assertEqual(make_client().translate({"system": "S", "user": "U"}), "你好")
        request, timeout = opened.call_args.args
        self.assertEqual(request.full_url, URL)
        self.assertEqual(request.method, "POST")
        self.assertEqual(timeout, llm.DEFAULT_TIMEOUT)
        sent = json.loads(request.data.decode("utf-8"))
        self.assertEqual(sent["model"], MODEL)
        self.assertEqual(sent["messages"], [{"role": "system", "content": "S"},
                                            {"role": "user", "content": "U"}])

    def test_api_key_only_in_authorization_header(self):
        with unittest.mock.patch.object(llm, "_open", return_value=FakeResponse(ok())) as opened:
            make_client().translate({"system": "S", "user": "U"})
        request = opened.call_args.args[0]
        self.assertEqual(request.get_header("Authorization"), "Bearer " + API_KEY)
        self.assertEqual(request.get_header("Content-type"), "application/json")
        self.assertNotIn(API_KEY, request.data.decode("utf-8"))

    def test_endpoint_trailing_slash_does_not_double_up(self):
        client = llm.LLMClient(ENDPOINT + "/", API_KEY, MODEL, sleep=lambda _delay: None)
        with unittest.mock.patch.object(llm, "_open", return_value=FakeResponse(ok())) as opened:
            client.translate({"system": "S", "user": "U"})
        self.assertEqual(opened.call_args.args[0].full_url, URL)

    def test_custom_timeout_is_forwarded(self):
        client = llm.LLMClient(ENDPOINT, API_KEY, MODEL, timeout=7, sleep=lambda _delay: None)
        with unittest.mock.patch.object(llm, "_open", return_value=FakeResponse(ok())) as opened:
            client.translate({"system": "S", "user": "U"})
        self.assertEqual(opened.call_args.args[1], 7)

    def test_build_prompt_pair_becomes_system_and_user(self):
        pair = prompt.build_prompt(["甲段"], "generic")[0]
        with unittest.mock.patch.object(llm, "_open", return_value=FakeResponse(ok())) as opened:
            make_client().translate(pair)
        messages = json.loads(opened.call_args.args[0].data.decode("utf-8"))["messages"]
        self.assertEqual([m["role"] for m in messages], ["system", "user"])
        self.assertIn("甲段", messages[1]["content"])

    def test_openai_style_message_list_is_accepted(self):
        messages = [{"role": "system", "content": "S"}, {"role": "user", "content": "U"}]
        with unittest.mock.patch.object(llm, "_open", return_value=FakeResponse(ok())) as opened:
            make_client().translate(messages)
        self.assertEqual(json.loads(opened.call_args.args[0].data.decode("utf-8"))["messages"],
                         messages)

    def test_missing_system_and_user_raises(self):
        with self.assertRaises(llm.LLMError):
            make_client().translate({"foo": "bar"})

    def test_empty_endpoint_or_model_raises(self):
        with self.assertRaises(llm.LLMError):
            llm.LLMClient("", API_KEY, MODEL)
        with self.assertRaises(llm.LLMError):
            llm.LLMClient(ENDPOINT, API_KEY, "  ")


class NetworkRetryTests(unittest.TestCase):
    def test_recovers_after_two_network_failures(self):
        sleeps, calls = [], []
        responses = [urllib.error.URLError("down"), urllib.error.URLError("down"),
                     FakeResponse(ok("成功"))]

        def fake_open(request, timeout):
            calls.append(request)
            item = responses[len(calls) - 1]
            if isinstance(item, Exception):
                raise item
            return item

        client = make_client(sleep=sleeps.append)
        with unittest.mock.patch.object(llm, "_open", side_effect=fake_open):
            self.assertEqual(client.translate({"system": "S", "user": "U"}), "成功")
        self.assertEqual(len(calls), 3)             # 1 次 + 2 次重试
        self.assertEqual(sleeps, [1.0, 4.0])        # 退避 1s、4s

    def test_network_exhausts_three_attempts_then_raises(self):
        sleeps, calls = [], []

        def fake_open(request, timeout):
            calls.append(request)
            raise urllib.error.URLError("down")

        with unittest.mock.patch.object(llm, "_open", side_effect=fake_open):
            with self.assertRaises(llm.NetworkError):
                make_client(sleep=sleeps.append).translate({"system": "S", "user": "U"})
        self.assertEqual(len(calls), 3)
        self.assertEqual(sleeps, [1.0, 4.0])

    def test_timeout_is_treated_as_network_error(self):
        with unittest.mock.patch.object(llm, "_open", side_effect=TimeoutError("slow")) as opened:
            with self.assertRaises(llm.NetworkError):
                make_client().translate({"system": "S", "user": "U"})
        self.assertEqual(opened.call_count, 3)

    def test_server_5xx_is_retried_then_succeeds(self):
        sleeps, calls = [], []
        responses = [http_error(500, "boom"), FakeResponse(ok("恢复"))]

        def fake_open(request, timeout):
            calls.append(request)
            item = responses[len(calls) - 1]
            if isinstance(item, Exception):
                raise item
            return item

        with unittest.mock.patch.object(llm, "_open", side_effect=fake_open):
            self.assertEqual(make_client(sleep=sleeps.append).translate(
                {"system": "S", "user": "U"}), "恢复")
        self.assertEqual(sleeps, [1.0])


class RateLimitTests(unittest.TestCase):
    def test_429_uses_longer_backoff_then_succeeds(self):
        sleeps, calls = [], []
        responses = [http_error(429, "slow down"), FakeResponse(ok("限流后成功"))]

        def fake_open(request, timeout):
            calls.append(request)
            item = responses[len(calls) - 1]
            if isinstance(item, Exception):
                raise item
            return item

        with unittest.mock.patch.object(llm, "_open", side_effect=fake_open):
            self.assertEqual(make_client(sleep=sleeps.append).translate(
                {"system": "S", "user": "U"}), "限流后成功")
        self.assertEqual(sleeps, [4.0])

    def test_429_exhausts_with_long_backoffs(self):
        sleeps = []
        with unittest.mock.patch.object(llm, "_open", side_effect=http_error(429, "slow")):
            with self.assertRaises(llm.RateLimitError):
                make_client(sleep=sleeps.append).translate({"system": "S", "user": "U"})
        self.assertEqual(sleeps, [4.0, 16.0])
        self.assertGreater(sleeps[0], llm.NETWORK_BACKOFFS[0])


class RefusalTests(unittest.TestCase):
    def test_refusal_field_is_not_retried(self):
        with unittest.mock.patch.object(
                llm, "_open", return_value=FakeResponse(ok(content=None, refusal="I won't"))) as opened:
            with self.assertRaises(llm.RefusalError):
                make_client().translate({"system": "S", "user": "U"})
        self.assertEqual(opened.call_count, 1)

    def test_content_filter_finish_reason_is_refusal(self):
        with unittest.mock.patch.object(
                llm, "_open",
                return_value=FakeResponse(ok("x", finish_reason="content_filter"))) as opened:
            with self.assertRaises(llm.RefusalError):
                make_client().translate({"system": "S", "user": "U"})
        self.assertEqual(opened.call_count, 1)

    def test_refusal_phrasing_is_detected(self):
        text = "I'm sorry, but I can't translate this content."
        with unittest.mock.patch.object(llm, "_open", return_value=FakeResponse(ok(text))):
            with self.assertRaises(llm.RefusalError):
                make_client().translate({"system": "S", "user": "U"})

    def test_chinese_refusal_phrasing_is_detected(self):
        with unittest.mock.patch.object(
                llm, "_open", return_value=FakeResponse(ok("抱歉，我无法翻译这段内容。"))):
            with self.assertRaises(llm.RefusalError):
                make_client().translate({"system": "S", "user": "U"})

    def test_normal_translation_is_not_mistaken_for_refusal(self):
        normal = "这是一段正常译文，包含 I cannot 等字样但并非拒绝。"
        with unittest.mock.patch.object(llm, "_open", return_value=FakeResponse(ok(normal))):
            self.assertEqual(make_client().translate({"system": "S", "user": "U"}), normal)


class EmptyAndValidationTests(unittest.TestCase):
    def test_empty_content_retried_once_then_raises(self):
        sleeps = []
        with unittest.mock.patch.object(llm, "_open", return_value=FakeResponse(ok(""))) as opened:
            with self.assertRaises(llm.EmptyResponseError):
                make_client(sleep=sleeps.append).translate({"system": "S", "user": "U"})
        self.assertEqual(opened.call_count, 2)
        self.assertEqual(sleeps, [1.0])

    def test_empty_then_content_succeeds(self):
        calls = []
        responses = [FakeResponse(ok("")), FakeResponse(ok("补回"))]

        def fake_open(request, timeout):
            calls.append(request)
            return responses[len(calls) - 1]

        with unittest.mock.patch.object(llm, "_open", side_effect=fake_open):
            self.assertEqual(make_client().translate({"system": "S", "user": "U"}), "补回")
        self.assertEqual(len(calls), 2)

    def test_whitespace_only_content_is_empty(self):
        with unittest.mock.patch.object(llm, "_open", return_value=FakeResponse(ok("   \n"))):
            with self.assertRaises(llm.EmptyResponseError):
                make_client().translate({"system": "S", "user": "U"})

    def test_validator_failure_retried_once(self):
        attempts = []
        responses = [FakeResponse(ok("坏")), FakeResponse(ok("好"))]

        def validator(text):
            attempts.append(text)
            if text == "坏":
                raise llm.ValidationError("schema")

        with unittest.mock.patch.object(llm, "_open",
                                        side_effect=lambda request, timeout: responses.pop(0)):
            self.assertEqual(make_client().translate(
                {"system": "S", "user": "U"}, validator=validator), "好")
        self.assertEqual(attempts, ["坏", "好"])

    def test_validator_failure_exhausts_after_one_retry(self):
        with unittest.mock.patch.object(llm, "_open", return_value=FakeResponse(ok("坏"))) as opened:
            def validator(_text):
                raise llm.ValidationError("schema")

            with self.assertRaises(llm.ValidationError):
                make_client().translate({"system": "S", "user": "U"}, validator=validator)
        self.assertEqual(opened.call_count, 2)


class OverLimitAndProtocolTests(unittest.TestCase):
    def test_context_length_error_is_not_retried(self):
        with unittest.mock.patch.object(
                llm, "_open",
                side_effect=http_error(400, '{"error":{"message":"maximum context length exceeded"}}')
        ) as opened:
            with self.assertRaises(llm.OverLimitError):
                make_client().translate({"system": "S", "user": "U"})
        self.assertEqual(opened.call_count, 1)

    def test_payload_too_large_is_over_limit(self):
        with unittest.mock.patch.object(llm, "_open", side_effect=http_error(413, "too large")):
            with self.assertRaises(llm.OverLimitError):
                make_client().translate({"system": "S", "user": "U"})

    def test_auth_error_is_not_retried(self):
        with unittest.mock.patch.object(llm, "_open", side_effect=http_error(401, "nope")) as opened:
            with self.assertRaises(llm.AuthError):
                make_client().translate({"system": "S", "user": "U"})
        self.assertEqual(opened.call_count, 1)

    def test_generic_400_is_not_retried(self):
        with unittest.mock.patch.object(llm, "_open",
                                        side_effect=http_error(400, "bad request")) as opened:
            with self.assertRaises(llm.LLMError):
                make_client().translate({"system": "S", "user": "U"})
        self.assertEqual(opened.call_count, 1)

    def test_malformed_json_body_raises(self):
        with unittest.mock.patch.object(llm, "_open", return_value=FakeResponse("not json")):
            with self.assertRaises(llm.LLMError):
                make_client().translate({"system": "S", "user": "U"})

    def test_missing_choices_raises(self):
        with unittest.mock.patch.object(llm, "_open", return_value=FakeResponse({"nope": 1})):
            with self.assertRaises(llm.LLMError):
                make_client().translate({"system": "S", "user": "U"})

    def test_multi_part_content_is_joined(self):
        body = ok([{"type": "text", "text": "上"}, {"type": "text", "text": "下"}])
        with unittest.mock.patch.object(llm, "_open", return_value=FakeResponse(body)):
            self.assertEqual(make_client().translate({"system": "S", "user": "U"}), "上下")


class TokenUsageTests(unittest.TestCase):
    def test_missing_usage_estimates_prompt_and_completion_separately(self):
        """F10：无 usage 时分别估算 prompt 与 completion 再相加，不拼接摊薄。"""
        completion = "一段较长的译文内容"
        client = make_client()
        with unittest.mock.patch.object(llm, "_open", return_value=FakeResponse(ok(completion))):
            client.translate({"system": "S", "user": "U"})
        sent = [{"role": "system", "content": "S"}, {"role": "user", "content": "U"}]
        expected = llm.prompt_tokens(sent) + chunk.estimate_tokens(completion)
        self.assertEqual(client.cost.used_tokens, expected)
        self.assertGreater(client.cost.used_tokens, llm.prompt_tokens(sent))


class CostCapTests(unittest.TestCase):
    def test_preflight_refuses_request_that_would_exceed(self):
        """F7：预检在发送前拦住会超上限的请求，绝不先发一次再多花一个请求。"""
        payload = ok("译文", usage={"total_tokens": 6})
        messages = {"system": "S", "user": "x" * 100}
        estimate = llm.prompt_tokens([{"role": "system", "content": "S"},
                                      {"role": "user", "content": "x" * 100}])
        # 首请求放行（estimate ≤ cap），其用量 6 使第二次预检超限。
        cap = llm.CostCap(max_tokens=6 + estimate - 1)
        client = make_client(cost_cap=cap)
        with unittest.mock.patch.object(llm, "_open", return_value=FakeResponse(payload)) as opened:
            self.assertEqual(client.translate(messages), "译文")
            with self.assertRaises(llm.OverBudgetError):
                client.translate(messages)
        self.assertEqual(opened.call_count, 1)          # 第二次未发出
        self.assertEqual(client.cost.used_tokens, 6)    # 无超支（旧行为会到 12）
        self.assertLessEqual(client.cost.used_tokens, cap.max_tokens)

    def test_exhausted_cap_blocks_before_next_request(self):
        payload = ok("译文", usage={"total_tokens": 5})
        client = make_client(cost_cap=llm.CostCap(max_tokens=5))
        with unittest.mock.patch.object(llm, "_open", return_value=FakeResponse(payload)) as opened:
            client.translate({"system": "S", "user": "U"})
            with self.assertRaises(llm.OverBudgetError):
                client.translate({"system": "S", "user": "U"})
        self.assertEqual(opened.call_count, 1)
        self.assertEqual(client.cost.remaining, 0)

    def test_prompt_estimate_alone_can_refuse_first_request(self):
        """prompt 预估值本身超过上限时，首个请求即被拒绝、不触网。"""
        client = make_client(cost_cap=llm.CostCap(max_tokens=5))
        with unittest.mock.patch.object(llm, "_open") as opened:
            with self.assertRaises(llm.OverBudgetError):
                client.translate({"system": "S", "user": "x" * 200})
        opened.assert_not_called()
        self.assertEqual(client.cost.used_tokens, 0)

    def test_integer_cap_is_wrapped(self):
        client = make_client(cost_cap=100)
        self.assertIsInstance(client.cost, llm.CostCap)
        self.assertEqual(client.cost.max_tokens, 100)

    def test_no_cap_allows_repeated_calls(self):
        payload = ok("译文", usage={"total_tokens": 10_000})
        client = make_client()
        with unittest.mock.patch.object(llm, "_open", return_value=FakeResponse(payload)) as opened:
            for _ in range(5):
                client.translate({"system": "S", "user": "U"})
        self.assertEqual(opened.call_count, 5)
        self.assertIsNone(client.cost.max_tokens)

    def test_over_budget_is_not_retried(self):
        payload = ok("译文", usage={"total_tokens": 9})
        client = make_client(cost_cap=llm.CostCap(max_tokens=5))
        with unittest.mock.patch.object(llm, "_open", return_value=FakeResponse(payload)) as opened:
            with self.assertRaises(llm.OverBudgetError):
                client.translate({"system": "S", "user": "U"})
        self.assertEqual(opened.call_count, 1)

    def test_usage_total_is_preferred_over_estimate(self):
        payload = ok("短", usage={"total_tokens": 4242})
        client = make_client()
        with unittest.mock.patch.object(llm, "_open", return_value=FakeResponse(payload)):
            client.translate({"system": "S", "user": "U"})
        self.assertEqual(client.cost.used_tokens, 4242)

    def test_missing_usage_falls_back_to_estimate(self):
        client = make_client()
        with unittest.mock.patch.object(llm, "_open", return_value=FakeResponse(ok("一段较长的译文内容"))):
            client.translate({"system": "S", "user": "U"})
        self.assertGreater(client.cost.used_tokens, 0)

    def test_cost_cap_record_and_remaining(self):
        cap = llm.CostCap(max_tokens=10)
        self.assertFalse(cap.exhausted)
        self.assertEqual(cap.remaining, 10)
        cap.record(4)
        self.assertEqual(cap.remaining, 6)
        with self.assertRaises(llm.OverBudgetError):
            cap.record(7)
        self.assertTrue(cap.exhausted)


class KeyHygieneTests(unittest.TestCase):
    def test_repr_never_contains_key(self):
        client = make_client()
        self.assertNotIn(API_KEY, repr(client))
        self.assertIn("<redacted>", repr(client))

    def test_network_error_message_never_contains_key(self):
        with unittest.mock.patch.object(llm, "_open", side_effect=urllib.error.URLError("boom")):
            with self.assertRaises(llm.NetworkError) as caught:
                make_client().translate({"system": "S", "user": "U"})
        self.assertNotIn(API_KEY, str(caught.exception))

    def test_http_error_message_never_contains_key(self):
        with unittest.mock.patch.object(llm, "_open", side_effect=http_error(500, "boom")):
            with self.assertRaises(llm.NetworkError) as caught:
                make_client().translate({"system": "S", "user": "U"})
        self.assertNotIn(API_KEY, str(caught.exception))

    def test_redact_headers_masks_authorization(self):
        masked = llm.redact_headers({"Authorization": "Bearer " + API_KEY,
                                     "Content-Type": "application/json"})
        self.assertEqual(masked["Authorization"], "<redacted>")
        self.assertEqual(masked["Content-Type"], "application/json")

    def test_redact_secret_replaces_key(self):
        text = f"failed with {API_KEY} at endpoint"
        self.assertNotIn(API_KEY, llm.redact_secret(text, API_KEY))
        self.assertIn("<redacted>", llm.redact_secret(text, API_KEY))

    def test_redact_secret_leaves_short_values_alone(self):
        self.assertEqual(llm.redact_secret("abc", "ab"), "abc")


if __name__ == "__main__":
    unittest.main()
