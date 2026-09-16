// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// v1.2 翻译 · 模型拉取服务测试（T5）：
//   - 请求 URI = {base}/models（OpenAI base 不再追加 /v1，绝不出现 /v1/v1/models）；
//   - 本地 loopback 零密钥外发（即使调用方传入密钥）；未确认端点（无已保存密钥）零鉴权头（规则 7）；
//   - 云端按协议鉴权：openai → Authorization: Bearer；anthropic → x-api-key + anthropic-version；
//   - 10s 超时 + 2 次重试（共 3 次尝试）；401 不重试；用户取消不重试；
//   - 非 JSON / 非 OpenAI 形状 → 结构化错误；游标分页两页；明文 http 云端端点拒绝。
// 全部走内存 HttpMessageHandler，不发起真实网络请求；密钥为可识别假值，断言不出现于异常文本。
using System.Net;
using System.Net.Http;
using System.Text;
using AIPDF.Windows.Services;
using Xunit;

namespace AIPDF.Windows.Tests.Services;

public sealed class LocalModelFetcherTests
{
    private const string ApiKey = "sk-task5-secret-key-8f2c";
    private const string OpenAiBase = "https://api.openai.com/v1";
    private const string LocalBase = "http://127.0.0.1:11434/v1";

    // ---------------------------------------------------------------- 本地（loopback）：零密钥外发

    [Fact]
    public async Task FetchAsync_Local_Success_ReturnsModels_WithoutAuthHeaders()
    {
        var handler = new ModelListHandler().ReplyJson("""{"data":[{"id":"qwen2.5:7b"},{"id":"llama3.2:3b"}]}""");
        var fetcher = new LocalModelFetcher(handler);

        var models = await fetcher.FetchAsync(LocalBase, null, "openai", CancellationToken.None);

        Assert.Equal(new[] { "qwen2.5:7b", "llama3.2:3b" }, models);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("GET", request.Method);
        Assert.Equal("http://127.0.0.1:11434/v1/models", request.Uri); // 只拼 /models，不再追加 /v1
        Assert.DoesNotContain("/v1/v1", request.Uri, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("/v1/models", request.Uri, StringComparison.Ordinal);
        Assert.Null(request.Header("Authorization"));
        Assert.Null(request.Header("x-api-key"));
    }

    [Theory]
    [InlineData("http://127.0.0.1:11434/v1")]
    [InlineData("http://[::1]:11434/v1")]
    public async Task FetchAsync_LoopbackWithKey_StillSendsNoAuthHeaders(string requestBase)
    {
        var handler = new ModelListHandler().ReplyJson("""{"data":[{"id":"m"}]}""");
        var fetcher = new LocalModelFetcher(handler);

        await fetcher.FetchAsync(requestBase, ApiKey, "openai", CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Null(request.Header("Authorization"));
        Assert.Null(request.Header("x-api-key"));
        Assert.DoesNotContain(ApiKey, request.Uri);
    }

    [Fact]
    public async Task FetchAsync_LocalhostResolvedAsLoopback_SendsNoAuthHeaders()
    {
        var handler = new ModelListHandler().ReplyJson("""{"data":[]}""");
        var fetcher = new LocalModelFetcher(handler, timeout: null, resolveHost: _ => [IPAddress.Loopback]);

        await fetcher.FetchAsync("http://localhost:11434", ApiKey, "openai", CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("http://localhost:11434/v1/models", request.Uri);
        Assert.Null(request.Header("Authorization"));
        Assert.Null(request.Header("x-api-key"));
    }

    // ---------------------------------------------------------------- 云端：按协议鉴权 / 未确认端点零密钥

    [Fact]
    public async Task FetchAsync_CloudOpenAi_SendsBearerAuthorization()
    {
        var handler = new ModelListHandler().ReplyJson("""{"data":[{"id":"gpt-4o"}]}""");
        var fetcher = new LocalModelFetcher(handler);

        var models = await fetcher.FetchAsync(OpenAiBase, ApiKey, "openai", CancellationToken.None);

        Assert.Equal(new[] { "gpt-4o" }, models);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://api.openai.com/v1/models", request.Uri);
        Assert.Equal($"Bearer {ApiKey}", request.Header("Authorization"));
        Assert.Null(request.Header("x-api-key"));
    }

    [Fact]
    public async Task FetchAsync_CloudAnthropic_SendsXApiKey_AndUsesV1ModelsUri()
    {
        var handler = new ModelListHandler().ReplyJson("""{"data":[{"id":"claude-sonnet-4"}]}""");
        var fetcher = new LocalModelFetcher(handler);

        await fetcher.FetchAsync("https://open.bigmodel.cn/api/anthropic", ApiKey, "anthropic", CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://open.bigmodel.cn/api/anthropic/v1/models", request.Uri); // anthropic base 无 /v1 → 补齐
        Assert.Equal(ApiKey, request.Header("x-api-key"));
        Assert.Equal("2023-06-01", request.Header("anthropic-version"));
        Assert.Null(request.Header("Authorization"));
    }

    [Fact]
    public async Task FetchAsync_CloudWithoutSavedKey_SendsNoAuthHeaders()
    {
        // 规则 7：端点未经用户显式保存（无密钥）→ 绝不自动外发鉴权头。
        var handler = new ModelListHandler().ReplyJson("""{"data":[]}""");
        var fetcher = new LocalModelFetcher(handler);

        await fetcher.FetchAsync(OpenAiBase, null, "openai", CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Null(request.Header("Authorization"));
        Assert.Null(request.Header("x-api-key"));
    }

    // ---------------------------------------------------------------- 失败：状态码 / 超时 / 非 JSON / 明文 http

    [Fact]
    public async Task FetchAsync_Unauthorized_ThrowsAuthFailure_WithoutRetry_AndHidesKey()
    {
        var handler = new ModelListHandler().ReplyStatus(HttpStatusCode.Unauthorized);
        var fetcher = new LocalModelFetcher(handler);

        var exception = await Assert.ThrowsAsync<LocalModelFetchException>(
            () => fetcher.FetchAsync(OpenAiBase, ApiKey, "openai", CancellationToken.None));

        Assert.Equal(LocalModelFetchFailureKind.Auth, exception.Kind);
        Assert.Equal(401, exception.StatusCode);
        Assert.Single(handler.Requests); // 401 是确定性答案，不重试
        Assert.DoesNotContain(ApiKey, exception.ToString());
    }

    [Fact]
    public async Task FetchAsync_Timeout_RetriesExactlyThreeTimes_AndHidesKey()
    {
        var handler = new ModelListHandler().Throw(_ => new TaskCanceledException("模拟超时", new TimeoutException()));
        var fetcher = new LocalModelFetcher(handler);

        var exception = await Assert.ThrowsAsync<LocalModelFetchException>(
            () => fetcher.FetchAsync(OpenAiBase, ApiKey, "openai", CancellationToken.None));

        Assert.Equal(LocalModelFetchFailureKind.Timeout, exception.Kind);
        Assert.Equal(3, handler.Requests.Count); // 恰好 3 次尝试（1 + 2 次重试）
        Assert.Equal(LocalModelFetcher.MaxAttempts, handler.Requests.Count);
        Assert.DoesNotContain(ApiKey, exception.ToString());
    }

    [Fact]
    public async Task FetchAsync_TransportFailure_RetriesExactlyThreeTimes_AndHidesKey()
    {
        var handler = new ModelListHandler().Throw(_ => new HttpRequestException("连接被拒绝"));
        var fetcher = new LocalModelFetcher(handler);

        var exception = await Assert.ThrowsAsync<LocalModelFetchException>(
            () => fetcher.FetchAsync(OpenAiBase, ApiKey, "openai", CancellationToken.None));

        Assert.Equal(LocalModelFetchFailureKind.Network, exception.Kind);
        Assert.Equal(3, handler.Requests.Count);
        Assert.DoesNotContain(ApiKey, exception.ToString());
    }

    [Theory]
    [InlineData("not-json-at-all")]                            // 非 JSON
    [InlineData("")]                                           // 空响应体
    [InlineData("""{"models":[{"name":"qwen2.5"}]}""")]        // 合法 JSON 但非 OpenAI 形状
    [InlineData("""["qwen2.5","llama3.2"]""")]                 // 根为数组
    public async Task FetchAsync_InvalidResponse_ThrowsStructuredError(string body)
    {
        var handler = new ModelListHandler().ReplyJson(body);
        var fetcher = new LocalModelFetcher(handler);

        var exception = await Assert.ThrowsAsync<LocalModelFetchException>(
            () => fetcher.FetchAsync(OpenAiBase, null, "openai", CancellationToken.None));

        Assert.Equal(LocalModelFetchFailureKind.InvalidResponse, exception.Kind);
    }

    [Fact]
    public async Task FetchAsync_CloudPlainHttp_Rejected_WithoutAnyRequest()
    {
        var handler = new ModelListHandler();
        var fetcher = new LocalModelFetcher(handler);

        var exception = await Assert.ThrowsAsync<LocalModelFetchException>(
            () => fetcher.FetchAsync("http://api.example.com/v1", ApiKey, "openai", CancellationToken.None));

        Assert.Equal(LocalModelFetchFailureKind.InsecureEndpoint, exception.Kind);
        Assert.Empty(handler.Requests); // 拒绝在明文 http 上发起任何请求
        Assert.DoesNotContain(ApiKey, exception.ToString());
    }

    [Fact]
    public async Task FetchAsync_UserCancellation_Propagates_WithoutRetry()
    {
        using var cts = new CancellationTokenSource();
        var handler = new ModelListHandler().Throw(_ =>
        {
            cts.Cancel();
            return new TaskCanceledException("用户取消");
        });
        var fetcher = new LocalModelFetcher(handler);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fetcher.FetchAsync(OpenAiBase, ApiKey, "openai", cts.Token));

        Assert.Single(handler.Requests); // 用户取消不是超时，不重试
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task FetchAsync_BlankRequestBase_ThrowsArgumentException(string requestBase)
    {
        var fetcher = new LocalModelFetcher(new ModelListHandler());

        await Assert.ThrowsAsync<ArgumentException>(
            () => fetcher.FetchAsync(requestBase, null, "openai", CancellationToken.None));
    }

    // ---------------------------------------------------------------- 成功形态：空列表 / 分页 / URI 形状 / 默认值

    [Fact]
    public async Task FetchAsync_EmptyData_ReturnsEmptyList()
    {
        var handler = new ModelListHandler().ReplyJson("""{"data":[]}""");
        var fetcher = new LocalModelFetcher(handler);

        var models = await fetcher.FetchAsync(LocalBase, null, "openai", CancellationToken.None);

        Assert.Empty(models);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("""{"data":[{"id":"m1"}],"next":"c2"}""", "cursor=c2")]   // next 游标
    [InlineData("""{"data":[{"id":"m1"}],"cursor":"c9"}""", "cursor=c9")] // cursor 游标
    public async Task FetchAsync_Paginated_FollowsCursor_TwoPages(string firstPage, string expectedQuery)
    {
        var handler = new ModelListHandler()
            .ReplyJson(firstPage)
            .ReplyJson("""{"data":[{"id":"m2"}]}""");
        var fetcher = new LocalModelFetcher(handler);

        var models = await fetcher.FetchAsync(LocalBase, null, "openai", CancellationToken.None);

        Assert.Equal(new[] { "m1", "m2" }, models);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal($"http://127.0.0.1:11434/v1/models?{expectedQuery}", handler.Requests[1].Uri);
    }

    [Fact]
    public async Task FetchAsync_Paginated_StopsWhenCursorDoesNotAdvance()
    {
        var handler = new ModelListHandler()
            .ReplyJson("""{"data":[{"id":"m1"}],"cursor":"same"}""")
            .ReplyJson("""{"data":[{"id":"m2"}],"cursor":"same"}""");
        var fetcher = new LocalModelFetcher(handler);

        var models = await fetcher.FetchAsync(LocalBase, null, "openai", CancellationToken.None);

        Assert.Equal(new[] { "m1", "m2" }, models);
        Assert.Equal(2, handler.Requests.Count); // 游标未前进 → 停止，不无限续拉
    }

    [Theory]
    [InlineData("http://127.0.0.1:11434", "http://127.0.0.1:11434/v1/models")]                                // 原始地址缺 /v1 → 补齐
    [InlineData("http://127.0.0.1:11434/v1/", "http://127.0.0.1:11434/v1/models")]                            // 尾斜杠
    [InlineData("https://api.deepseek.com/v1/chat/completions", "https://api.deepseek.com/v1/models")]        // 完整端点粘贴
    [InlineData("https://open.bigmodel.cn/api/paas/v4", "https://open.bigmodel.cn/api/paas/v4/models")]       // v4 版本段保留
    public async Task FetchAsync_RequestUri_NeverDoublesVersionSegment(string input, string expectedUri)
    {
        var handler = new ModelListHandler().ReplyJson("""{"data":[]}""");
        var fetcher = new LocalModelFetcher(handler);

        await fetcher.FetchAsync(input, null, "openai", CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(expectedUri, request.Uri);
        Assert.DoesNotContain("/v1/v1", request.Uri, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("/models", request.Uri, StringComparison.Ordinal);
    }

    [Fact]
    public void Defaults_AreTenSecondTimeout_AndThreeAttempts()
    {
        var fetcher = new LocalModelFetcher();

        Assert.Equal(TimeSpan.FromSeconds(10), LocalModelFetcher.DefaultTimeout);
        Assert.Equal(TimeSpan.FromSeconds(10), fetcher.Timeout);
        Assert.Equal(3, LocalModelFetcher.MaxAttempts);
    }
}

/// <summary>内存 HttpMessageHandler：记录请求（方法 / URI / 请求头）并按队列返回预置响应或异常。</summary>
internal sealed class ModelListHandler : HttpMessageHandler
{
    private readonly Queue<Func<CancellationToken, HttpResponseMessage>> _responses = new();
    private Func<CancellationToken, Exception>? _fault;

    /// <summary>按到达顺序记录的请求（URI 与请求头快照，供断言）。</summary>
    public List<RecordedModelRequest> Requests { get; } = [];

    public ModelListHandler ReplyJson(string json, HttpStatusCode status = HttpStatusCode.OK)
    {
        _responses.Enqueue(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        });
        return this;
    }

    public ModelListHandler ReplyStatus(HttpStatusCode status)
    {
        _responses.Enqueue(_ => new HttpResponseMessage(status));
        return this;
    }

    /// <summary>持续故障：设置后每次请求都抛（用于重试上限 / 取消传播断言）。</summary>
    public ModelListHandler Throw(Func<CancellationToken, Exception> factory)
    {
        _fault = factory;
        return this;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(new RecordedModelRequest(request));

        if (_fault is not null)
        {
            try
            {
                return Task.FromException<HttpResponseMessage>(_fault(cancellationToken));
            }
            catch (Exception exception)
            {
                return Task.FromException<HttpResponseMessage>(exception);
            }
        }

        if (_responses.Count == 0)
        {
            return Task.FromException<HttpResponseMessage>(new InvalidOperationException("测试未预置更多响应。"));
        }

        try
        {
            return Task.FromResult(_responses.Dequeue()(cancellationToken));
        }
        catch (Exception exception)
        {
            return Task.FromException<HttpResponseMessage>(exception);
        }
    }
}

/// <summary>单个已记录请求：方法 + URI + 请求头快照（请求对象在发送后被释放，须提前快照）。</summary>
internal sealed class RecordedModelRequest
{
    private readonly Dictionary<string, string> _headers;

    public RecordedModelRequest(HttpRequestMessage request)
    {
        Method = request.Method.Method;
        Uri = request.RequestUri?.ToString() ?? "";
        _headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in request.Headers)
        {
            _headers[header.Key] = string.Join(",", header.Value);
        }
    }

    public string Method { get; }

    public string Uri { get; }

    public string? Header(string name) => _headers.TryGetValue(name, out var value) ? value : null;
}
