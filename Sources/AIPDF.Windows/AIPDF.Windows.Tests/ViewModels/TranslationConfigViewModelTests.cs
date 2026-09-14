// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// v1.1 T13 共享翻译配置面板测试：默认值（本地引擎 / 自动检测源 / 中文目标 / 学术模板 / mono+dual）
// 与校验（目标语言显式、源≠目标、至少一种输出、自定义模板需正文、成本上限为正、术语表为 CSV）。
using System.ComponentModel;
using System.Linq;
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
        Assert.Equal(TranslationEngineKind.Local, vm.Engine); // 决策 #15：默认本地
        Assert.True(vm.IsLocalEngine);
        Assert.False(vm.IsCloudEngine);
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
    public void Engine_SwitchesBetweenLocalAndCloud()
    {
        var vm = new TranslationConfigViewModel();

        vm.IsCloudEngine = true;
        Assert.Equal(TranslationEngineKind.Cloud, vm.Engine);
        Assert.False(vm.IsLocalEngine);

        vm.IsLocalEngine = true;
        Assert.Equal(TranslationEngineKind.Local, vm.Engine);
        Assert.False(vm.IsCloudEngine);
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
