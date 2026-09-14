// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// v1.1 翻译 · 云端凭据存储测试：命名空间 / 存取删 / DeleteAll 只清 AIPDF-Translate-* /
// 目的地归一（忽略 path·query）/ 后端不可用传播，全部走内存替身，不碰真实凭据管理器。
using System.Text.RegularExpressions;
using AIPDF.Windows.Services;
using Xunit;

namespace AIPDF.Windows.Tests.Services;

public sealed class TranslationCredentialStoreTests
{
    private const string Provider = "deepseek";
    private const string Endpoint = "https://api.deepseek.com/v1";
    private const string Key = "sk-t10-credential-secret";

    private static TranslationCredentialStore NewStore(out InMemoryCredentialBackend backend)
    {
        backend = new InMemoryCredentialBackend();
        return new TranslationCredentialStore(backend);
    }

    // ---------------------------------------------------------------- 存取 / 删除

    [Fact]
    public void Store_Then_Get_RoundTrips()
    {
        var store = NewStore(out var backend);
        store.Store(Provider, Endpoint, Key);

        Assert.Equal(Key, store.Get(Provider, Endpoint));
        Assert.Equal(1, backend.Count);
    }

    [Fact]
    public void Store_OverwritesExistingKey()
    {
        var store = NewStore(out _);
        store.Store(Provider, Endpoint, Key);
        store.Store(Provider, Endpoint, Key + "-rotated");

        Assert.Equal(Key + "-rotated", store.Get(Provider, Endpoint));
    }

    [Fact]
    public void Get_Missing_ReturnsNull()
    {
        var store = NewStore(out _);
        Assert.Null(store.Get(Provider, Endpoint));
        Assert.Null(store.Get(Provider, "https://not-saved.example.com"));
    }

    [Theory]
    [InlineData("", Endpoint, Key)]
    [InlineData("   ", Endpoint, Key)]
    [InlineData(Provider, "", Key)]
    [InlineData(Provider, "   ", Key)]
    [InlineData(Provider, Endpoint, "")]
    [InlineData(Provider, Endpoint, "   ")]
    public void Store_RejectsBlankArguments(string provider, string endpoint, string key)
    {
        var store = NewStore(out _);
        Assert.Throws<ArgumentException>(() => store.Store(provider, endpoint, key));
    }

    [Fact]
    public void Get_BlankArguments_ReturnsNull()
    {
        var store = NewStore(out _);
        Assert.Null(store.Get("", Endpoint));
        Assert.Null(store.Get(Provider, "  "));
    }

    [Fact]
    public void Delete_RemovesEntry_ReturnsTrueThenFalse()
    {
        var store = NewStore(out var backend);
        store.Store(Provider, Endpoint, Key);

        Assert.True(store.Delete(Provider, Endpoint));
        Assert.Null(store.Get(Provider, Endpoint));
        Assert.False(store.Delete(Provider, Endpoint)); // 已删除再删返回 false
        Assert.Equal(0, backend.Count);
    }

    [Fact]
    public void Delete_BlankArguments_ReturnsFalse()
    {
        var store = NewStore(out _);
        Assert.False(store.Delete("", Endpoint));
        Assert.False(store.Delete(Provider, ""));
    }

    [Fact]
    public void DeleteAll_RemovesOnlyTranslateNamespace()
    {
        var store = NewStore(out var backend);
        backend.Write("Other-App-Credential", "keep-me");
        store.Store(Provider, Endpoint, Key);
        store.Store("moonshot", "https://api.moonshot.cn/v1", "sk-moonshot");

        var removed = store.DeleteAll();

        Assert.Equal(2, removed);
        Assert.Equal("keep-me", backend.Read("Other-App-Credential"));
        Assert.Null(store.Get(Provider, Endpoint));
        Assert.Equal(1, backend.Count);
    }

    // ---------------------------------------------------------------- 命名与目的地归一

    [Fact]
    public void TargetName_HasNamespaceProviderAndShortHash()
    {
        var target = TranslationCredentialStore.TargetName(Provider, Endpoint);

        Assert.Matches(new Regex("^AIPDF-Translate-deepseek-[0-9a-f]{8}$"), target);
        Assert.Equal(target, TranslationCredentialStore.TargetName(Provider, Endpoint)); // 稳定
    }

    [Fact]
    public void TargetName_IgnoresPathQueryAndUserInfo()
    {
        var origin = TranslationCredentialStore.TargetName(Provider, "https://api.deepseek.com/v1");

        Assert.Equal(origin, TranslationCredentialStore.TargetName(Provider, "https://api.deepseek.com/v2/chat"));
        Assert.Equal(origin, TranslationCredentialStore.TargetName(Provider, "https://api.deepseek.com"));
        Assert.Equal(origin, TranslationCredentialStore.TargetName(Provider, "https://api.deepseek.com/v1?api_key=ignored"));
    }

    [Fact]
    public void TargetName_DiffersByProtocolHostAndPort()
    {
        var origin = TranslationCredentialStore.TargetName(Provider, "https://api.deepseek.com/v1");

        Assert.NotEqual(origin, TranslationCredentialStore.TargetName(Provider, "http://api.deepseek.com/v1"));
        Assert.NotEqual(origin, TranslationCredentialStore.TargetName(Provider, "https://other.deepseek.com/v1"));
        Assert.NotEqual(origin, TranslationCredentialStore.TargetName(Provider, "https://api.deepseek.com:8443/v1"));
    }

    [Fact]
    public void TargetName_DiffersByProvider()
    {
        Assert.NotEqual(
            TranslationCredentialStore.TargetName("deepseek", Endpoint),
            TranslationCredentialStore.TargetName("moonshot", Endpoint));
    }

    [Fact]
    public void DifferentEndpoints_DoNotCollide()
    {
        var store = NewStore(out var backend);
        store.Store(Provider, "https://api.deepseek.com/v1", "key-a");
        store.Store(Provider, "https://api.deepseek.com:8443/v1", "key-b");

        Assert.Equal("key-a", store.Get(Provider, "https://api.deepseek.com/v1"));
        Assert.Equal("key-b", store.Get(Provider, "https://api.deepseek.com:8443/v1"));
        Assert.Equal(2, backend.Count);
    }

    // ---------------------------------------------------------------- 后端不可用（T39）

    [Fact]
    public void BackendUnavailable_PropagatesTypedException()
    {
        var store = new TranslationCredentialStore(new ThrowingCredentialBackend());

        Assert.Throws<CredentialStoreUnavailableException>(() => store.Store(Provider, Endpoint, Key));
        Assert.Throws<CredentialStoreUnavailableException>(() => store.Get(Provider, Endpoint));
        Assert.Throws<CredentialStoreUnavailableException>(() => store.Delete(Provider, Endpoint));
        Assert.Throws<CredentialStoreUnavailableException>(() => store.DeleteAll());
    }

    private sealed class ThrowingCredentialBackend : ICredentialBackend
    {
        private static CredentialStoreUnavailableException Down() => new("unavailable", 5);

        public string? Read(string target) => throw Down();

        public void Write(string target, string secret) => throw Down();

        public bool Delete(string target) => throw Down();

        public IReadOnlyList<string> Enumerate(string prefix) => throw Down();
    }
}
