// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
using System.Windows;
using AIPDF.Windows.Bridge;
using AIPDF.Windows.Models;
using AIPDF.Windows.Tools;

namespace AIPDF.Windows.Views.Security;

/// <summary>
/// 安全保护组（解锁/加密/签名/遮盖/比较）的导航注册行（Wave 5 并行约定）：
/// MainWindow 构造时调用 Register() 向 ToolPageRegistry 添加本组解析器；
/// 选中安全组工具时由 TryCreate 构造页面并挂接视图模型。
/// 视图模型创建（CreateViewModel）与视图构造分离：纯 VM 部分可在无 STA 的测试环境断言。
/// </summary>
public static class SecurityPages
{
    /// <summary>MainWindow 构造时的本组导航注册行。</summary>
    public static void Register() => ToolPageRegistry.AddResolver(TryCreate);

    /// <summary>纯视图模型工厂（不构建 WPF 可视树）；非安全组工具返回 null（不认领）。</summary>
    internal static ToolViewModelBase? CreateViewModel(Tool tool, EngineBridge? bridge) => tool.Id switch
    {
        "unlock" => new UnlockViewModel(bridge, tool),
        "protect" => new ProtectViewModel(bridge, tool),
        "sign" => new SignViewModel(bridge, tool),
        "redact" => new RedactViewModel(bridge, tool),
        "compare" => new CompareViewModel(bridge, tool),
        _ => null,
    };

    internal static FrameworkElement? TryCreate(Tool tool, EngineBridge? bridge) => CreateViewModel(tool, bridge) switch
    {
        UnlockViewModel viewModel => new UnlockPage { DataContext = viewModel },
        ProtectViewModel viewModel => new ProtectPage { DataContext = viewModel },
        SignViewModel viewModel => new SignPage { DataContext = viewModel },
        RedactViewModel viewModel => new RedactPage { DataContext = viewModel },
        CompareViewModel viewModel => new ComparePage { DataContext = viewModel },
        _ => null,
    };
}
