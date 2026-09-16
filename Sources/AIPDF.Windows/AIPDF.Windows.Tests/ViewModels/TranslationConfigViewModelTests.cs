// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// v1.1 T13 / v1.2 T9 共享翻译配置面板测试：默认值（本地服务 / 自动检测源 / 中文目标 / 学术模板 / mono+dual）
// 与校验（目标语言显式、源≠目标、至少一种输出、自定义模板需正文、成本上限为正、术语表为 CSV）；
// T9 追加供应商选择派生（协议 / 按模型覆盖 / 只读展示 / 拉取填充）。
using System.ComponentModel;
using System.Linq;
using AIPDF.Windows.Services;
using AIPDF.Windows.ViewModels;
using Xunit;

namespace AIPDF.Windows.Tests.ViewModels;

public sealed class TranslationConfigViewModelTests
{
    [Fact]
    public void Defaults_MatchDesignDecisions()
    {
        var vm = new TranslationConfigViewModel();

        Assert.Equal(TranslationConfigViewModel.AutoDetectCode, vm.SourceLanguage.Code);
        Assert.Equal(TranslationConfigViewModel.DefaultTargetCode, vm.TargetLanguage.Code);
        Assert.Equal(TranslationConfigViewModel.DefaultTemplateId, vm.SelectedTemplate.Id);
        Assert.Equal(TranslationConfigViewModel.DefaultProviderId, vm.SelectedProvider); // 决策 #15：默认本地
        Assert.True(vm.IsLocalProvider);
        Assert.False(vm.IsCloudProvider);
        Assert.True(vm.GenerateMono);   // 默认 mono + dual
        Assert.True(vm.GenerateDual);
        Assert.False(vm.ExportMarkdown); // Markdown 默认关
        Assert.True(vm.CostLimitEnabled);
        Assert.Equal(TranslationConfigViewModel.DefaultCostLimitTokens, vm.CostLimitTokens);
        Assert.False(vm.IsCostLimitUnlimited);
        Assert.Equal("", vm.GlossaryPath);
    }

    [Fact]
    public void Defaults_AreValid()
    {
        var vm = new TranslationConfigViewModel();

        Assert.Null(vm.Validate());
        Assert.True(vm.CanStart);
        Assert.False(vm.HasValidationError);
        Assert.Equal("", vm.ValidationMessage);
    }

    [Fact]
    public void Options_CoverFivePresetsPlusCustom()
    {
        var vm = new TranslationConfigViewModel();

        Assert.Equal(6, vm.TemplateOptions.Count);
        Assert.Single(vm.TemplateOptions, t => t.IsCustom);
        Assert.Equal(5, vm.TemplateOptions.Count(t => !t.IsCustom));
        Assert.Equal(2, vm.TemplateOptions.Count(t => t.HighRisk)); // 法律 / 医学
    }

    [Fact]
    public void TargetOptions_ExcludeAutoDetect()
    {
        var vm = new TranslationConfigViewModel();

        Assert.DoesNotContain(vm.TargetLanguageOptions, o => o.Code == TranslationConfigViewModel.AutoDetectCode);
        Assert.Contains(vm.SourceLanguageOptions, o => o.Code == TranslationConfigViewModel.AutoDetectCode);
    }

    [Fact]
    public void Validate_TargetMustBeExplicit()
    {
        var vm = new TranslationConfigViewModel
        {
            TargetLanguage = new TranslationLanguageOption(TranslationConfigViewModel.AutoDetectCode, "自动检测"),
        };

        Assert.NotNull(vm.Validate());
        Assert.False(vm.CanStart);
        Assert.True(vm.HasValidationError);
    }

    [Fact]
    public void Validate_SourceEqualToTarget_Rejected()
    {
        var vm = new TranslationConfigViewModel();
        vm.SourceLanguage = vm.SourceLanguageOptions.First(o => o.Code == "zh"); // 目标默认 zh

        Assert.Equal("源语言与目标语言不能相同。", vm.Validate());
    }

    [Fact]
    public void Validate_AtLeastOneOutputRequired()
    {
        var vm = new TranslationConfigViewModel
        {
            GenerateMono = false,
            GenerateDual = false,
            ExportMarkdown = false,
        };

        Assert.Equal("请至少选择一种输出格式。", vm.Validate());

        vm.ExportMarkdown = true;
        Assert.Null(vm.Validate());
    }

    [Fact]
    public void Validate_CustomTemplateRequiresPrompt()
    {
        var vm = new TranslationConfigViewModel();
        vm.SelectedTemplate = vm.TemplateOptions.First(t => t.IsCustom);

        Assert.True(vm.IsCustomTemplate);
        Assert.Equal("已选「自定义」模板，请填写 Prompt 内容。", vm.Validate());

        vm.CustomPrompt = "把下文翻译为简洁的商务中文，保留数字与专名。";
        Assert.Null(vm.Validate());
    }

    [Fact]
    public void Validate_CostLimitMustBePositiveUnlessUnlimited()
    {
        var vm = new TranslationConfigViewModel { CostLimitTokens = 0 };

        Assert.Equal("成本上限必须大于 0，或勾选「不限」。", vm.Validate());

        vm.IsCostLimitUnlimited = true;
        Assert.False(vm.CostLimitEnabled);
        Assert.Null(vm.Validate());
        Assert.Equal("云端不限（不设成本硬上限）", vm.CostLimitSummary);
    }

    [Fact]
    public void Validate_GlossaryMustBeCsv()
    {
        var vm = new TranslationConfigViewModel { GlossaryPath = "C:/terms/table.txt" };

        Assert.Equal("术语表需为 CSV 文件（.csv）。", vm.Validate());

        vm.GlossaryPath = "C:/terms/table.csv";
        Assert.Null(vm.Validate());
    }

    [Fact]
    public void HighRiskTemplate_FlagsConfirmation()
    {
        var vm = new TranslationConfigViewModel();

        Assert.False(vm.IsHighRiskTemplate);
        Assert.False(vm.RequiresHighRiskConfirmation);

        vm.SelectedTemplate = vm.TemplateOptions.First(t => t.Id == "legal");
        Assert.True(vm.IsHighRiskTemplate);
        Assert.True(vm.RequiresHighRiskConfirmation);

        vm.SelectedTemplate = vm.TemplateOptions.First(t => t.Id == "medical");
        Assert.True(vm.RequiresHighRiskConfirmation);

        vm.SelectedTemplate = vm.TemplateOptions.First(t => t.Id == "technical");
        Assert.False(vm.RequiresHighRiskConfirmation);
    }

    [Fact]
    public void ProviderSwitch_TogglesLocalAndCloud()
    {
        var vm = new TranslationConfigViewModel();

        vm.SelectedProvider = "openai";
        Assert.True(vm.IsCloudProvider);
        Assert.False(vm.IsLocalProvider);
        Assert.Equal("OpenAI", vm.ProviderDisplayName);

        vm.SelectedProvider = TranslationConfigViewModel.DefaultProviderId;
        Assert.True(vm.IsLocalProvider);
        Assert.False(vm.IsCloudProvider);
        Assert.Equal("本地服务", vm.ProviderDisplayName);
    }

    // ---------------------------------------------------------------- v1.2 T9 供应商选择

    [Fact]
    public void AvailableProviders_CoverLocalFourteenPresetsAndSavedCustom()
    {
        var vm = new TranslationConfigViewModel();

        Assert.Equal(15, vm.AvailableProviders.Count); // 本地 + 14 家预设
        Assert.Equal("local", vm.AvailableProviders[0].Id);
        Assert.True(vm.AvailableProviders[0].IsLocal);
        Assert.Contains(vm.AvailableProviders, o => o.Id == "glm-coding-plan" && !o.IsCustom);
        Assert.DoesNotContain(vm.AvailableProviders, o => o.IsCustom);
    }

    [Fact]
    public void AvailableProviders_AppendSavedCustomByStableIdentity()
    {
        var custom = new TranslationProviderConfig
        {
            Provider = "Corp 网关",
            Endpoint = "https://gw.corp/llm/v1",
            Model = "my-model",
        };

        var vm = new TranslationConfigViewModel([custom]);

        Assert.Equal(16, vm.AvailableProviders.Count);
        var option = vm.AvailableProviders.First(o => o.IsCustom);
        Assert.Equal("corp 网关", option.Id); // 身份键与 T8 凭据寻址同源（自定义名统一小写）
        Assert.Equal("Corp 网关", option.DisplayName);
    }

    [Fact]
    public void AvailableProviders_SavedPresetDoesNotDuplicateListEntry()
    {
        var savedDeepSeek = new TranslationProviderConfig
        {
            Provider = "deepseek",
            Endpoint = "https://api.deepseek.com/v1",
            Model = "deepseek-chat",
            PresetId = "deepseek",
        };

        var vm = new TranslationConfigViewModel([savedDeepSeek]);

        Assert.Equal(15, vm.AvailableProviders.Count);
        Assert.Single(vm.AvailableProviders, o => o.Id == "deepseek");
    }

    [Fact]
    public void ProviderSwitch_DerivesProtocolAndBadge()
    {
        var vm = new TranslationConfigViewModel();
        var raised = new List<string>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is not null)
            {
                raised.Add(e.PropertyName);
            }
        };

        vm.SelectedProvider = "glm-coding-plan"; // 编程套餐：Anthropic 协议

        Assert.Equal(TranslationProviderConfig.ProtocolAnthropic, vm.Protocol);
        Assert.Equal(TranslationConfigViewModel.ProtocolAnthropicLabel, vm.ProtocolLabel);
        Assert.Equal("GLM Coding Plan", vm.ProviderDisplayName);
        Assert.Contains(nameof(TranslationConfigViewModel.Protocol), raised);
        Assert.Contains(nameof(TranslationConfigViewModel.ProtocolLabel), raised);
        Assert.Contains(nameof(TranslationConfigViewModel.IsCloudProvider), raised);

        vm.SelectedProvider = "openai";
        Assert.Equal(TranslationProviderConfig.ProtocolOpenAi, vm.Protocol);
        Assert.Equal(TranslationConfigViewModel.ProtocolOpenAiLabel, vm.ProtocolLabel);
    }

    [Fact]
    public void MixedProtocolProvider_ModelOverrideDrivesProtocol()
    {
        var vm = new TranslationConfigViewModel();

        vm.SelectedProvider = "opencode-go";
        Assert.Equal("minimax-m3", vm.SelectedModel); // 默认取首个预设模型
        Assert.Equal(TranslationProviderConfig.ProtocolAnthropic, vm.Protocol);

        vm.SelectedModel = "kimi-k3";
        Assert.Equal(TranslationProviderConfig.ProtocolOpenAi, vm.Protocol);

        vm.SelectedModel = "deepseek-v4-pro";
        Assert.Equal(TranslationProviderConfig.ProtocolOpenAi, vm.Protocol);

        vm.SelectedModel = "minimax-m2.7"; // 覆盖表命中（不在默认模型列表也可派生）
        Assert.Equal(TranslationProviderConfig.ProtocolAnthropic, vm.Protocol);

        vm.SelectedModel = "not-in-overrides";
        Assert.Equal(TranslationProviderConfig.ProtocolOpenAi, vm.Protocol); // 未命中回退供应商默认
    }

    [Fact]
    public void ModelProtocols_ExposeSelectedProviderOverrides()
    {
        var vm = new TranslationConfigViewModel();

        vm.SelectedProvider = "opencode-go";
        Assert.True(vm.ModelProtocols.Count >= 3);
        Assert.Equal(TranslationProviderConfig.ProtocolAnthropic, vm.ModelProtocols["minimax-m3"]);

        vm.SelectedProvider = "deepseek";
        Assert.Empty(vm.ModelProtocols); // 只读空表（无覆盖的供应商）
    }

    [Fact]
    public void LocalProvider_DerivesReadOnlyState()
    {
        var vm = new TranslationConfigViewModel();

        Assert.True(vm.IsLocalProvider);
        Assert.False(vm.IsCloudProvider);
        Assert.Equal(TranslationProviderConfig.ProtocolOpenAi, vm.Protocol);
        Assert.Equal(TranslationConfigViewModel.ProtocolOpenAiLabel, vm.ProtocolLabel);
        Assert.Empty(vm.ModelProtocols);
        Assert.Empty(vm.AvailableModels); // 本地模型由「拉取模型」填充
        Assert.Equal(TranslationConfigViewModel.NoModelLabel, vm.ModelLabel);
    }

    [Fact]
    public void BlankProviderSelection_FallsBackToLocal()
    {
        var vm = new TranslationConfigViewModel();

        vm.SelectedProvider = "openai";
        vm.SelectedProvider = ""; // WPF 绑定清空选择时的防御口径

        Assert.Equal(TranslationConfigViewModel.DefaultProviderId, vm.SelectedProvider);
        Assert.True(vm.IsLocalProvider);
        Assert.Equal("本地服务", vm.ProviderDisplayName);
    }

    [Fact]
    public void AvailableModels_PresetDefaults_AndFetchedFillForCustom()
    {
        var presetVm = new TranslationConfigViewModel { SelectedProvider = "deepseek" };
        Assert.Equal(new[] { "deepseek-chat" }, presetVm.AvailableModels);
        Assert.Equal("deepseek-chat", presetVm.SelectedModel);

        var custom = new TranslationProviderConfig
        {
            Provider = "Corp 网关",
            Endpoint = "https://gw.corp/llm/v1",
        };
        var customVm = new TranslationConfigViewModel([custom]);
        customVm.SelectedProvider = "corp 网关";
        Assert.Empty(customVm.AvailableModels); // 自定义：UI 调 T5 拉取填充
        Assert.Equal(TranslationConfigViewModel.NoModelLabel, customVm.ModelLabel);

        customVm.SetFetchedModels(["glm-4-flash", "glm-4-flash", " qwen-plus "]);
        Assert.Equal(new[] { "glm-4-flash", "qwen-plus" }, customVm.AvailableModels);
        Assert.Equal("glm-4-flash", customVm.SelectedModel);
    }

    [Fact]
    public void SavedConfig_SuppliesModelAndProtocolOverride()
    {
        var saved = new TranslationProviderConfig
        {
            Provider = "Corp 网关",
            Endpoint = "https://gw.corp/llm/v1",
            Model = "my-model",
            ApiProtocol = TranslationProviderConfig.ProtocolAnthropic,
        };

        var vm = new TranslationConfigViewModel([saved]);
        vm.SelectedProvider = "corp 网关";

        Assert.Equal("my-model", vm.SelectedModel); // 已保存模型优先于空列表
        Assert.Equal("my-model", vm.ModelLabel);
        Assert.Equal(TranslationProviderConfig.ProtocolAnthropic, vm.Protocol);
        Assert.Equal(TranslationConfigViewModel.ProtocolAnthropicLabel, vm.ProtocolLabel);
    }

    [Fact]
    public void SavedPresetConfig_StillUsesPresetModelOverrides()
    {
        var savedOpencodeGo = new TranslationProviderConfig
        {
            Provider = "opencode go",
            Endpoint = "https://opencode.ai/zen/go/v1",
            Model = "minimax-m3",
            PresetId = "opencode-go",
            ApiProtocol = TranslationProviderConfig.ProtocolOpenAi, // 未手改：等于预设默认
        };

        var vm = new TranslationConfigViewModel([savedOpencodeGo]);
        vm.SelectedProvider = "opencode-go";

        Assert.Equal("minimax-m3", vm.SelectedModel);
        Assert.Equal(TranslationProviderConfig.ProtocolAnthropic, vm.Protocol); // 预设按模型覆盖仍生效
    }

    [Fact]
    public void CostLimitSummary_ReflectsTokens()
    {
        var vm = new TranslationConfigViewModel();
        Assert.Equal("云端 ≤ 2.0M token / 任务", vm.CostLimitSummary);

        vm.CostLimitTokens = 500_000;
        Assert.Equal("云端 ≤ 0.5M token / 任务", vm.CostLimitSummary);
    }

    [Fact]
    public void ConfigChange_RaisesDerivedValidationNotifications()
    {
        var vm = new TranslationConfigViewModel();
        var raised = new List<string>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is not null)
            {
                raised.Add(e.PropertyName);
            }
        };

        vm.GenerateMono = false;
        vm.GenerateDual = false;

        Assert.Contains(nameof(TranslationConfigViewModel.ValidationMessage), raised);
        Assert.Contains(nameof(TranslationConfigViewModel.HasValidationError), raised);
        Assert.Contains(nameof(TranslationConfigViewModel.CanStart), raised);
        Assert.True(vm.HasValidationError);
    }

    [Fact]
    public void BackfillStrategy_IsReadOnlyDisplayText()
    {
        var vm = new TranslationConfigViewModel();

        Assert.Equal(TranslationConfigViewModel.BackfillStrategyText, vm.BackfillStrategySummary);
        Assert.Contains("缩字", vm.BackfillStrategySummary);
    }

    [Fact]
    public void ImplementsINotifyPropertyChanged_ForBinding()
    {
        var vm = new TranslationConfigViewModel();
        Assert.IsAssignableFrom<INotifyPropertyChanged>(vm);
    }
}
