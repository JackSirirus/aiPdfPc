// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// V3 T1 设置存储测试（纯逻辑）：
//   - 7 项设置往返 + UTF-8 无 BOM；
//   - 缺文件 / JSON 损坏 / schema 不识别 / 缺失字段 → 默认（AC-39.2：不崩、不提示）；
//   - 归一化：未知 zoom/panel/theme 回退、最近上限 [10,50]、打印 DPI [72,600]、渲染 DPI 回退/钳制；
//   - 无密码红线（序列化文本 + 模型结构双向断言）；
//   - 原子写（无 .tmp 半截文件、完整可解析、目录自建）；
//   - 缩放阶梯与 PageLayoutCalculator 取值一致，且设置不改变 150% 高 DPI 阈值阶梯。
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using AIPDF.Windows.Services;
using AIPDF.Windows.ViewModels;
using AIPDF.Windows.Views.Reader;
using Xunit;

namespace AIPDF.Windows.Tests.Services;

public sealed class SettingsStoreTests
{
    private sealed class Fixture : IDisposable
    {
        public string Root { get; }
        public string SettingsPath { get; }

        public Fixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "aipdf-v3t1-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            SettingsPath = Path.Combine(Root, "settings.json");
        }

        public SettingsStore NewStore() => new(SettingsPath);

        public void WriteRaw(string json) => File.WriteAllText(SettingsPath, json);

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
        }
    }

    private static void AssertDefaults(AppSettings s)
    {
        Assert.Equal(AppSettings.CurrentSchema, s.Schema);
        Assert.Equal("100", s.DefaultZoom);          // 非适配档，沿用旧行为
        Assert.Equal("thumbs", s.DefaultPanelKey);
        Assert.Equal(20, s.RecentFilesLimit);
        Assert.Equal("system", s.Theme);
        Assert.Equal(150, s.PrintDpi);
        Assert.Equal("", s.DefaultOutputDir);
        Assert.Equal(96, s.DefaultRenderDpi);         // = PageLayoutCalculator.BaseDpi
    }

    // ---------------------------------------------------------------- 往返（7 项）

    [Fact]
    public void SaveLoad_Roundtrip_PreservesAllSevenSettings_Utf8NoBom()
    {
        using var fx = new Fixture();
        var store = fx.NewStore();
        var outputDir = Path.Combine(fx.Root, "out");

        store.Save(new AppSettings
        {
            DefaultZoom = "150",
            DefaultPanelKey = "outline",
            RecentFilesLimit = 35,
            Theme = "dark",
            PrintDpi = 300,
            DefaultOutputDir = outputDir,
            DefaultRenderDpi = 144,
        });

        var bytes = File.ReadAllBytes(fx.SettingsPath);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF); // UTF-8 无 BOM

        var loaded = store.Load();
        Assert.Equal(AppSettings.CurrentSchema, loaded.Schema);
        Assert.Equal("150", loaded.DefaultZoom);
        Assert.Equal("outline", loaded.DefaultPanelKey);
        Assert.Equal(35, loaded.RecentFilesLimit);
        Assert.Equal("dark", loaded.Theme);
        Assert.Equal(300, loaded.PrintDpi);
        Assert.Equal(outputDir, loaded.DefaultOutputDir);
        Assert.Equal(144, loaded.DefaultRenderDpi);
    }

    [Theory]
    [InlineData("fit-width")]
    [InlineData("fit-page")]
    [InlineData("25")]
    [InlineData("33")]
    [InlineData("50")]
    [InlineData("67")]
    [InlineData("100")]
    [InlineData("150")]
    [InlineData("200")]
    [InlineData("300")]
    [InlineData("400")]
    public void SaveLoad_Roundtrip_ZoomModes(string zoom)
    {
        using var fx = new Fixture();
        var store = fx.NewStore();

        store.Save(new AppSettings { DefaultZoom = zoom });

        Assert.Equal(zoom, store.Load().DefaultZoom);
    }

    // ---------------------------------------------------------------- 回退（AC-39.2）

    [Fact]
    public void Load_MissingFile_ReturnsDefaults()
    {
        using var fx = new Fixture();
        AssertDefaults(fx.NewStore().Load());
    }

    [Fact]
    public void Load_CorruptJson_ReturnsDefaults_NoThrow()
    {
        using var fx = new Fixture();
        fx.WriteRaw("{ 这不是 json ");
        AssertDefaults(fx.NewStore().Load()); // 不崩、不提示
    }

    [Fact]
    public void Load_UnsupportedSchema_ReturnsDefaults()
    {
        using var fx = new Fixture();
        fx.WriteRaw("""{"schemaVersion":999,"defaultZoom":"150","theme":"dark"}""");
        AssertDefaults(fx.NewStore().Load());
    }

    [Fact]
    public void Load_MissingFields_KeepDefaults()
    {
        using var fx = new Fixture();
        fx.WriteRaw("""{"schemaVersion":1}""");
        AssertDefaults(fx.NewStore().Load());
    }

    [Fact]
    public void Load_ToleratesUtf8Bom()
    {
        using var fx = new Fixture();
        var utf8Bom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        File.WriteAllText(fx.SettingsPath, """{"schemaVersion":1,"theme":"dark"}""", utf8Bom);
        Assert.Equal("dark", fx.NewStore().Load().Theme);
    }

    [Fact]
    public void Load_UnknownEnums_FallBackToDefaults()
    {
        using var fx = new Fixture();
        // search 明确不在默认面板允许集内（由 Ctrl+F 激活，非默认态）
        fx.WriteRaw("""{"schemaVersion":1,"defaultZoom":"fit-nonsense","defaultPanelKey":"search","theme":"neon"}""");

        var loaded = fx.NewStore().Load();

        Assert.Equal("100", loaded.DefaultZoom);
        Assert.Equal("thumbs", loaded.DefaultPanelKey);
        Assert.Equal("system", loaded.Theme);
    }

    // ---------------------------------------------------------------- 钳制 / 归一化

    [Theory]
    [InlineData(5, 10)]   // 越下界钳到 10
    [InlineData(99, 50)]  // 越上界钳到 50
    [InlineData(0, 10)]
    [InlineData(10, 10)]
    [InlineData(20, 20)]
    [InlineData(50, 50)]
    public void Load_RecentFilesLimit_ClampedToRange(int stored, int expected)
    {
        using var fx = new Fixture();
        fx.WriteRaw($"{{\"schemaVersion\":1,\"recentFilesLimit\":{stored}}}");
        Assert.Equal(expected, fx.NewStore().Load().RecentFilesLimit);
    }

    [Theory]
    [InlineData(10, 72)]    // 越下界钳到 72
    [InlineData(9999, 600)] // 越上界钳到 600
    [InlineData(72, 72)]
    [InlineData(150, 150)]
    [InlineData(600, 600)]
    public void Load_PrintDpi_ClampedToRange(int stored, int expected)
    {
        using var fx = new Fixture();
        fx.WriteRaw($"{{\"schemaVersion\":1,\"printDpi\":{stored}}}");
        Assert.Equal(expected, fx.NewStore().Load().PrintDpi);
    }

    [Theory]
    [InlineData(5000.0, 600.0)] // 越上界钳制
    [InlineData(1000.0, 600.0)]
    [InlineData(0.0, 96.0)]     // 非正数（未知/非法）回退 96
    [InlineData(-5.0, 96.0)]
    [InlineData(10.0, 36.0)]    // 正数越下界钳制
    [InlineData(96.0, 96.0)]
    [InlineData(144.0, 144.0)]
    public void Load_DefaultRenderDpi_OutOfRange_FallsBackOrClamps(double stored, double expected)
    {
        using var fx = new Fixture();
        var json = stored.ToString(CultureInfo.InvariantCulture);
        fx.WriteRaw($"{{\"schemaVersion\":1,\"defaultRenderDpi\":{json}}}");
        Assert.Equal(expected, fx.NewStore().Load().DefaultRenderDpi);
    }

    [Fact]
    public void Save_NormalizesOutOfRangeValues()
    {
        using var fx = new Fixture();
        var store = fx.NewStore();

        store.Save(new AppSettings
        {
            DefaultZoom = "bogus",
            DefaultPanelKey = "search",
            RecentFilesLimit = 999,
            Theme = "neon",
            PrintDpi = 1,
            DefaultRenderDpi = -1,
        });

        // Save 即归一化；重读（再次归一化）结果稳定
        var loaded = store.Load();
        Assert.Equal("100", loaded.DefaultZoom);
        Assert.Equal("thumbs", loaded.DefaultPanelKey);
        Assert.Equal(50, loaded.RecentFilesLimit);
        Assert.Equal("system", loaded.Theme);
        Assert.Equal(72, loaded.PrintDpi);
        Assert.Equal(96, loaded.DefaultRenderDpi);
    }

    [Fact]
    public void NormalizeZoom_CanonicalizesCase_AndRejectsOffLadder()
    {
        Assert.Equal("fit-width", SettingsStore.NormalizeZoom(" Fit-Width "));
        Assert.Equal("fit-page", SettingsStore.NormalizeZoom("FIT-PAGE"));
        Assert.Equal("150", SettingsStore.NormalizeZoom("150"));
        Assert.Equal("100", SettingsStore.NormalizeZoom("120"));   // 非阶梯档 → 默认
        Assert.Equal("100", SettingsStore.NormalizeZoom(null));
        Assert.Equal("100", SettingsStore.NormalizeZoom(""));
    }

    [Fact]
    public void NormalizeOutputDir_WhitespaceBecomesEmpty_ElseTrimmed()
    {
        Assert.Equal("", SettingsStore.NormalizeOutputDir(null));
        Assert.Equal("", SettingsStore.NormalizeOutputDir("   "));
        Assert.Equal(@"C:\out", SettingsStore.NormalizeOutputDir(@"  C:\out  "));
    }

    // ---------------------------------------------------------------- v1.1 翻译设置（T23）

    [Fact]
    public void SaveLoad_Roundtrip_TranslationSettings()
    {
        using var fx = new Fixture();
        var store = fx.NewStore();

        store.Save(new AppSettings
        {
            TranslationDefaultSourceLanguage = "en",
            TranslationDefaultTargetLanguage = "ja",
            TranslationTemplateId = "legal",
            TranslationCustomPrompt = "保持术语一致",
            TranslationGlossaryPath = @"  C:\dict\terms.csv  ",
            TranslationCostLimitEnabled = false,
            TranslationCostLimitTokens = 500_000,
            TranslationProviders =
            [
                new TranslationProviderConfig
                {
                    Provider = " deepseek ",
                    Endpoint = " https://api.deepseek.com/v1 ",
                    Model = " deepseek-chat ",
                },
            ],
        });

        var loaded = store.Load();
        Assert.Equal("en", loaded.TranslationDefaultSourceLanguage);
        Assert.Equal("ja", loaded.TranslationDefaultTargetLanguage);
        Assert.Equal("legal", loaded.TranslationTemplateId);
        Assert.Equal("保持术语一致", loaded.TranslationCustomPrompt);
        Assert.Equal(@"C:\dict\terms.csv", loaded.TranslationGlossaryPath);
        Assert.False(loaded.TranslationCostLimitEnabled);
        Assert.Equal(500_000, loaded.TranslationCostLimitTokens);
        Assert.Single(loaded.TranslationProviders);
        var provider = loaded.TranslationProviders[0];
        Assert.Equal("deepseek", provider.Provider);
        Assert.Equal("https://api.deepseek.com/v1", provider.Endpoint);
        Assert.Equal("deepseek-chat", provider.Model);
    }

    [Fact]
    public void Load_MissingTranslationFields_KeepDefaults()
    {
        using var fx = new Fixture();
        fx.WriteRaw("""{"schemaVersion":2}""");

        var loaded = fx.NewStore().Load();
        Assert.Equal("auto", loaded.TranslationDefaultSourceLanguage);
        Assert.Equal("zh", loaded.TranslationDefaultTargetLanguage);
        Assert.Equal("academic", loaded.TranslationTemplateId);
        Assert.Equal("", loaded.TranslationCustomPrompt);
        Assert.Equal("", loaded.TranslationGlossaryPath);
        Assert.True(loaded.TranslationCostLimitEnabled);
        Assert.Equal(2_000_000, loaded.TranslationCostLimitTokens);
        Assert.Empty(loaded.TranslationProviders);
    }

    [Fact]
    public void Load_UnknownTranslationLanguageOrTemplate_FallsBack()
    {
        using var fx = new Fixture();
        fx.WriteRaw(
            """{"schemaVersion":2,"translationDefaultSourceLanguage":"klingon","translationDefaultTargetLanguage":"auto","translationTemplateId":"poetry"}""");

        var loaded = fx.NewStore().Load();
        Assert.Equal("auto", loaded.TranslationDefaultSourceLanguage);
        Assert.Equal("zh", loaded.TranslationDefaultTargetLanguage); // 目标语言不接受 auto
        Assert.Equal("academic", loaded.TranslationTemplateId);
    }

    [Theory]
    [InlineData(0, 2_000_000L)]
    [InlineData(-5, 2_000_000L)]
    [InlineData(1, 1L)]
    [InlineData(9_999_999_999L, 1_000_000_000L)]
    [InlineData(250_000, 250_000L)]
    public void Load_TranslationCostLimitTokens_FallsBackOrClamps(long stored, long expected)
    {
        using var fx = new Fixture();
        fx.WriteRaw($"{{\"schemaVersion\":2,\"translationCostLimitTokens\":{stored}}}");
        Assert.Equal(expected, fx.NewStore().Load().TranslationCostLimitTokens);
    }

    [Fact]
    public void NormalizeTranslationProviders_DropsBlankAndUnsafe_DedupesOrigin()
    {
        var providers = new List<TranslationProviderConfig>
        {
            new() { Provider = "deepseek", Endpoint = "https://api.deepseek.com/v1", Model = "a" },
            new() { Provider = "", Endpoint = "https://api.deepseek.com/v1" },                            // 缺 provider
            new() { Provider = "moonshot", Endpoint = "" },                                               // 缺 endpoint
            new() { Provider = "leak", Endpoint = "https://key:secret@api.leak.com/v1" },                 // 内嵌密钥
            new() { Provider = "deepseek", Endpoint = "https://api.deepseek.com/v2/chat", Model = "b" },  // 同 origin → 去重
        };

        var normalized = SettingsStore.NormalizeTranslationProviders(providers);

        Assert.Single(normalized);
        Assert.Equal("deepseek", normalized[0].Provider);
        Assert.Equal("https://api.deepseek.com/v1", normalized[0].Endpoint);
        Assert.Equal("a", normalized[0].Model);
    }

    [Fact]
    public void Load_TranslationProviders_SkipsUnsafeEndpoint()
    {
        using var fx = new Fixture();
        fx.WriteRaw(
            """{"schemaVersion":2,"translationProviders":[{"provider":"leak","endpoint":"https://api.leak.com/v1?api_key=sk-x","model":"m"}]}""");
        Assert.Empty(fx.NewStore().Load().TranslationProviders);
    }

    [Fact]
    public void TranslationSettings_PersistWithoutSecret()
    {
        using var fx = new Fixture();
        fx.NewStore().Save(new AppSettings
        {
            TranslationProviders =
            [
                new TranslationProviderConfig
                {
                    Provider = "deepseek",
                    Endpoint = "https://api.deepseek.com/v1",
                    Model = "deepseek-chat",
                },
            ],
        });

        var raw = File.ReadAllText(fx.SettingsPath);
        Assert.Contains("translationProviders", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("apiKey", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TranslationSettings_OptionCodes_MatchConfigViewModel()
    {
        var vm = new TranslationConfigViewModel();

        Assert.Equal(
            vm.SourceLanguageOptions.Select(o => o.Code),
            AppSettings.TranslationSourceLanguageCodes);
        Assert.Equal(
            vm.TargetLanguageOptions.Select(o => o.Code),
            AppSettings.TranslationTargetLanguageCodes);
        Assert.Equal(
            vm.TemplateOptions.Select(o => o.Id),
            AppSettings.TranslationTemplateIds);
    }

    // ---------------------------------------------------------------- 无密码红线

    [Fact]
    public void Settings_Serialization_ContainsNoPasswordField()
    {
        using var fx = new Fixture();
        fx.NewStore().Save(new AppSettings { DefaultOutputDir = @"C:\secret\dir" });

        var json = File.ReadAllText(fx.SettingsPath);
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pwd", json, StringComparison.OrdinalIgnoreCase);

        // 结构层红线：模型本身不得存在密码相关属性（防止未来误加字段）
        var props = typeof(AppSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        Assert.DoesNotContain(props, p =>
            p.Name.Contains("password", StringComparison.OrdinalIgnoreCase)
            || p.Name.Contains("pwd", StringComparison.OrdinalIgnoreCase));
    }

    // ---------------------------------------------------------------- 原子写

    [Fact]
    public void Save_IsAtomic_NoTempLeftover_AndReplacesPrevious()
    {
        using var fx = new Fixture();
        var store = fx.NewStore();

        store.Save(new AppSettings { RecentFilesLimit = 30 });
        store.Save(new AppSettings { RecentFilesLimit = 40, Theme = "dark" });

        // 目录内仅剩目标文件，无 .tmp 半截文件
        var files = Directory.GetFiles(fx.Root);
        Assert.Single(files);
        Assert.True(string.Equals(fx.SettingsPath, files[0], StringComparison.OrdinalIgnoreCase), files[0]);
        Assert.DoesNotContain(".tmp", files[0], StringComparison.OrdinalIgnoreCase);

        // 目标文件始终可完整解析（非半截），且为最后一次写入内容
        using var doc = JsonDocument.Parse(File.ReadAllText(fx.SettingsPath));
        Assert.Equal(40, doc.RootElement.GetProperty("recentFilesLimit").GetInt32());
        var loaded = store.Load();
        Assert.Equal(40, loaded.RecentFilesLimit);
        Assert.Equal("dark", loaded.Theme);
    }

    [Fact]
    public void Save_CreatesMissingDirectory()
    {
        using var fx = new Fixture();
        var nested = Path.Combine(fx.Root, "nested", "sub", "settings.json");
        var store = new SettingsStore(nested);

        store.Save(new AppSettings { Theme = "light" });

        Assert.True(File.Exists(nested));
        Assert.Equal("light", new SettingsStore(nested).Load().Theme);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(nested)!, "*.tmp"));
    }

    // ---------------------------------------------------------------- 与布局层不变量

    [Fact]
    public void ZoomLadder_MatchesPageLayoutCalculator()
    {
        Assert.Equal(
            PageLayoutCalculator.LadderPercent,
            AppSettings.ZoomLadderPercent.Select(v => (double)v));
    }

    [Fact]
    public void DefaultRenderDpi_DoesNotAlterHighDpiThresholdLadder()
    {
        // 设置只暴露渲染基准 DPI；≥150% 走 150dpi、以下走 96dpi 的阈值阶梯固定不变（T5 注入基准时须保持）。
        Assert.Equal(PageLayoutCalculator.BaseDpi, PageLayoutCalculator.DpiForZoom(100));
        Assert.Equal(PageLayoutCalculator.HighDpi, PageLayoutCalculator.DpiForZoom(150));
        Assert.Equal(PageLayoutCalculator.HighDpi, PageLayoutCalculator.DpiForZoom(400));
    }
}
