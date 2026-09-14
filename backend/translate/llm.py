# SPDX-License-Identifier: AGPL-3.0-only
# Copyright (C) 2026 AIPDF contributors
"""OpenAI 兼容 LLM 客户端：错误分类、统一重试与成本硬上限（翻译引擎 T5）。

只做一件事：把 `prompt.build_prompt()` 生成的 system/user 消息 POST 到
`{endpoint}/chat/completions`，取回 `choices[0].message.content`。围绕这一次调用叠加：

- **错误分类**：网络 / 限流 / 拒译 / 空响应 / 超上下文 / 校验失败 / 中止 / 成本超限各自对应
  一个异常类型，让编排层（T10）与界面能给出正确处置（见《需求总结》§错误分类与重试策略）。
- **统一重试**：网络退避 1s、4s（最多 2 次重试，共 3 次尝试）；429 用更长退避；空响应与
  校验失败各只重试 1 次；拒译、超上下文、中止、成本超限一律不重试。
- **成本硬上限**：每次请求前用保守估算**预检** prompt token，`已用量 + 估算 > 上限` 即抛
  `OverBudgetError`，因此不会先发一次再超支一个请求（F7）。用量优先取服务商 `usage`，
  缺失时分别估算 prompt 与 completion 再相加（F10），绝不静默继续。
- **密钥卫生**：`api_key` 只从构造参数进入；本模块不读环境变量 / argv / 文件，不写日志，
  不落盘，不把密钥放进异常信息；诊断用 `redact_headers` / `redact_secret` 脱敏。
- **取消**：本层不做 Python 级取消。应用以**进程终止**（SIGTERM/SIGINT，宿主
  `engine.stop()`）取消整次任务，并给每个 HTTP 请求设 `DEFAULT_TIMEOUT` 秒的 socket
  超时兜底，避免服务端不响应时无限挂起。`AbortedError` 仅为宿主显式中止保留，本层
  自身不再设置取消事件（F6）。

仅依赖标准库（`urllib.request`），不引入 httpx/requests。
"""
from __future__ import annotations

import json
import re
import time
import urllib.error
import urllib.request

try:  # 作为包导入（tests 走 backend 顶层 → translate.llm）
    from .chunk import estimate_tokens
except ImportError:  # 直接以 backend 为 sys.path 顶层加载时
    from chunk import estimate_tokens


# —— 错误分类 ---------------------------------------------------------------

class LLMError(Exception):
    """翻译调用相关的基类错误。协议/配置类错误不可重试。"""


class NetworkError(LLMError):
    """连接失败 / DNS / TLS / 超时，以及 5xx、408 等服务端瞬时错误。可重试。"""


class RateLimitError(LLMError):
    """HTTP 429 / 额度或频率限制。可重试，退避更长。"""


class AuthError(LLMError):
    """HTTP 401/403：凭据被拒。不可重试，需用户在设置中修正 API Key。"""


class RefusalError(LLMError):
    """模型明确拒译（refusal 字段 / content_filter / 拒译文案）。不可重试，改模板或模型。"""


class EmptyResponseError(LLMError):
    """返回空内容。可疑，最多重试 1 次。"""


class OverLimitError(LLMError):
    """请求超出模型上下文/token 上限。本层不可重试，由调用方重新分块。"""


class AbortedError(LLMError):
    """用户取消 / 撤销同意。不可重试。

    本层自身不再设置 Python 级取消事件（F6）：应用以进程终止取消整次任务，宿主
    `engine.stop()` 会直接抛出可辨识的 `CancelledError` 中断链路。保留该类型供宿主
    显式中止与既有异常分类使用。
    """


class ValidationError(LLMError, ValueError):
    """输出 schema 不合法（如校验回调发现段落数不匹配）。最多重试 1 次。"""


class OverBudgetError(LLMError):
    """估算 token 超过 `CostCap.max_tokens`。不可重试，停止后续请求。"""


# —— 重试策略（统一文案与计数，见需求总结 §错误分类与重试策略）-----------------

# 网络：退避 1s、4s，最多 2 次重试（共 3 次尝试）。
NETWORK_BACKOFFS = (1.0, 4.0)
# 限流：更长退避。
RATE_LIMIT_BACKOFFS = (4.0, 16.0)
# 空响应 / 校验失败：各只重试 1 次。
EMPTY_BACKOFFS = (1.0,)
VALIDATION_BACKOFFS = (1.0,)

_RETRY_POLICY = {
    NetworkError: (len(NETWORK_BACKOFFS), NETWORK_BACKOFFS),
    RateLimitError: (len(RATE_LIMIT_BACKOFFS), RATE_LIMIT_BACKOFFS),
    EmptyResponseError: (len(EMPTY_BACKOFFS), EMPTY_BACKOFFS),
    ValidationError: (len(VALIDATION_BACKOFFS), VALIDATION_BACKOFFS),
}

# 单次 HTTP 请求的 socket 超时（秒，连接 + 读取）。F6：进程终止取消之外的必要兜底，
# 避免服务端不响应时无界挂起；一次 translate 最多尝试 3 次，最坏约 3×此值。
DEFAULT_TIMEOUT = 30.0

_NETWORK_MESSAGE = "无法连接翻译服务，请检查网络或服务端点后重试。"
_RATE_LIMIT_MESSAGE = "翻译服务限流（HTTP 429），请稍后重试或更换服务商。"
_REFUSAL_MESSAGE = "模型拒绝翻译这段内容。请更换 prompt 模板或模型后重试。"
_EMPTY_MESSAGE = "翻译服务返回了空内容。"
_BUDGET_MESSAGE = "已达成本硬上限（{limit} tokens），停止后续翻译请求。"
_OVER_LIMIT_MESSAGE = "请求超出模型上下文上限，请减小分段或更换模型。"
_AUTH_MESSAGE = "翻译服务拒绝了凭据（HTTP {status}）。请在设置中检查 API Key。"
_SERVER_MESSAGE = "翻译服务暂时不可用（HTTP {status}），请稍后重试。"
_GENERIC_HTTP_MESSAGE = "翻译服务返回错误（HTTP {status}）。"
_MALFORMED_MESSAGE = "翻译服务返回了无法解析的响应。"
_BAD_REQUEST_MESSAGE = "翻译请求消息格式不正确。"

# 超上下文判定：服务商错误报文里出现这些标记即判为 over-limit（非网络错误）。
_OVER_LIMIT_MARKERS = (
    "context length", "context_length_exceeded", "maximum context", "max context",
    "too many tokens", "token limit", "reduce the length", "prompt is too long",
    "input is too long", "context window", "超出最大", "上下文长度", "过长",
)

# 拒译文案：仅匹配开头的元描述，避免误伤正常译文。
_REFUSAL_PATTERNS = (
    re.compile(r"^\s*i\s+(?:cannot|can't|can not|am unable to|am not able to|won't)\s+"
               r"(?:translate|assist|help|comply|fulfill|process)", re.I),
    re.compile(r"^\s*i'm sorry[,.]?\s*(?:but\s+)?i\s+(?:cannot|can't|am unable)", re.I),
    re.compile(r"^\s*as an ai\s+(?:language\s+)?model", re.I),
    re.compile(r"^\s*(?:抱歉|对不起)[，,。 ]*(?:我)?(?:无法|不能|没法)(?:翻译|协助|处理|完成)", re.I),
    re.compile(r"^\s*作为(?:一个)?\s*ai", re.I),
)

# 诊断输出里需要脱敏的请求头。
_SENSITIVE_HEADERS = {"authorization", "proxy-authorization", "api-key", "x-api-key"}
_REDACTED = "<redacted>"


# —— 密钥脱敏 ---------------------------------------------------------------

def redact_headers(headers):
    """返回脱敏后的 header 副本：Authorization 等敏感头以固定占位替换。

    本模块从不把密钥写进日志或异常；此函数供编排层记录请求诊断时复用。
    """
    redacted = {}
    for name, value in dict(headers or {}).items():
        redacted[name] = _REDACTED if str(name).lower() in _SENSITIVE_HEADERS else value
    return redacted


def redact_secret(text, secret):
    """把 `text` 中出现的密钥替换为占位符；密钥过短时不做替换以免误伤普通文本。"""
    text = "" if text is None else str(text)
    secret = str(secret or "")
    if len(secret) < 4:
        return text
    return text.replace(secret, _REDACTED)


# —— 成本硬上限 -------------------------------------------------------------

class CostCap:
    """单任务 token 硬上限。`max_tokens=None` 表示不限额。

    `ensure_available(estimated)` 在每次请求前调用：`已用量 + 估算 > 上限` 即拒绝，
    因此不会先发一次再超支一个请求（F7，预检）。`record()` 在请求后累加实际用量，
    一旦超过上限立即抛 `OverBudgetError`。调用方据此停止后续请求并转「成本超限」。
    """

    def __init__(self, max_tokens=None):
        self.max_tokens = None if max_tokens is None else max(0, int(max_tokens))
        self.used_tokens = 0

    @property
    def exhausted(self):
        return self.max_tokens is not None and self.used_tokens >= self.max_tokens

    @property
    def remaining(self):
        if self.max_tokens is None:
            return None
        return max(0, self.max_tokens - self.used_tokens)

    def ensure_available(self, estimated=0):
        """请求前预检：已用量已达上限，或再加上本次估算会超上限，即拒绝。"""
        if self.exhausted:
            raise OverBudgetError(_BUDGET_MESSAGE.format(limit=self.max_tokens))
        if (self.max_tokens is not None
                and self.used_tokens + max(0, int(estimated or 0)) > self.max_tokens):
            raise OverBudgetError(_BUDGET_MESSAGE.format(limit=self.max_tokens))

    def record(self, tokens):
        self.used_tokens += max(0, int(tokens or 0))
        if self.max_tokens is not None and self.used_tokens > self.max_tokens:
            raise OverBudgetError(_BUDGET_MESSAGE.format(limit=self.max_tokens))


# —— 传输层 ----------------------------------------------------------------

def _open(request, timeout):
    """实际发出 HTTP 请求。测试通过 monkeypatch 本函数注入 mock 传输，避免真实联网。"""
    return urllib.request.urlopen(request, timeout=timeout)


class LLMClient:
    """OpenAI 兼容 `chat/completions` 客户端。

    `api_key` 只从构造参数进入（调用方经 stdin 拿到后传入）；本类不读环境变量 / argv /
    文件。`endpoint` 是 API 基址（如 `https://host/v1`），调用时拼接 `/chat/completions`。
    `cost_cap` 可传 `CostCap` 实例或直接传一个整数 token 上限。
    """

    def __init__(self, endpoint, api_key, model, timeout=DEFAULT_TIMEOUT, cost_cap=None,
                 sleep=time.sleep):
        if not str(endpoint or "").strip():
            raise LLMError("未配置翻译服务端点。")
        if not str(model or "").strip():
            raise LLMError("未配置翻译模型。")
        self.endpoint = str(endpoint).strip()
        self.api_key = str(api_key or "")
        self.model = str(model).strip()
        self.timeout = timeout
        self.cost = cost_cap if isinstance(cost_cap, CostCap) else CostCap(cost_cap)
        self._sleep = sleep

    def __repr__(self):
        # 密钥永不进入 repr；endpoint/model 不是秘密。
        return (f"LLMClient(endpoint={self.endpoint!r}, model={self.model!r}, "
                f"api_key={_REDACTED})")

    def translate(self, prompt_messages, validator=None):
        """翻译一组消息，返回 `choices[0].message.content`。

        `prompt_messages` 接受 `prompt.build_prompt()` 产出的 `{"system", "user"}` 对，或
        OpenAI 风格的 `[{"role", "content"}, ...]` 列表。`validator` 可选：收到结果后调用，
        发现 schema 不符应抛 `ValidationError`（本层会重试 1 次）。每个请求发送前先做
        成本预检（F7）。取消由宿主进程终止完成，见模块 docstring（F6）。
        """
        messages = _normalize_messages(prompt_messages)
        retries = {}
        while True:
            self.cost.ensure_available(prompt_tokens(messages))
            try:
                payload = self._post(messages)
                self.cost.record(_token_usage(payload, messages))
                content = _extract_content(payload)
                if validator is not None:
                    validator(content)
                return content
            except (NetworkError, RateLimitError, EmptyResponseError, ValidationError) as error:
                delay = _next_delay(error, retries)
                if delay is None:
                    raise
                self._sleep(delay)

    def _post(self, messages):
        """POST 一次请求并返回解析后的 JSON；失败按类别抛出对应异常。"""
        body = json.dumps({"model": self.model, "messages": messages},
                          ensure_ascii=False).encode("utf-8")
        request = urllib.request.Request(
            self.endpoint.rstrip("/") + "/chat/completions", data=body, method="POST")
        request.add_header("Content-Type", "application/json")
        request.add_header("Authorization", "Bearer " + self.api_key)
        request.add_header("Accept", "application/json")
        try:
            with _open(request, self.timeout) as response:
                raw = response.read()
        except urllib.error.HTTPError as error:
            # HTTPError 在 3.12+ 是临时文件包装器：读完报文必须关闭，否则泄漏 fd。
            try:
                classified = _classify_http_error(error)
            finally:
                error.close()
            raise self._redact_error(classified) from error
        except (urllib.error.URLError, OSError) as error:
            raise NetworkError(self._redact(_NETWORK_MESSAGE)) from error
        try:
            text = raw.decode("utf-8", "replace") if isinstance(raw, bytes) else str(raw)
            return json.loads(text)
        except ValueError as error:
            raise LLMError(self._redact(_MALFORMED_MESSAGE)) from error

    def _redact(self, message):
        return redact_secret(message, self.api_key)

    def _redact_error(self, error):
        # 错误实例化后再脱敏其文本：即使服务商报文里意外回显密钥也不外泄。
        return type(error)(redact_secret(str(error), self.api_key))


# —— 内部工具 ---------------------------------------------------------------

def _next_delay(error, retries):
    """返回本次应退避的秒数；该类重试次数用尽则返回 None（调用方抛出）。"""
    policy = _RETRY_POLICY.get(type(error))
    if policy is None:
        return None
    max_retries, delays = policy
    used = retries.get(type(error), 0)
    if used >= max_retries:
        return None
    retries[type(error)] = used + 1
    return delays[min(used, len(delays) - 1)]


def _normalize_messages(prompt_messages):
    """把 `{"system","user"}` 对或 role/content 列表规范为 OpenAI messages 列表。"""
    if isinstance(prompt_messages, dict):
        if "role" in prompt_messages and "content" in prompt_messages:
            return [{"role": str(prompt_messages["role"]),
                     "content": _as_text(prompt_messages["content"])}]
        messages = []
        if "system" in prompt_messages:
            messages.append({"role": "system", "content": _as_text(prompt_messages["system"])})
        if "user" in prompt_messages:
            messages.append({"role": "user", "content": _as_text(prompt_messages["user"])})
        if not messages:
            raise LLMError(_BAD_REQUEST_MESSAGE)
        return messages
    if isinstance(prompt_messages, (list, tuple)):
        messages = []
        for item in prompt_messages:
            if not isinstance(item, dict) or "role" not in item or "content" not in item:
                raise LLMError("翻译请求消息必须是含 role 与 content 的对象列表。")
            messages.append({"role": str(item["role"]), "content": _as_text(item["content"])})
        if not messages:
            raise LLMError("翻译请求消息为空。")
        return messages
    raise LLMError(_BAD_REQUEST_MESSAGE)


def _as_text(value):
    return "" if value is None else str(value)


def _extract_content(payload):
    """从响应体取 `choices[0].message.content`；区分拒译 / 空响应 / 结构错误。"""
    if not isinstance(payload, dict):
        raise LLMError(_MALFORMED_MESSAGE)
    choices = payload.get("choices")
    if not isinstance(choices, list) or not choices:
        raise LLMError(_MALFORMED_MESSAGE)
    choice = choices[0]
    if not isinstance(choice, dict):
        raise LLMError(_MALFORMED_MESSAGE)
    message = choice.get("message")
    if not isinstance(message, dict):
        raise LLMError(_MALFORMED_MESSAGE)
    refusal = message.get("refusal")
    if isinstance(refusal, str) and refusal.strip():
        raise RefusalError(_REFUSAL_MESSAGE)
    if choice.get("finish_reason") == "content_filter":
        raise RefusalError(_REFUSAL_MESSAGE)
    content = message.get("content")
    if content is None:
        raise EmptyResponseError(_EMPTY_MESSAGE)
    if not isinstance(content, str):
        content = _join_content_parts(content)
    if not content.strip():
        raise EmptyResponseError(_EMPTY_MESSAGE)
    if _looks_like_refusal(content):
        raise RefusalError(_REFUSAL_MESSAGE)
    return content


def _join_content_parts(content):
    """兼容多段内容（部分端点返回 `[{"type":"text","text":...}]`）。"""
    if isinstance(content, list):
        parts = [item["text"] for item in content
                 if isinstance(item, dict) and isinstance(item.get("text"), str)]
        if parts:
            return "".join(parts)
    raise LLMError(_MALFORMED_MESSAGE)


def _looks_like_refusal(text):
    return any(pattern.match(text) for pattern in _REFUSAL_PATTERNS)


def prompt_tokens(messages):
    """保守估算一次请求的 prompt token 数（成本预检用，F7）。

    与 `chunk.estimate_tokens` 同一口径：CJK 记 1 token/字，拉丁约 4 字符/token，
    混合取更保守值。仅统计发送的 prompt，不含补全。
    """
    text = "\n".join(str(item.get("content", "")) for item in messages)
    return estimate_tokens(text)


def _token_usage(payload, messages):
    """请求后用量：优先取服务商 `usage.total_tokens`，缺失时分开估算 prompt 与补全（F10）。

    分开估算（而不是把 prompt 与补全拼成一个字符串再估）避免混合脚本下 `len // 2`
    下界把两部分互相摊薄；得到的是保守上界，方向是**不低估**用量。
    """
    usage = payload.get("usage") if isinstance(payload, dict) else None
    if isinstance(usage, dict):
        total = usage.get("total_tokens")
        if isinstance(total, int) and not isinstance(total, bool) and total >= 0:
            return total
    completion = ""
    if isinstance(payload, dict):
        choices = payload.get("choices")
        if isinstance(choices, list) and choices and isinstance(choices[0], dict):
            message = choices[0].get("message")
            if isinstance(message, dict) and isinstance(message.get("content"), str):
                completion = message["content"]
    return prompt_tokens(messages) + estimate_tokens(completion)


def _http_error_detail(error):
    """读取并截断服务商错误报文（小写）用于分类；读取失败返回空串。"""
    try:
        raw = error.read()
    except Exception:
        return ""
    if isinstance(raw, bytes):
        text = raw.decode("utf-8", "replace")
    elif isinstance(raw, str):
        text = raw
    else:
        return ""
    return text[:2000].lower()


def _classify_http_error(error):
    """把 HTTP 错误映射到错误分类；报文本身不进入异常信息（只保留状态码）。"""
    status = getattr(error, "code", None)
    detail = _http_error_detail(error)
    if status == 429:
        return RateLimitError(_RATE_LIMIT_MESSAGE)
    if status == 413 or any(marker in detail for marker in _OVER_LIMIT_MARKERS):
        return OverLimitError(_OVER_LIMIT_MESSAGE)
    if status in (401, 403):
        return AuthError(_AUTH_MESSAGE.format(status=status))
    if isinstance(status, int) and (status >= 500 or status == 408):
        return NetworkError(_SERVER_MESSAGE.format(status=status))
    return LLMError(_GENERIC_HTTP_MESSAGE.format(status=status))


__all__ = [
    "LLMClient", "CostCap", "DEFAULT_TIMEOUT",
    "LLMError", "NetworkError", "RateLimitError", "AuthError", "RefusalError",
    "EmptyResponseError", "OverLimitError", "AbortedError", "ValidationError",
    "OverBudgetError",
    "NETWORK_BACKOFFS", "RATE_LIMIT_BACKOFFS", "EMPTY_BACKOFFS", "VALIDATION_BACKOFFS",
    "redact_headers", "redact_secret", "prompt_tokens",
]
