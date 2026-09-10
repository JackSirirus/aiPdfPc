// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
// T3 阅读视图 code-behind：仅做「ViewModel 无法触及的视图职责」——
//   滚动/视口上报、滚动请求执行、Ctrl+滚轮档位转发、页码输入焦点与红边抖动动画。
// 业务规则（渲染请求、缩放阶梯、页码换算）全部在 ReaderViewModel / PageLayoutCalculator。
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using AIPDF.Windows.ViewModels;

namespace AIPDF.Windows.Views.Reader;

/// <summary>
/// 虚拟化连续阅读视图。可独立实例化预览：
/// <c>new ReaderView()</c> 使用自带空模型；宿主（T5 标签）用 <c>new ReaderView(viewModel)</c> 注入。
/// </summary>
public partial class ReaderView : UserControl
{
    private readonly ReaderViewModel _viewModel;

    /// <summary>Ctrl+滚轮高分辨率增量累积（一个物理档位 = 120 delta）。</summary>
    private double _wheelAccum;

    public ReaderView() : this(new ReaderViewModel())
    {
    }

    /// <summary>宿主注入构造：DataContext 由本视图接管（预览与 T5 宿主统一走这里）。</summary>
    public ReaderView(ReaderViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = _viewModel;

        _viewModel.ScrollRequested += OnViewModelScrollRequested;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        Loaded += (_, _) => ScrollHost.Focus();
    }

    /// <summary>视图持有的阅读模型（供宿主联动：页码、缩放、跳页等）。</summary>
    public ReaderViewModel ViewModel => _viewModel;

    // ------------------------------------------------------------ 滚动 ↔ 模型

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (ScrollHost.ViewportHeight <= 0) return; // 尚未完成测量
        _viewModel.UpdateViewport(ScrollHost.VerticalOffset, ScrollHost.ViewportHeight, ScrollHost.ViewportWidth);
    }

    private void OnViewModelScrollRequested(object? sender, ReaderScrollRequestEventArgs e)
    {
        // 缩放改写各页尺寸后需等一拍布局更新，否则立即滚动会被旧 extent 截断
        Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            new Action(() => ScrollHost.ScrollToVerticalOffset(e.Offset)));
    }

    // ------------------------------------------------------------ Ctrl+滚轮缩放（FR-06）

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;

        e.Handled = true;
        _wheelAccum += e.Delta;
        var steps = (int)(_wheelAccum / 120.0);
        if (steps == 0) return;
        _wheelAccum -= steps * 120.0;
        _viewModel.ZoomByWheelSteps(steps);
    }

    // ------------------------------------------------------------ 页码跳转（FR-05）

    private void OnJumpBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            _viewModel.JumpToPageCommand.Execute(null);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            _viewModel.JumpText = "";
            _viewModel.IsJumpInvalid = false;
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Ctrl+G 定位页码输入框（FR-05）
        if (e.Key == Key.G && Keyboard.Modifiers == ModifierKeys.Control && !JumpBox.IsKeyboardFocused)
        {
            e.Handled = true;
            JumpBox.Focus();
            JumpBox.SelectAll();
        }
    }

    /// <summary>非法页码红边抖动（FR-05 AC-05.2）。</summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ReaderViewModel.IsJumpInvalid) && _viewModel.IsJumpInvalid)
        {
            PlayInvalidJumpAnimation();
        }
    }

    private void PlayInvalidJumpAnimation()
    {
        if (JumpBox.RenderTransform is not TranslateTransform transform)
        {
            transform = new TranslateTransform();
            JumpBox.RenderTransform = transform;
        }

        var shake = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(320) };
        shake.KeyFrames.Add(new LinearDoubleKeyFrame(0, TimeSpan.Zero));
        shake.KeyFrames.Add(new LinearDoubleKeyFrame(-5, TimeSpan.FromMilliseconds(60)));
        shake.KeyFrames.Add(new LinearDoubleKeyFrame(5, TimeSpan.FromMilliseconds(120)));
        shake.KeyFrames.Add(new LinearDoubleKeyFrame(-4, TimeSpan.FromMilliseconds(180)));
        shake.KeyFrames.Add(new LinearDoubleKeyFrame(4, TimeSpan.FromMilliseconds(240)));
        shake.KeyFrames.Add(new LinearDoubleKeyFrame(0, TimeSpan.FromMilliseconds(300)));
        transform.BeginAnimation(TranslateTransform.XProperty, shake);
    }
}
