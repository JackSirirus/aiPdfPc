// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 AIPDF contributors
using System.Globalization;
using System.IO;
using AIPDF.Windows.Controls;
using AIPDF.Windows.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AIPDF.Windows.Tools;

/// <summary>
/// 编辑组（catalog「编辑内容」：rotate / numbers / watermark / crop / edit / forms）的工具页视图模型。
/// 参数默认值与可选值一律取自 Tool.Fields（backend/catalog.json），不在代码里写第二份清单；
/// 数值范围、页码合法性、区域是否越界由引擎裁决（引擎是权威），视图模型只做必填与存在性预检。
/// 参数值以 catalog 约定的字符串经引擎桥 stdin（UTF-8 JSON）传递，中文往返无损由桥层契约保证。
/// </summary>
public delegate Task ExecuteEditorToolDelegate(
    EditorToolViewModel viewModel,
    IReadOnlyList<string> files,
    string outputDir,
    CancellationToken cancellationToken);

/// <summary>编辑组工具页的公共基类：文件/输出目录选择、页面范围、执行命令与错误展示。</summary>
public abstract partial class EditorToolViewModel : ObservableObject, ICurrentDocumentPinnable, IToolStartGated
{
    private readonly ExecuteEditorToolDelegate _execute;

    protected EditorToolViewModel(Tool tool, ExecuteEditorToolDelegate execute)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(execute);
        Tool = tool;
        _execute = execute;
        ExecuteCommand = new AsyncRelayCommand(ExecuteRunAsync, () => CanExecuteRun);
    }

    /// <summary>
    /// V2 T10 FR-21：启动前门禁（宿主在承载页面时挂接）。编辑组不经 T7 队列，故门禁挂在执行入口。
    /// 返回 false（如未保存批注 flush 失败且用户取消）则阻止本次执行。
    /// </summary>
    public Func<CancellationToken, Task<bool>>? StartGate { get; set; }

    /// <summary>目录中的工具条目（名称、副标题、说明与参数默认值来源）。</summary>
    public Tool Tool { get; }

    /// <summary>本次处理的 PDF 文件（编辑组每次处理一份文件）。</summary>
    [ObservableProperty]
    private string _inputFile = "";

    /// <summary>输出文件夹（引擎在其下新建 AIPDF-日期-编号 任务目录，不覆盖原件）。</summary>
    [ObservableProperty]
    private string _outputDir = "";

    /// <summary>页面范围（1 基，留空为全部；例如 1-3,5。框选会填充框选所在页）。</summary>
    [ObservableProperty]
    private string _pages = "";

    // ---------- 浮菜单「用工具处理」置顶/预填（V2 T2） ----------

    /// <summary>被浮菜单固定的当前文档路径（null = 无）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPinnedDocument))]
    [NotifyPropertyChangedFor(nameof(PinnedDocumentLabel))]
    private string? _pinnedDocumentPath;

    /// <summary>是否有「当前文档」标记（XAML 徽标显隐）。</summary>
    public bool HasPinnedDocument => !string.IsNullOrEmpty(PinnedDocumentPath);

    /// <summary>「当前文档：文件名」标记文案。</summary>
    public string? PinnedDocumentLabel => HasPinnedDocument
        ? $"当前文档：{System.IO.Path.GetFileName(PinnedDocumentPath!)}"
        : null;

    /// <summary>是否有页范围字段可预填（基类默认无；rotate 覆写为 true）。</summary>
    public virtual bool SupportsPageRangePrefill => false;

    /// <summary>预填页范围（基类默认空实现）。</summary>
    public virtual void PrefillPageRange(int startPage, int endPage)
    {
    }

    /// <summary>把当前文档写入 InputFile、标记「当前文档」（浮菜单子菜单入口；编辑组每次处理一份）。</summary>
    public void PinCurrentDocument(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        string fullPath;
        try { fullPath = System.IO.Path.GetFullPath(path); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException) { return; }

        InputFile = fullPath;
        if (string.IsNullOrWhiteSpace(OutputDir))
        {
            OutputDir = System.IO.Path.GetDirectoryName(fullPath) ?? "";
        }

        PinnedDocumentPath = fullPath;
    }

    /// <summary>执行前的参数预检错误；引擎错误经状态栏展示，不在这里重复。</summary>
    [ObservableProperty]
    private string _errorText = "";

    public IAsyncRelayCommand ExecuteCommand { get; }

    partial void OnInputFileChanged(string value) => ExecuteCommand.NotifyCanExecuteChanged();

    partial void OnOutputDirChanged(string value) => ExecuteCommand.NotifyCanExecuteChanged();

    private bool CanExecuteRun => !IsBusy && !string.IsNullOrWhiteSpace(InputFile) && !string.IsNullOrWhiteSpace(OutputDir);

    /// <summary>执行中标志：防重入（编辑组一次只跑一个引擎任务）。</summary>
    [ObservableProperty]
    private bool _isBusy;

    partial void OnIsBusyChanged(bool value) => ExecuteCommand.NotifyCanExecuteChanged();

    /// <summary>参数与 catalog 字段的映射。键与值形状与 backend/engine.py 的读取方式一致。</summary>
    public abstract Dictionary<string, object?> BuildOptions();

    /// <summary>执行前预检；返回 null 表示通过。引擎仍是最终裁决者。</summary>
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(InputFile))
        {
            return "请先选择要处理的 PDF 文件。";
        }

        if (!File.Exists(InputFile))
        {
            return $"文件不存在：{InputFile}";
        }

        if (string.IsNullOrWhiteSpace(OutputDir))
        {
            return "请选择输出文件夹。";
        }

        if (!Directory.Exists(OutputDir))
        {
            return $"输出文件夹不存在：{OutputDir}";
        }

        return ValidateParameters();
    }

    /// <summary>工具特有参数预检；返回 null 表示通过。</summary>
    protected virtual string? ValidateParameters() => null;

    /// <summary>catalog 字段默认值（field() 的 value 输出，恒为字符串）。</summary>
    protected string FieldDefault(string key) =>
        Tool.Fields.FirstOrDefault(field => field.Key == key)?.Value ?? "";

    /// <summary>catalog 字段可选值（field() 的 choices 输出）。</summary>
    protected IReadOnlyList<string> FieldChoices(string key) =>
        Tool.Fields.FirstOrDefault(field => field.Key == key)?.Choices ?? [];

    private async Task ExecuteRunAsync()
    {
        ErrorText = "";
        if (Validate() is { } error)
        {
            ErrorText = error;
            return;
        }

        // V2 T10 FR-21：先经启动门禁（强制 flush 未保存批注）；失败且用户取消则不启动引擎。
        if (StartGate is { } gate && !await gate(CancellationToken.None))
        {
            ErrorText = "批注保存失败，工具未启动。";
            return;
        }

        IsBusy = true;
        try
        {
            await _execute(this, [InputFile], OutputDir, CancellationToken.None);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>点值转目录约定字符串（不变文化，最多两位小数；引擎 number() 负责范围校验）。</summary>
    protected static string FormatPoint(double value) =>
        value.ToString("0.##", CultureInfo.InvariantCulture);

    internal static string OrDefault(string value, string fallback) =>
        string.IsNullOrEmpty(value) ? fallback : value;
}

/// <summary>
/// 需要在预览中框选矩形的工具（crop / edit / forms）。
/// PdfPreviewControl 的 RegionSelected 事件给出「可见页、左上原点、pt」的区域
/// （与 backend/engine.py area() 坐标约定一致），这里原样转成 x/y/width/height 参数，
/// 不做任何坐标翻转；拖拽方向的规范化由预览控件完成。
/// </summary>
public abstract partial class RegionEditorViewModel : EditorToolViewModel
{
    private readonly double _defaultX;
    private readonly double _defaultY;
    private readonly double _defaultWidth;
    private readonly double _defaultHeight;

    protected RegionEditorViewModel(Tool tool, ExecuteEditorToolDelegate execute) : base(tool, execute)
    {
        _defaultX = ParsePoint(FieldDefault("x"), 48);
        _defaultY = ParsePoint(FieldDefault("y"), 72);
        _defaultWidth = ParsePoint(FieldDefault("width"), 240);
        _defaultHeight = ParsePoint(FieldDefault("height"), 64);
        _x = _defaultX;
        _y = _defaultY;
        _width = _defaultWidth;
        _height = _defaultHeight;
    }

    /// <summary>区域左上角 x（pt，可见页左上原点）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectionSummary))]
    private double _x;

    /// <summary>区域左上角 y（pt，可见页左上原点）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectionSummary))]
    private double _y;

    /// <summary>区域宽（pt）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectionSummary))]
    private double _width;

    /// <summary>区域高（pt）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectionSummary))]
    private double _height;

    /// <summary>当前矩形是否来自一次框选（手动填写坐标时不置位）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectionSummary))]
    private bool _hasSelection;

    /// <summary>框选结果摘要；未框选时为空。</summary>
    public string SelectionSummary => HasSelection
        ? $"已框选：第 {Pages} 页 · x {FormatPoint(X)} · y {FormatPoint(Y)} · 宽 {FormatPoint(Width)} · 高 {FormatPoint(Height)}（pt）"
        : "";

    /// <summary>消费预览框选事件：填充页面范围（框选所在页，1 基）与矩形参数。</summary>
    public void ApplySelection(PdfRegionSelectedEventArgs selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        Pages = (selection.PageIndex + 1).ToString(CultureInfo.InvariantCulture);
        X = Math.Round(Math.Max(0, selection.RegionPt.X), 2);
        Y = Math.Round(Math.Max(0, selection.RegionPt.Y), 2);
        Width = Math.Round(selection.RegionPt.Width, 2);
        Height = Math.Round(selection.RegionPt.Height, 2);
        HasSelection = true;
    }

    /// <summary>清除框选：矩形回到 catalog 默认值，页面范围保留用户输入。</summary>
    public void ClearSelection()
    {
        X = _defaultX;
        Y = _defaultY;
        Width = _defaultWidth;
        Height = _defaultHeight;
        HasSelection = false;
    }

    /// <summary>矩形参数键与 area() 的读取键一致：x / y / width / height。</summary>
    protected void AddRectOptions(Dictionary<string, object?> options)
    {
        options["x"] = FormatPoint(X);
        options["y"] = FormatPoint(Y);
        options["width"] = FormatPoint(Width);
        options["height"] = FormatPoint(Height);
    }

    private static double ParsePoint(string value, double fallback) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
}

/// <summary>旋转 PDF：角度照 catalog（90 / 180 / 270，顺时针）。</summary>
public sealed partial class RotateViewModel : EditorToolViewModel
{
    public RotateViewModel(Tool tool, ExecuteEditorToolDelegate execute) : base(tool, execute)
    {
        _angle = OrDefault(FieldDefault("angle"), "90");
        Angles = FieldChoices("angle");
    }

    /// <summary>顺时针旋转角度（catalog choice）。</summary>
    [ObservableProperty]
    private string _angle;

    public IReadOnlyList<string> Angles { get; }

    public override Dictionary<string, object?> BuildOptions() => new()
    {
        ["pages"] = Pages,
        ["angle"] = Angle,
    };

    /// <summary>浮菜单「旋转」有页范围字段（Pages），可预填当前页（H-6）。</summary>
    public override bool SupportsPageRangePrefill => true;

    /// <summary>预填页面范围为「startPage-endPage」（1 基）。</summary>
    public override void PrefillPageRange(int startPage, int endPage)
        => Pages = $"{startPage}-{endPage}";
}

/// <summary>添加页码：格式 / 起始编号 / 位置 / 字号照 catalog。</summary>
public sealed partial class PageNumbersViewModel : EditorToolViewModel
{
    public PageNumbersViewModel(Tool tool, ExecuteEditorToolDelegate execute) : base(tool, execute)
    {
        _format = OrDefault(FieldDefault("format"), "{n} / {total}");
        _start = OrDefault(FieldDefault("start"), "1");
        _position = OrDefault(FieldDefault("position"), "bottom-center");
        _fontsize = OrDefault(FieldDefault("fontsize"), "11");
        Positions = FieldChoices("position");
    }

    /// <summary>页码格式；{n} 为所选页内的序号、{total} 为所选页数（引擎替换）。</summary>
    [ObservableProperty]
    private string _format;

    /// <summary>起始编号。</summary>
    [ObservableProperty]
    private string _start;

    /// <summary>页码位置（catalog choice：top-left … bottom-right）。</summary>
    [ObservableProperty]
    private string _position;

    /// <summary>字号。</summary>
    [ObservableProperty]
    private string _fontsize;

    public IReadOnlyList<string> Positions { get; }

    public override Dictionary<string, object?> BuildOptions() => new()
    {
        ["pages"] = Pages,
        ["format"] = Format,
        ["start"] = Start,
        ["position"] = Position,
        ["fontsize"] = Fontsize,
    };
}

/// <summary>添加水印：文字或图片二选一；中文文本经 UTF-8 stdin 往返无损。</summary>
public sealed partial class WatermarkViewModel : EditorToolViewModel
{
    public WatermarkViewModel(Tool tool, ExecuteEditorToolDelegate execute) : base(tool, execute)
    {
        _text = OrDefault(FieldDefault("text"), "");
        _position = OrDefault(FieldDefault("position"), "bottom-center");
        _opacity = OrDefault(FieldDefault("opacity"), "0.2");
        _fontsize = OrDefault(FieldDefault("fontsize"), "42");
        Positions = FieldChoices("position");
    }

    /// <summary>水印文字（中文安全：参数只经 stdin JSON，不进命令行）。</summary>
    [ObservableProperty]
    private string _text;

    /// <summary>图片水印路径（可选；填写时优先于文字水印）。</summary>
    [ObservableProperty]
    private string _imagePath = "";

    /// <summary>水印位置（catalog choice）。</summary>
    [ObservableProperty]
    private string _position;

    /// <summary>不透明度（0—1）。</summary>
    [ObservableProperty]
    private string _opacity;

    /// <summary>字号。</summary>
    [ObservableProperty]
    private string _fontsize;

    public IReadOnlyList<string> Positions { get; }

    public override Dictionary<string, object?> BuildOptions()
    {
        var options = new Dictionary<string, object?>
        {
            ["pages"] = Pages,
            ["text"] = Text,
            ["position"] = Position,
            ["opacity"] = Opacity,
            ["fontsize"] = Fontsize,
        };
        // 引擎以 options.get("image") 真值判断走图片水印；留空时不发送该键。
        if (!string.IsNullOrWhiteSpace(ImagePath))
        {
            options["image"] = ImagePath;
        }

        return options;
    }

    protected override string? ValidateParameters() =>
        string.IsNullOrWhiteSpace(Text) && string.IsNullOrWhiteSpace(ImagePath)
            ? "请输入水印文字或选择图片水印。"
            : null;
}

/// <summary>裁剪 PDF：区域可从预览框选，也可按 pt 手填（1 点 = 1/72 英寸）。</summary>
public sealed class CropViewModel : RegionEditorViewModel
{
    public CropViewModel(Tool tool, ExecuteEditorToolDelegate execute) : base(tool, execute)
    {
    }

    public override Dictionary<string, object?> BuildOptions()
    {
        var options = new Dictionary<string, object?> { ["pages"] = Pages };
        AddRectOptions(options);
        return options;
    }
}

/// <summary>编辑 PDF（添加文字、图片、形状和标注）：模式照 catalog，位置可框选。</summary>
public sealed partial class EditViewModel : RegionEditorViewModel
{
    public EditViewModel(Tool tool, ExecuteEditorToolDelegate execute) : base(tool, execute)
    {
        _mode = OrDefault(FieldDefault("mode"), "text");
        _fontsize = OrDefault(FieldDefault("fontsize"), "18");
        _color = OrDefault(FieldDefault("color"), "#3159D9");
        Modes = FieldChoices("mode");
    }

    /// <summary>编辑方式（catalog choice：text / image / rectangle / highlight / ink）。</summary>
    [ObservableProperty]
    private string _mode;

    /// <summary>文字内容（mode=text 时必填）。</summary>
    [ObservableProperty]
    private string _text = "";

    /// <summary>图片文件路径（mode=image 时必填）。</summary>
    [ObservableProperty]
    private string _imagePath = "";

    /// <summary>字号。</summary>
    [ObservableProperty]
    private string _fontsize;

    /// <summary>颜色（#RRGGBB，引擎 rgb() 校验格式）。</summary>
    [ObservableProperty]
    private string _color;

    public IReadOnlyList<string> Modes { get; }

    public override Dictionary<string, object?> BuildOptions()
    {
        var options = new Dictionary<string, object?>
        {
            ["pages"] = Pages,
            ["mode"] = Mode,
            ["text"] = Text,
            ["fontsize"] = Fontsize,
            ["color"] = Color,
        };
        if (!string.IsNullOrWhiteSpace(ImagePath))
        {
            options["image"] = ImagePath;
        }

        AddRectOptions(options);
        return options;
    }

    protected override string? ValidateParameters()
    {
        if (Mode == "text" && string.IsNullOrWhiteSpace(Text))
        {
            return "请输入文字内容。";
        }

        return Mode == "image" && string.IsNullOrWhiteSpace(ImagePath) ? "请选择要添加的图片。" : null;
    }
}

/// <summary>
/// PDF 表单：填写模式按字段名→值构造 formValues（引擎契约），创建模式在指定位置新建字段。
/// 目录 note：导入文件后自动读取字段；填写保留交互性。字段列表的读取属文件外壳职责，
/// 本页提供字段名/值手填通道，契约与引擎 form_update() 一致，不发明新参数。
/// </summary>
public sealed partial class FormsViewModel : RegionEditorViewModel
{
    public FormsViewModel(Tool tool, ExecuteEditorToolDelegate execute) : base(tool, execute)
    {
        _formMode = OrDefault(FieldDefault("formMode"), "fill");
        _fieldName = OrDefault(FieldDefault("fieldName"), "");
        _fieldType = OrDefault(FieldDefault("fieldType"), "text");
        _fieldValue = FieldDefault("fieldValue");
        _choices = FieldDefault("choices");
        FormModes = FieldChoices("formMode");
        FieldTypes = FieldChoices("fieldType");
    }

    /// <summary>操作：fill（填写既有字段）或 create（新建字段）。</summary>
    [ObservableProperty]
    private string _formMode;

    /// <summary>字段名称（fill 模式下是既有字段名；create 模式下是新字段名）。</summary>
    [ObservableProperty]
    private string _fieldName;

    /// <summary>字段类型（catalog choice：text / checkbox / list / radio，仅 create）。</summary>
    [ObservableProperty]
    private string _fieldType;

    /// <summary>字段值（fill 模式为要写入的值；create 模式为默认值；checkbox 填 true/false）。</summary>
    [ObservableProperty]
    private string _fieldValue;

    /// <summary>选项（每行一个；list / radio 需要，仅 create）。</summary>
    [ObservableProperty]
    private string _choices;

    public IReadOnlyList<string> FormModes { get; }

    public IReadOnlyList<string> FieldTypes { get; }

    public override Dictionary<string, object?> BuildOptions()
    {
        if (FormMode == "create")
        {
            var options = new Dictionary<string, object?>
            {
                ["formMode"] = "create",
                ["fieldName"] = FieldName,
                ["fieldType"] = FieldType,
                ["fieldValue"] = FieldValue,
                ["choices"] = Choices,
                ["pages"] = Pages,
            };
            AddRectOptions(options);
            return options;
        }

        // fill：引擎读取 options["formValues"]（字段名 → 值）；一次填写一个字段。
        return new Dictionary<string, object?>
        {
            ["formMode"] = "fill",
            ["formValues"] = new Dictionary<string, object?> { [FieldName] = FieldValue },
        };
    }

    protected override string? ValidateParameters()
    {
        if (string.IsNullOrWhiteSpace(FieldName))
        {
            return "请输入字段名称。";
        }

        if (FormMode != "create")
        {
            return null;
        }

        var optionCount = Choices.Split('\n').Count(line => !string.IsNullOrWhiteSpace(line));
        return FieldType switch
        {
            "list" when optionCount < 1 => "列表字段需要至少一个选项。",
            "radio" when optionCount < 2 => "单选组至少需要两个选项。",
            _ => null,
        };
    }
}
