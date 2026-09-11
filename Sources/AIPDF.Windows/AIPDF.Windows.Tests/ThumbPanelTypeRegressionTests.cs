// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using AIPDF.Windows.ViewModels;
using Xunit;

namespace AIPDF.Windows.Tests;

/// <summary>
/// 回归守护：WPF 依赖属性类型崩溃。
///
/// <para>
/// 缺陷（2026-09-11 运行期崩溃，.NET Runtime 事件 1026 / APPCRASH）：
/// <c>Views/Reader/Panels/ThumbPanel.xaml</c> 用
/// <c>Width="{x:Static vm:ThumbPanelViewModel.ThumbWidth}"</c> 把它赋给
/// <see cref="System.Windows.FrameworkElement.Width"/>（<c>double</c> 依赖属性）。
/// 当 <c>ThumbWidth</c> 是 <c>int</c> 时，<c>x:Static</c> 不做类型转换（不像字面量
/// <c>Width="120"</c> 会走 LengthConverter），DP 值被装箱成 <see cref="int"/>；
/// 布局测量时 <c>FrameworkElement.get_Width()</c> 做 <c>(double)</c> 拆箱即抛
/// <see cref="System.InvalidCastException"/>（Unable to cast Int32 to Double），
/// 在 UI 线程的 <c>VirtualizingStackPanel.MeasureChild</c> 路径上未被捕获 →
/// 进程直接终止（打开任意 PDF 即复现）。
/// </para>
///
/// <para>
/// 修复：<c>ThumbWidth</c> 必须是 <c>double</c>。本测试锁死该类型，防止回退成整型。
/// </para>
/// </summary>
public sealed class ThumbPanelTypeRegressionTests
{
    [Fact]
    public void ThumbWidth_IsDouble_SoWpfWidthDependencyPropertyDoesNotCrash()
    {
        // int 会被 x:Static 装箱为 Int32 塞进 double DP → 测量期 InvalidCastException。
        Assert.IsType<double>(ThumbPanelViewModel.ThumbWidth);
    }

    /// <summary>
    /// 复刻 XAML <c>x:Static</c> 的真实语义：把常量装箱后直接写入 <c>Width</c>（double DP）再测量。
    /// 用真实 WPF 类型，因此修复前会以 <see cref="System.InvalidCastException"/> 失败（红），修复后通过。
    /// </summary>
    [Fact]
    public void ThumbWidth_WrittenToWidthDependencyProperty_MeasuresWithoutInvalidCast()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var grid = new Grid();
                grid.SetValue(FrameworkElement.WidthProperty, ThumbPanelViewModel.ThumbWidth);
                grid.Measure(new Size(1000, 1000));
                _ = grid.DesiredSize;
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(failure);
    }
}
