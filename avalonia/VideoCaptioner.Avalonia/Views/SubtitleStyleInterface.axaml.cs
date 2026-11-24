using System.Globalization;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using FluentAvalonia.UI.Controls;
using VideoCaptioner.Avalonia.Services;
using VideoCaptioner.Avalonia.Views.Style;

namespace VideoCaptioner.Avalonia.Views;

/// <summary>
/// Port of videocaptioner/ui/view/subtitle_style_interface.py.
/// Presets live in SUBTITLE_STYLE_PATH (ass-*.json / rounded-*.json, shared with the Python app);
/// previews are rendered by the backend (render_ass_preview / render_preview) through bridge/handlers/style.py.
/// </summary>
public partial class SubtitleStyleInterface : UserControl, IWindowTitleProvider
{
    private const string Ctx = "SubtitleStyleInterface";
    private const int InfobarDurationSuccess = 2000;
    private const int InfobarDurationWarning = 5000;

    /// <summary>PERVIEW_TEXTS: key -> (original, translation).</summary>
    private static readonly (string Key, string Main, string Sub)[] PreviewTexts =
    [
        ("长文本", "This is a long text for testing subtitle preview, text wrapping, and style settings.",
            "这是一段用于测试字幕预览、自动换行以及样式设置的较长文本内容。"),
        ("中文本", "Welcome to apply for the prestigious South China Normal University!", "欢迎报考百年名校华南师范大学"),
        ("短文本", "Elementary school students know this", "小学二年级的都知道"),
    ];

    private static readonly string[] LayoutTexts = ["译文在上", "原文在上", "仅译文", "仅原文"];
    private static readonly string[] OrientationTexts = ["横屏", "竖屏"];
    private const string AssStyleMode = "ASS 样式";
    private const string RoundedBgMode = "圆角背景";

    // 控制是否触发样式变更回调（加载样式时禁用）
    private bool _loadingStyle;
    // signals are connected after __setValues (initial loading must not save or re-render)
    private bool _signalsConnected;
    // comboBox.blockSignals(True) on the style name combo box
    private bool _styleComboSignalsBlocked;
    private bool _renderModeSignalsBlocked;

    private string _assetsPath = "";
    private string _styleDir = "";

    // Preview rendering: one backend render at a time, the newest request wins.
    private bool _previewBusy;
    private bool _previewPending;

    public string? WindowTitle => I18n.Tr("字幕样式配置", Ctx);

    public SubtitleStyleInterface()
    {
        InitializeComponent();

        RenderModeComboBox.ItemsSource = Models.Options.SubtitleRenderMode;
        LayoutComboBox.ItemsSource = LayoutTexts;
        PreviewTextComboBox.ItemsSource = PreviewTexts.Select(p => p.Key).ToArray();
        PreviewTextComboBox.SelectedIndex = 0;
        OrientationComboBox.ItemsSource = OrientationTexts;
        OrientationComboBox.SelectedIndex = 0;

        // ColorSettingCard initial colours
        AssPrimaryColorButton.Color = Colors.White;
        AssPrimaryOutlineColorButton.Color = Colors.Black;
        AssSecondaryColorButton.Color = Colors.White;
        AssSecondaryOutlineColorButton.Color = Colors.Black;
        RoundedTextColorButton.Color = Colors.White;
        RoundedBgColorButton.Color = Color.FromArgb(200, 25, 25, 25);

        foreach (var cb in new[] { AssPrimaryFontComboBox, AssSecondaryFontComboBox, RoundedFontComboBox })
            cb.MaxDropDownHeight = 12 * 34; // setMaxVisibleItems(12)

        // The preview stage follows the width of the preview card and the height left in the column.
        PreviewColumn.SizeChanged += (_, _) => UpdatePreviewImage();
        PreviewTopWidget.SizeChanged += (_, e) => { if (e.WidthChanged) UpdatePreviewImage(); };
        PreviewHeader.SizeChanged += (_, e) => { if (e.HeightChanged) UpdatePreviewImage(); };
        PreviewBottomLayout.SizeChanged += (_, e) => { if (e.HeightChanged) UpdatePreviewImage(); };

        AddHandler(DragDrop.DragEnterEvent, OnDragOver);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        AddHandler(DragDrop.DropEvent, OnDrop);

        _ = InitAsync();
    }

    private async Task InitAsync()
    {
        try
        {
            await Bridge.Instance.Ready;
            var paths = await Bridge.Instance.RequestAsync("paths");
            _assetsPath = paths?["ASSETS_PATH"]?.GetValue<string>() ?? "";
            _styleDir = paths?["SUBTITLE_STYLE_PATH"]?.GetValue<string>() ?? "";
            await SetValuesAsync();
        }
        catch (Exception e)
        {
            Toast.Error(I18n.Tr("错误", Ctx), e.Message);
        }
        ConnectSignals();
    }

    // ------------------------------------------------------------------ init

    /// <summary>__setValues</summary>
    private async Task SetValuesAsync()
    {
        SetComboText(RenderModeComboBox, Cfg.SubtitleRenderMode.Value);
        SetComboText(LayoutComboBox, Cfg.SubtitleLayout.Value);

        // 获取字体列表（内置字体 + 系统字体）
        var fonts = new List<string>();
        try
        {
            if (await Bridge.Instance.RequestAsync("style_fonts") is JsonArray arr)
                fonts.AddRange(arr.Select(n => n?.GetValue<string>() ?? "").Where(s => s.Length > 0));
        }
        catch (BridgeException e)
        {
            Toast.Error(I18n.Tr("错误", Ctx), e.Message);
        }
        foreach (var cb in new[] { AssPrimaryFontComboBox, AssSecondaryFontComboBox, RoundedFontComboBox })
        {
            cb.ItemsSource = fonts.ToList();
            if (fonts.Count > 0) cb.SelectedIndex = 0;
        }

        // 设置圆角背景模式的初始值
        RoundedFontSizeSpinBox.Value = Cfg.RoundedBgFontSize.Value;
        RoundedCornerRadiusSpinBox.Value = Cfg.RoundedBgCornerRadius.Value;
        RoundedPaddingHSpinBox.Value = Cfg.RoundedBgPaddingH.Value;
        RoundedPaddingVSpinBox.Value = Cfg.RoundedBgPaddingV.Value;
        RoundedMarginBottomSpinBox.Value = Cfg.RoundedBgMarginBottom.Value;
        RoundedLineSpacingSpinBox.Value = Cfg.RoundedBgLineSpacing.Value;
        RoundedLetterSpacingSpinBox.Value = Cfg.RoundedBgLetterSpacing.Value;

        // 设置颜色
        RoundedTextColorButton.Color = StyleColorButton.ParseQColor(Cfg.RoundedBgTextColor.Value);
        RoundedBgColorButton.Color = StyleColorButton.ParseRgbaHex(Cfg.RoundedBgColor.Value);

        // 加载样式列表（根据当前模式）
        await RefreshStyleListAsync();

        // 根据当前渲染模式显示/隐藏设置组
        UpdateVisibleGroups();
    }

    /// <summary>connectSignals</summary>
    private void ConnectSignals()
    {
        RenderModeComboBox.SelectionChanged += (_, _) => { if (!_renderModeSignalsBlocked) _ = OnRenderModeChangedAsync(); };

        LayoutComboBox.SelectionChanged += (_, _) =>
        {
            var text = LayoutComboBox.SelectedItem as string;
            if (text == null) return;
            UpdatePreview();
            Cfg.SubtitleLayout.Value = text;
            SignalBus.EmitSubtitleLayoutChanged(text);
        };
        AssVerticalSpacingSpinBox.ValueChanged += (_, _) => OnAssSettingChanged();

        foreach (var cb in new[] { AssPrimaryFontComboBox, AssSecondaryFontComboBox })
            cb.SelectionChanged += (_, _) => OnAssSettingChanged();
        foreach (var nb in new[] { AssPrimarySizeSpinBox, AssPrimarySpacingSpinBox, AssPrimaryOutlineSizeSpinBox,
                     AssSecondarySizeSpinBox, AssSecondarySpacingSpinBox, AssSecondaryOutlineSizeSpinBox })
            nb.ValueChanged += (_, _) => OnAssSettingChanged();
        foreach (var btn in new[] { AssPrimaryColorButton, AssPrimaryOutlineColorButton,
                     AssSecondaryColorButton, AssSecondaryOutlineColorButton })
            btn.ColorChanged += _ => OnAssSettingChanged();

        RoundedFontComboBox.SelectionChanged += (_, _) => OnRoundedBgSettingChanged();
        foreach (var nb in new[] { RoundedFontSizeSpinBox, RoundedCornerRadiusSpinBox, RoundedPaddingHSpinBox,
                     RoundedPaddingVSpinBox, RoundedMarginBottomSpinBox, RoundedLineSpacingSpinBox,
                     RoundedLetterSpacingSpinBox })
            nb.ValueChanged += (_, _) => OnRoundedBgSettingChanged();
        RoundedTextColorButton.ColorChanged += _ => OnRoundedBgSettingChanged();
        RoundedBgColorButton.ColorChanged += _ => OnRoundedBgSettingChanged();

        PreviewTextComboBox.SelectionChanged += (_, _) => UpdatePreview();
        OrientationComboBox.SelectionChanged += (_, _) => OnOrientationChanged();
        PreviewImageButton.Click += async (_, _) => await SelectPreviewImageAsync();

        StyleNameComboBox.SelectionChanged += (_, _) =>
        {
            if (_styleComboSignalsBlocked) return;
            if (StyleNameComboBox.SelectedItem is string name) _ = LoadStyleAsync(name);
        };
        NewStyleButton.Click += async (_, _) => await CreateNewStyleAsync();
        OpenStyleFolderButton.Click += (_, _) => Dialogs.OpenFolder(_styleDir);

        SignalBus.SubtitleLayoutChanged += OnSubtitleLayoutChanged;
        SignalBus.SubtitleRenderModeChanged += OnRenderModeChangedExternal;
        _signalsConnected = true;
    }

    // ------------------------------------------------------------------ signal handlers

    private void OnSubtitleLayoutChanged(string layout)
    {
        Cfg.SubtitleLayout.Value = layout;
        if (LayoutComboBox.SelectedItem as string != layout) SetComboText(LayoutComboBox, layout);
    }

    /// <summary>处理外部渲染模式变更（从视频合成界面同步）</summary>
    private void OnRenderModeChangedExternal(string modeText)
    {
        _renderModeSignalsBlocked = true;
        SetComboText(RenderModeComboBox, modeText);
        _renderModeSignalsBlocked = false;
        UpdateVisibleGroups();
        _ = RefreshAndPreviewAsync();
    }

    private async Task RefreshAndPreviewAsync()
    {
        await RefreshStyleListAsync();
        UpdatePreview();
    }

    /// <summary>渲染模式切换（本界面触发）</summary>
    private async Task OnRenderModeChangedAsync()
    {
        if (RenderModeComboBox.SelectedItem is not string modeText) return;
        Cfg.SubtitleRenderMode.Value = modeText;
        // 断开自身监听，避免信号回传导致重复执行
        SignalBus.SubtitleRenderModeChanged -= OnRenderModeChangedExternal;
        SignalBus.EmitSubtitleRenderModeChanged(modeText);
        SignalBus.SubtitleRenderModeChanged += OnRenderModeChangedExternal;
        UpdateVisibleGroups();
        await RefreshAndPreviewAsync();
    }

    /// <summary>圆角背景设置变更</summary>
    private void OnRoundedBgSettingChanged()
    {
        if (!_signalsConnected || _loadingStyle) return;

        Cfg.RoundedBgFontName.Value = RoundedFontComboBox.SelectedItem as string ?? "";
        Cfg.RoundedBgFontSize.Value = SpinInt(RoundedFontSizeSpinBox);
        Cfg.RoundedBgCornerRadius.Value = SpinInt(RoundedCornerRadiusSpinBox);
        Cfg.RoundedBgPaddingH.Value = SpinInt(RoundedPaddingHSpinBox);
        Cfg.RoundedBgPaddingV.Value = SpinInt(RoundedPaddingVSpinBox);
        Cfg.RoundedBgMarginBottom.Value = SpinInt(RoundedMarginBottomSpinBox);
        Cfg.RoundedBgLineSpacing.Value = SpinInt(RoundedLineSpacingSpinBox);
        Cfg.RoundedBgLetterSpacing.Value = SpinInt(RoundedLetterSpacingSpinBox);

        Cfg.RoundedBgTextColor.Value = StyleColorButton.ColorName(RoundedTextColorButton.Color);
        Cfg.RoundedBgColor.Value = StyleColorButton.RgbaHex(RoundedBgColorButton.Color);

        // 自动保存当前样式
        if (StyleNameComboBox.SelectedItem is string current && current.Length > 0)
            _ = SaveStyleAsync(current);

        UpdatePreview();
    }

    /// <summary>ASS 样式设置变更</summary>
    private void OnAssSettingChanged()
    {
        if (!_signalsConnected || _loadingStyle) return;

        UpdatePreview();
        var current = StyleNameComboBox.SelectedItem as string;
        _ = SaveStyleAsync(string.IsNullOrEmpty(current) ? "default" : current);
    }

    private void OnOrientationChanged()
    {
        var landscape = OrientationComboBox.SelectedItem as string == "横屏";
        Cfg.SubtitlePreviewImage.Value = DefaultBgPath(landscape);
        UpdatePreview();
    }

    private async Task SelectPreviewImageAsync()
    {
        var file = await Dialogs.OpenFileAsync(I18n.Tr("选择背景图片", Ctx),
            (I18n.Tr("图片文件", Ctx), ["*.png", "*.jpg", "*.jpeg"]));
        if (!string.IsNullOrEmpty(file))
        {
            Cfg.SubtitlePreviewImage.Value = file;
            UpdatePreview();
        }
    }

    // ------------------------------------------------------------------ groups / modes

    private bool IsRoundedMode => RenderModeComboBox.SelectedItem as string == RoundedBgMode;
    private string ModeKey => IsRoundedMode ? "rounded" : "ass";

    /// <summary>根据渲染模式显示/隐藏设置组</summary>
    private void UpdateVisibleGroups()
    {
        var isAss = RenderModeComboBox.SelectedItem as string == AssStyleMode;
        AssVerticalSpacingCard.IsVisible = isAss;
        AssPrimaryGroup.IsVisible = isAss;
        AssSecondaryGroup.IsVisible = isAss;
        RoundedBgGroup.IsVisible = !isAss;
    }

    // ------------------------------------------------------------------ style presets

    /// <summary>根据当前渲染模式刷新样式列表</summary>
    private async Task RefreshStyleListAsync()
    {
        var styleFiles = new List<string>();
        try
        {
            if (await Bridge.Instance.RequestAsync("style_list", new { mode = ModeKey }) is JsonArray arr)
                styleFiles.AddRange(arr.Select(n => n?.GetValue<string>() ?? ""));
        }
        catch (BridgeException e)
        {
            Toast.Error(I18n.Tr("错误", Ctx), e.Message);
        }

        _styleComboSignalsBlocked = true;
        // 确保有默认样式
        if (!styleFiles.Contains("default"))
        {
            styleFiles.Insert(0, "default");
            await SaveStyleAsync("default");
        }
        else
        {
            styleFiles.Remove("default");
            styleFiles.Insert(0, "default");
        }
        StyleNameComboBox.ItemsSource = styleFiles;

        var name = Cfg.SubtitleStyleName.Value;
        if (!styleFiles.Contains(name)) name = styleFiles[0];
        StyleNameComboBox.SelectedItem = name;
        _styleComboSignalsBlocked = false;

        await LoadStyleAsync(name);
    }

    /// <summary>加载指定样式（根据当前渲染模式加载对应格式）</summary>
    private async Task LoadStyleAsync(string styleName)
    {
        var mode = ModeKey;
        JsonNode? data;
        try
        {
            data = await Bridge.Instance.RequestAsync("style_load", new { name = styleName, mode });
        }
        catch (BridgeException e)
        {
            Toast.Error(I18n.Tr("错误", Ctx), e.Message);
            return;
        }
        if (data is not JsonObject obj) return;

        _loadingStyle = true;
        try
        {
            if (mode == "rounded") LoadRoundedBgStyle(obj);
            else LoadAssStyle(obj);
            Cfg.SubtitleStyleName.Value = styleName;
        }
        finally
        {
            _loadingStyle = false;
        }
        UpdatePreview();

        if (IsEffectivelyVisible)
            Toast.Success(I18n.Tr("成功", Ctx), I18n.Tr("已加载样式 ", Ctx) + styleName,
                InfobarDurationSuccess, ToastPosition.Top);
    }

    /// <summary>加载 ASS 样式 (.json)</summary>
    private void LoadAssStyle(JsonObject s)
    {
        SetComboText(AssPrimaryFontComboBox, Str(s, "font_name"));
        AssPrimarySizeSpinBox.Value = Num(s, "font_size");
        AssVerticalSpacingSpinBox.Value = Num(s, "margin_bottom");
        AssPrimaryColorButton.Color = StyleColorButton.ParseQColor(Str(s, "primary_color"));
        AssPrimaryOutlineColorButton.Color = StyleColorButton.ParseQColor(Str(s, "outline_color"));
        AssPrimarySpacingSpinBox.Value = Num(s, "spacing");
        AssPrimaryOutlineSizeSpinBox.Value = Num(s, "outline_width");

        if (s["secondary"] is JsonObject sec)
        {
            SetComboText(AssSecondaryFontComboBox, Str(sec, "font_name"));
            AssSecondarySizeSpinBox.Value = Num(sec, "font_size");
            AssSecondaryColorButton.Color = StyleColorButton.ParseQColor(Str(sec, "color"));
            AssSecondaryOutlineColorButton.Color = StyleColorButton.ParseQColor(Str(sec, "outline_color"));
            AssSecondarySpacingSpinBox.Value = Num(sec, "spacing");
            AssSecondaryOutlineSizeSpinBox.Value = Num(sec, "outline_width");
        }
    }

    /// <summary>加载圆角背景样式 (.json): only keys present in the file are applied.</summary>
    private void LoadRoundedBgStyle(JsonObject d)
    {
        if (d.ContainsKey("font_name")) SetComboText(RoundedFontComboBox, Str(d, "font_name"));
        if (d.ContainsKey("font_size")) RoundedFontSizeSpinBox.Value = Num(d, "font_size");
        if (d.ContainsKey("text_color")) RoundedTextColorButton.Color = StyleColorButton.ParseQColor(Str(d, "text_color"));
        if (d.ContainsKey("bg_color")) RoundedBgColorButton.Color = StyleColorButton.ParseRgbaHex(Str(d, "bg_color"));
        if (d.ContainsKey("corner_radius")) RoundedCornerRadiusSpinBox.Value = Num(d, "corner_radius");
        if (d.ContainsKey("padding_h")) RoundedPaddingHSpinBox.Value = Num(d, "padding_h");
        if (d.ContainsKey("padding_v")) RoundedPaddingVSpinBox.Value = Num(d, "padding_v");
        if (d.ContainsKey("margin_bottom")) RoundedMarginBottomSpinBox.Value = Num(d, "margin_bottom");
        if (d.ContainsKey("line_spacing")) RoundedLineSpacingSpinBox.Value = Num(d, "line_spacing");
        if (d.ContainsKey("letter_spacing")) RoundedLetterSpacingSpinBox.Value = Num(d, "letter_spacing");
    }

    /// <summary>创建新样式</summary>
    private async Task CreateNewStyleAsync()
    {
        var styleName = await new StyleNameDialog().ExecAsync();
        if (string.IsNullOrEmpty(styleName)) return;

        // 检查是否已存在同名样式（使用带前缀的实际路径）
        try
        {
            if ((await Bridge.Instance.RequestAsync("style_exists", new { name = styleName, mode = ModeKey }))?.GetValue<bool>() == true)
            {
                Toast.Warning(I18n.Tr("警告", Ctx), I18n.Tr("样式 ", Ctx) + styleName + I18n.Tr(" 已存在", Ctx),
                    InfobarDurationWarning, ToastPosition.Top);
                return;
            }
        }
        catch (BridgeException e)
        {
            Toast.Error(I18n.Tr("错误", Ctx), e.Message);
            return;
        }

        // 保存新样式
        await SaveStyleAsync(styleName);

        // 更新样式列表并选中新样式
        var items = (StyleNameComboBox.ItemsSource as IEnumerable<string> ?? []).ToList();
        items.Add(styleName);
        _styleComboSignalsBlocked = true;
        var previous = StyleNameComboBox.SelectedItem;
        StyleNameComboBox.ItemsSource = items;
        StyleNameComboBox.SelectedItem = previous;
        _styleComboSignalsBlocked = false;
        StyleNameComboBox.SelectedItem = styleName;

        Toast.Success(I18n.Tr("成功", Ctx), I18n.Tr("已创建新样式 ", Ctx) + styleName,
            InfobarDurationSuccess, ToastPosition.Top);
    }

    /// <summary>保存样式（根据当前渲染模式保存对应格式）</summary>
    private async Task SaveStyleAsync(string styleName)
    {
        try
        {
            await Bridge.Instance.RequestAsync("style_save", new { name = styleName, mode = ModeKey, style = CurrentStyle() });
        }
        catch (BridgeException e)
        {
            Toast.Error(I18n.Tr("错误", Ctx), e.Message);
        }
    }

    /// <summary>Current card values in the shape style_save / style_preview expect.</summary>
    private JsonObject CurrentStyle()
    {
        if (IsRoundedMode)
        {
            return new JsonObject
            {
                ["font_name"] = RoundedFontComboBox.SelectedItem as string ?? "",
                ["font_size"] = SpinInt(RoundedFontSizeSpinBox),
                ["text_color"] = StyleColorButton.ColorName(RoundedTextColorButton.Color),
                ["bg_color"] = StyleColorButton.RgbaHex(RoundedBgColorButton.Color),
                ["corner_radius"] = SpinInt(RoundedCornerRadiusSpinBox),
                ["padding_h"] = SpinInt(RoundedPaddingHSpinBox),
                ["padding_v"] = SpinInt(RoundedPaddingVSpinBox),
                ["margin_bottom"] = SpinInt(RoundedMarginBottomSpinBox),
                ["line_spacing"] = SpinInt(RoundedLineSpacingSpinBox),
                ["letter_spacing"] = SpinInt(RoundedLetterSpacingSpinBox),
            };
        }
        return new JsonObject
        {
            ["font_name"] = AssPrimaryFontComboBox.SelectedItem as string ?? "",
            ["font_size"] = SpinInt(AssPrimarySizeSpinBox),
            ["primary_color"] = StyleColorButton.ColorName(AssPrimaryColorButton.Color),
            ["outline_color"] = StyleColorButton.ColorName(AssPrimaryOutlineColorButton.Color),
            ["outline_width"] = SpinDouble(AssPrimaryOutlineSizeSpinBox),
            ["spacing"] = SpinDouble(AssPrimarySpacingSpinBox),
            ["margin_bottom"] = SpinInt(AssVerticalSpacingSpinBox),
            ["secondary"] = new JsonObject
            {
                ["font_name"] = AssSecondaryFontComboBox.SelectedItem as string ?? "",
                ["font_size"] = SpinInt(AssSecondarySizeSpinBox),
                ["color"] = StyleColorButton.ColorName(AssSecondaryColorButton.Color),
                ["outline_color"] = StyleColorButton.ColorName(AssSecondaryOutlineColorButton.Color),
                ["outline_width"] = SpinDouble(AssSecondaryOutlineSizeSpinBox),
                ["spacing"] = SpinDouble(AssSecondarySpacingSpinBox),
            },
        };
    }

    // ------------------------------------------------------------------ preview

    /// <summary>ASSETS_PATH/default_bg_{landscape,portrait}.png (DEFAULT_BG_LANDSCAPE / DEFAULT_BG_PORTRAIT).</summary>
    private string DefaultBgPath(bool landscape) =>
        Path.Combine(_assetsPath, landscape ? "default_bg_landscape.png" : "default_bg_portrait.png");

    /// <summary>
    /// The default background actually used for rendering. A pip-installed backend has no
    /// default_bg_*.png in ASSETS_PATH; then the copy bundled with this app is used.
    /// </summary>
    private static string BundledBg(bool landscape)
    {
        var name = landscape ? "default_bg_landscape.png" : "default_bg_portrait.png";
        var target = Path.Combine(Path.GetTempPath(), "VideoCaptioner", name);
        if (!File.Exists(target))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var src = AssetLoader.Open(new Uri($"avares://VideoCaptioner/Assets/{name}"));
            using var dst = File.Create(target);
            src.CopyTo(dst);
        }
        return target;
    }

    /// <summary>更新预览图片</summary>
    private void UpdatePreview()
    {
        if (_previewBusy)
        {
            _previewPending = true;
            return;
        }
        _ = RenderPreviewAsync();
    }

    private async Task RenderPreviewAsync()
    {
        _previewBusy = true;
        try
        {
            do
            {
                _previewPending = false;
                await RenderOnceAsync();
            } while (_previewPending);
        }
        finally
        {
            _previewBusy = false;
        }
    }

    private async Task RenderOnceAsync()
    {
        // 获取预览文本
        var key = PreviewTextComboBox.SelectedItem as string;
        var entry = PreviewTexts.FirstOrDefault(p => p.Key == key);
        if (entry.Key == null) entry = PreviewTexts[0];
        string mainText = entry.Main;
        string? subText = entry.Sub;

        // 字幕布局
        switch (LayoutComboBox.SelectedItem as string)
        {
            case "译文在上": (mainText, subText) = (subText, mainText); break;
            case "原文在上": break;
            case "仅译文": (mainText, subText) = (subText, null); break;
            case "仅原文": subText = null; break;
        }

        // 获取预览方向和背景
        var landscape = OrientationComboBox.SelectedItem as string != "竖屏";
        var userBg = Cfg.SubtitlePreviewImage.Value;
        string bg;
        if (!string.IsNullOrEmpty(userBg) && (File.Exists(userBg) || Directory.Exists(userBg)))
            bg = userBg;
        else
        {
            bg = DefaultBgPath(landscape);
            if (!File.Exists(bg)) bg = BundledBg(landscape);
        }

        try
        {
            var res = await Bridge.Instance.RequestAsync("style_preview", new
            {
                mode = ModeKey,
                main_text = mainText,
                sub_text = subText,
                bg_image_path = bg,
                style = CurrentStyle(),
            });
            var path = res?.GetValue<string>();
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
            OnPreviewReady(path);
        }
        catch (Exception e) when (e is BridgeException or IOException)
        {
            Toast.Error(I18n.Tr("错误", Ctx), e.Message);
        }
    }

    /// <summary>预览图片生成完成的回调</summary>
    private void OnPreviewReady(string previewPath)
    {
        // Read into memory: the ASS preview is always written to the same cache file.
        var bytes = File.ReadAllBytes(previewPath);
        var old = PreviewImage.Source as Bitmap;
        using (var ms = new MemoryStream(bytes)) PreviewImage.Source = new Bitmap(ms);
        old?.Dispose();
        // render_preview (rounded) writes a new temp file each time.
        if (previewPath.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase))
        {
            try { File.Delete(previewPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        UpdatePreviewImage();
    }

    /// <summary>
    /// scaledToWidth(stage width), then scaledToHeight if too tall; 8px rounded corners.
    /// The stage takes the card width and at most the height left in the (non-scrolling) preview column,
    /// so the preview card stays fully visible next to the scrolling settings.
    /// </summary>
    private void UpdatePreviewImage()
    {
        var bmp = PreviewImage.Source as Bitmap;
        var aspect = bmp is { Size.Width: > 0 } ? bmp.Size.Height / bmp.Size.Width : 9.0 / 16.0;
        var width = Math.Floor(PreviewTopWidget.Bounds.Width);
        if (width <= 0) return;

        // everything in the card except the stage: padding, border, header, controls, spacing
        var chrome = PreviewCard.Bounds.Height - PreviewTopWidget.Bounds.Height;
        var maxHeight = Math.Max(PreviewTopWidget.MinHeight, Math.Floor(PreviewColumn.Bounds.Height - chrome));
        var stageHeight = Math.Min(Math.Floor(width * aspect), maxHeight);
        if (double.IsNaN(PreviewTopWidget.Height) || Math.Abs(PreviewTopWidget.Height - stageHeight) >= 1)
            PreviewTopWidget.Height = stageHeight;

        if (bmp == null) return;
        double w = width, h = width * aspect;
        if (h > stageHeight)
        {
            h = stageHeight;
            w = stageHeight / aspect;
        }
        PreviewImage.Width = Math.Floor(w);
        PreviewImage.Height = Math.Floor(h);
        PreviewLoadingRing.IsVisible = false;
    }

    // ------------------------------------------------------------------ drag & drop

    private static bool IsImage(string p) =>
        p.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
        p.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
        p.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase);

    private static List<string> DroppedFiles(DragEventArgs e) =>
        e.Data.GetFiles()?.Select(f => f.TryGetLocalPath()).Where(p => p != null).Cast<string>().ToList() ?? [];

    /// <summary>拖入事件：检查是否为图片文件</summary>
    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var accept = DroppedFiles(e).Any(IsImage);
        e.DragEffects = accept ? DragDropEffects.Copy : DragDropEffects.None;
        PreviewDropOverlay.IsVisible = accept;
        e.Handled = true;
    }

    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        // also raised when the pointer moves between child elements; the next DragOver shows it again
        PreviewDropOverlay.IsVisible = false;
    }

    /// <summary>放下事件：将图片设置为预览背景</summary>
    private void OnDrop(object? sender, DragEventArgs e)
    {
        PreviewDropOverlay.IsVisible = false;
        foreach (var file in DroppedFiles(e))
        {
            if (!IsImage(file)) continue;
            Cfg.SubtitlePreviewImage.Value = file;
            UpdatePreview();
            Toast.Success(I18n.Tr("成功", Ctx), I18n.Tr("已设置预览背景：", Ctx) + Path.GetFileName(file),
                InfobarDurationSuccess, ToastPosition.Top);
            break; // 只处理第一个图片文件
        }
        e.Handled = true;
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>qfluentwidgets ComboBox.setCurrentText: no-op if the text is not an item.</summary>
    private static void SetComboText(ComboBox cb, string? text)
    {
        if (text == null || cb.ItemsSource is not IEnumerable<string> items) return;
        if (items.Contains(text)) cb.SelectedItem = text;
    }

    private static int SpinInt(NumberBox nb) => double.IsNaN(nb.Value) ? (int)nb.Minimum : (int)Math.Round(nb.Value);

    private static double SpinDouble(NumberBox nb) =>
        Math.Round(double.IsNaN(nb.Value) ? nb.Minimum : nb.Value, 1);

    private static string Str(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : o[key]?.ToString() ?? "";

    private static double Num(JsonObject o, string key)
    {
        var n = o[key];
        if (n is JsonValue v)
        {
            if (v.TryGetValue<double>(out var d)) return d;
            if (v.TryGetValue<string>(out var s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
        }
        return 0;
    }
}
