using System.Globalization;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using FluentAvalonia.UI.Controls;
using VideoCaptioner.Avalonia.Controls;
using VideoCaptioner.Avalonia.Services;

namespace VideoCaptioner.Avalonia.Views.Components;

/// <summary>Port of videocaptioner/ui/components/FasterWhisperSettingWidget.py (FasterWhisperSettingWidget).</summary>
public class FasterWhisperSettingWidget : UserControl
{
    private const string Ctx = "FasterWhisperSettingWidget";
    private static string Tr(string s) => I18n.Tr(s, Ctx);

    public ComboBoxSettingCard ModelCard { get; }
    public HyperlinkCard ManageModelCard { get; }
    public ComboBoxSettingCard LanguageCard { get; }
    public ComboBoxSettingCard DeviceCard { get; }
    public ComboBoxSettingCard ComputeTypeCard { get; }
    public SwitchSettingCard VadFilterCard { get; }
    public DoubleSpinBoxSettingCard VadThresholdCard { get; }
    public ComboBoxSettingCard VadMethodCard { get; }
    public SwitchSettingCard FfMdxKim2Card { get; }
    public SwitchSettingCard OneWordCard { get; }
    public LineEditSettingCard PromptCard { get; }

    private bool _shown;

    public FasterWhisperSettingWidget()
    {
        var settingGroup = new SettingCardGroup { Title = Tr("Faster Whisper 设置") };

        ModelCard = new ComboBoxSettingCard(Cfg.FasterWhisperModel, Symbol.Code, Tr("模型"),
            Tr("选择 Faster Whisper 模型"), options: []);
        ManageModelCard = new HyperlinkCard("", Tr("管理模型"), Symbol.Download, Tr("模型管理"),
            Tr("下载或更新 Faster Whisper 模型"));
        LanguageCard = new ComboBoxSettingCard(Cfg.TranscribeLanguage, Symbol.Character, Tr("源语言"),
            Tr("音视频中说话的语言，默认根据前30秒自动识别"));
        LanguageCard.ComboBox.MaxDropDownHeight = 6 * 33; // setMaxVisibleItems(6)
        DeviceCard = new ComboBoxSettingCard(Cfg.FasterWhisperDevice, Symbol.Setting, Tr("运行设备"), Tr("模型运行设备"),
            options: ["cuda", "cpu"]);
        ComputeTypeCard = new ComboBoxSettingCard(Cfg.FasterWhisperComputeType, Symbol.Calculator, Tr("计算精度"),
            Tr("int8 显存占用约为 float16 的一半；自动 = RTX 50 系用 float16，其余用程序默认"),
            texts: [Tr("自动"), "int8", "int8_float16", "float16", "float32"],
            options: ["auto", "int8", "int8_float16", "float16", "float32"]);

        // VAD设置组
        var vadGroup = new SettingCardGroup { Title = Tr("VAD设置") };
        VadFilterCard = new SwitchSettingCard(Symbol.Checkmark, Tr("VAD过滤"), Tr("过滤无人声语音片断，减少幻觉"),
            Cfg.FasterWhisperVadFilter);
        VadThresholdCard = new DoubleSpinBoxSettingCard(Cfg.FasterWhisperVadThreshold, Symbol.Volume, Tr("VAD阈值"),
            Tr("语音概率阈值，高于此值视为语音"), minimum: 0.00, maximum: 1.00, decimals: 2, step: 0.05);
        VadMethodCard = new ComboBoxSettingCard(Cfg.FasterWhisperVadMethod, Symbol.Audio, Tr("VAD方法"),
            Tr("选择VAD检测方法"));

        // 其他设置组
        var otherGroup = new SettingCardGroup { Title = Tr("其他设置") };
        FfMdxKim2Card = new SwitchSettingCard(Symbol.Audio, Tr("人声分离"), Tr("处理前使用MDX-Net降噪，分离人声和背景音乐"),
            Cfg.FasterWhisperFfMdxKim2);
        OneWordCard = new SwitchSettingCard(Symbol.Ruler, Tr("单字时间戳"), Tr("开启生成单字级时间戳；关闭后使用原始分段断句"),
            Cfg.FasterWhisperOneWord);
        PromptCard = new LineEditSettingCard(Cfg.FasterWhisperPrompt, Symbol.Message, Tr("提示词"), Tr("可选的提示词,默认空"), "");

        // Win11 layout: "管理模型" is a row of the model expander, the VAD options are rows of the VAD switch
        ModelCard.Items.Add(ManageModelCard);
        ModelCard.IsExpanded = true;
        settingGroup.Items.Add(ModelCard);
        DeviceCard.Items.Add(ComputeTypeCard);
        DeviceCard.IsExpanded = true;
        settingGroup.Items.Add(DeviceCard);
        settingGroup.Items.Add(LanguageCard);

        VadFilterCard.Items.Add(VadThresholdCard);
        VadFilterCard.Items.Add(VadMethodCard);
        VadFilterCard.IsExpanded = true;
        vadGroup.Items.Add(VadFilterCard);

        otherGroup.Items.Add(FfMdxKim2Card);
        otherGroup.Items.Add(OneWordCard);
        otherGroup.Items.Add(PromptCard);

        // 设置组件最小宽度 (WinUI SettingsCard standard widths)
        PromptCard.EditorWidth = SettingCard.TextWidth;

        Content = new ScrollViewer
        {
            HorizontalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = new StackPanel
            {
                Spacing = 16,
                Children = { settingGroup, vadGroup, otherGroup },
            },
        };

        ManageModelCard.LinkClicked += ShowModelManager;
        VadFilterCard.CheckedChanged += OnVadFilterChanged;
        // QWidget.showEvent: the effective viewport becomes non-empty when the widget is actually shown.
        EffectiveViewportChanged += (_, e) =>
        {
            var shown = IsEffectivelyVisible && e.EffectiveViewport.Width > 0 && e.EffectiveViewport.Height > 0;
            if (shown && !_shown) _ = OnShownAsync();
            _shown = shown;
        };
        DetachedFromVisualTree += (_, _) => _shown = false;

        _ = RefreshModelsAsync(initial: true);
    }

    private async Task OnShownAsync()
    {
        // 检查 Faster Whisper 程序是否存在
        try
        {
            var status = await Bridge.Instance.RequestAsync("faster_whisper_status");
            if (status?["has_program"]?.GetValue<bool>() == false)
            {
                ShowErrorInfo(Tr("Faster Whisper程序不存在，请先下载程序"));
                ShowModelManager();
            }
        }
        catch
        {
            // backend unavailable
        }
    }

    /// <summary>
    /// Only downloaded models are selectable. The initial filter checks model.bin (setup_ui);
    /// the refresh after a download checks the model folder (FasterWhisperDownloadDialog).
    /// </summary>
    public async Task RefreshModelsAsync(bool initial = false)
    {
        try
        {
            var res = await Bridge.Instance.RequestAsync("faster_whisper_status");
            var available = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in res?["models"]?.AsArray() ?? [])
                if (m?[initial ? "downloaded" : "dir_exists"]?.GetValue<bool>() == true)
                    available.Add(m["label"]!.GetValue<string>());
            ModelCard.SetItems(Models.Options.FasterWhisperModel.Where(available.Contains).ToList());
        }
        catch
        {
            // backend unavailable
        }
    }

    private void OnVadFilterChanged(bool isChecked)
    {
        VadThresholdCard.IsEnabled = isChecked;
        VadMethodCard.IsEnabled = isChecked;
    }

    private void ShowModelManager()
    {
        _ = new FasterWhisperDownloadDialog(this).ShowAsync();
    }

    public void ShowErrorInfo(string errorMsg)
    {
        Toast.Error(Tr("错误"), errorMsg, 5000, ToastPosition.Bottom);
    }
}

/// <summary>Port of FasterWhisperDownloadDialog (program download/installation + modelscope model download).</summary>
public class FasterWhisperDownloadDialog : ContentDialog
{
    protected override Type StyleKeyOverride => typeof(ContentDialog);

    private const string Ctx = "FasterWhisperDownloadDialog";
    private static string Tr(string s) => I18n.Tr(s, Ctx);

    /// <summary>类变量跟踪下载状态</summary>
    public static bool IsDownloading { get; private set; }

    private readonly FasterWhisperSettingWidget? _settingWidget;
    private readonly StackPanel _layout = new() { MinWidth = 620, Spacing = 8 };
    private readonly ProgressBar _progressBar = new() { IsVisible = false, Minimum = 0, Maximum = 100 };
    private readonly TextBlock _progressLabel = new() { IsVisible = false, Classes = { "caption" }, TextWrapping = TextWrapping.Wrap };
    private ComboBox? _programCombo;
    private Button? _programDownloadBtn;
    private ModelDownloadTable? _modelTable;
    private JsonArray _models = [];
    private JsonArray _programs = [];
    private string _modelPath = "";
    private string _binPath = "";
    private BridgeJob? _programJob;
    private BridgeJob? _modelJob;

    public FasterWhisperDownloadDialog(FasterWhisperSettingWidget? settingWidget = null)
    {
        _settingWidget = settingWidget;
        Resources["ContentDialogMaxWidth"] = 760.0;
        Title = Tr("模型管理");
        CloseButtonText = Tr("关闭");
        Content = _layout;
        Opened += async (_, _) => await SetupUiAsync();
        Closed += (_, e) =>
        {
            if (e.Result != ContentDialogResult.Primary) OnDialogReject();
        };
    }

    private async Task SetupUiAsync()
    {
        JsonNode? info;
        try
        {
            info = await Bridge.Instance.RequestAsync("faster_whisper_status");
        }
        catch (Exception e)
        {
            Toast.Error(Tr("下载错误"), e.Message, 3000);
            return;
        }
        _models = info?["models"]?.AsArray() ?? [];
        _programs = info?["programs"]?.AsArray() ?? [];
        _modelPath = info?["model_path"]?.GetValue<string>() ?? "";
        _binPath = info?["bin_path"]?.GetValue<string>() ?? "";
        var installedVersions = info?["installed_versions"]?.AsArray().Select(v => v?.GetValue<string>() ?? "").ToList() ?? [];

        SetupProgramSection(info?["has_program"]?.GetValue<bool>() ?? false, installedVersions);
        SetupModelSection();
        _layout.Children.Add(WhisperCppDownloadDialog.ProgressCard(_progressBar, _progressLabel));
    }

    private void SetupProgramSection(bool hasProgram, List<string> installedVersions)
    {
        var openFolderBtn = WhisperCppDownloadDialog.FolderLinkButton(Tr("打开程序文件夹"));
        openFolderBtn.Click += (_, _) => { if (Directory.Exists(_binPath)) Dialogs.OpenUrl(_binPath); };
        _layout.Children.Add(WhisperCppDownloadDialog.SectionTitle(Tr("Faster Whisper 下载"), openFolderBtn));

        if (hasProgram)
        {
            var versionsText = string.Join(" + ", installedVersions);
            _layout.Children.Add(WhisperCppDownloadDialog.StatusBar(Tr($"已安装版本: {versionsText}"), InfoBarSeverity.Success));
            if (installedVersions.Count == 1)
                _layout.Children.Add(new TextBlock { Text = Tr("您可以继续下载其他版本:"), Classes = { "caption" }, Margin = new Thickness(2, 4, 0, 0) });
        }
        else
        {
            _layout.Children.Add(WhisperCppDownloadDialog.StatusBar(Tr("未下载Faster Whisper 程序"), InfoBarSeverity.Warning));
        }

        // 只显示未安装的版本
        var items = _programs
            .Where(p => !installedVersions.Contains(p!["type"]!.GetValue<string>()))
            .Select(p => $"{p!["label"]!.GetValue<string>()} ({p["size"]!.GetValue<string>()})")
            .ToList();
        if (items.Count > 0)
        {
            _programCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, ItemsSource = items, SelectedIndex = 0 };
            _programDownloadBtn = new Button
            {
                Classes = { "accent" },
                MinWidth = 120,
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children =
                    {
                        new SymbolIcon { Symbol = Symbol.Download, FontSize = 16 },
                        new TextBlock { Text = Tr("下载程序"), VerticalAlignment = VerticalAlignment.Center },
                    },
                },
            };
            _programDownloadBtn.Click += (_, _) => StartDownload();
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
            row.Children.Add(_programCombo);
            Grid.SetColumn(_programDownloadBtn, 1);
            row.Children.Add(_programDownloadBtn);
            _layout.Children.Add(new Border { Classes = { "card" }, Padding = new Thickness(16, 12), Child = row });
        }
    }

    private void SetupModelSection()
    {
        var openFolderBtn = WhisperCppDownloadDialog.FolderLinkButton(Tr("打开模型文件夹"));
        openFolderBtn.Click += (_, _) => { if (Directory.Exists(_modelPath)) Dialogs.OpenUrl(_modelPath); };
        _layout.Children.Add(WhisperCppDownloadDialog.SectionTitle(Tr("模型下载"), openFolderBtn, top: 16));

        _modelTable = new ModelDownloadTable(Tr("模型名称"), Tr("大小"), Tr("状态"), Tr("操作"));
        foreach (var m in _models)
        {
            var downloaded = m?["downloaded"]?.GetValue<bool>() ?? false;
            var sizeKb = double.Parse(m!["size"]!.GetValue<string>(), CultureInfo.InvariantCulture);
            _modelTable.AddRow(m["label"]!.GetValue<string>(), (sizeKb / 1024).ToString("F1", CultureInfo.InvariantCulture) + " MB",
                downloaded ? Tr("已下载") : Tr("未下载"), downloaded, downloaded ? Tr("重新下载") : Tr("下载"));
        }
        _modelTable.DownloadClicked += DownloadModel;
        _layout.Children.Add(_modelTable);
    }

    private void SetAllDownloadButtonsEnabled(bool enabled)
    {
        if (_programDownloadBtn != null) _programDownloadBtn.IsEnabled = enabled;
        if (_programCombo != null) _programCombo.IsEnabled = enabled;
        _modelTable?.SetAllButtonsEnabled(enabled);
    }

    private void StartDownload()
    {
        if (IsDownloading)
        {
            Toast.Warning(Tr("下载进行中"), Tr("请等待当前下载任务完成"), 3000);
            return;
        }

        IsDownloading = true;
        SetAllDownloadButtonsEnabled(false);

        var selectedText = _programCombo?.SelectedItem as string ?? "";
        var selectedLabel = selectedText.Split(" (")[0];
        var program = _programs.FirstOrDefault(p => p!["label"]!.GetValue<string>() == selectedLabel);
        if (program == null)
        {
            Toast.Error(Tr("下载错误"), Tr("未找到对应的程序配置"), 3000);
            IsDownloading = false;
            SetAllDownloadButtonsEnabled(true);
            return;
        }

        _progressBar.IsVisible = true;
        _progressLabel.IsVisible = true;
        _programDownloadBtn!.IsEnabled = false;
        _programCombo!.IsEnabled = false;

        string? errorTitle = null;
        var job = _programJob = Bridge.Instance.Run("faster_whisper_download_program", new { label = selectedLabel });
        job.Progress += (v, msg) =>
        {
            _progressBar.Value = v;
            _progressLabel.Text = msg;
        };
        job.Event += (name, _) =>
        {
            if (name == "unzipping") _progressLabel.Text = Tr("正在解压文件...");
            else if (name == "download_error") errorTitle = Tr("下载失败");
            else if (name == "install_error") errorTitle = Tr("安装失败");
        };
        job.Finished += _ => FinishProgramInstallation();
        job.Error += error =>
        {
            if (errorTitle == Tr("安装失败"))
            {
                Toast.Error(Tr("安装失败"), error, 3000);
                CleanupInstallation();
                return;
            }
            // _on_program_download_error
            Toast.Error(Tr("下载失败"), error, 3000);
            IsDownloading = false;
            SetAllDownloadButtonsEnabled(true);
            _progressBar.IsVisible = false;
            _progressLabel.IsVisible = false;
        };
    }

    private void FinishProgramInstallation()
    {
        Toast.Success(Tr("安装完成"), Tr("Faster Whisper 程序已安装成功"), 3000);
        Hide(ContentDialogResult.Primary); // self.accept()
        CleanupInstallation();
    }

    private void CleanupInstallation()
    {
        IsDownloading = false;
        SetAllDownloadButtonsEnabled(true);
        _progressBar.IsVisible = false;
        _progressLabel.IsVisible = false;
    }

    /// <summary>_on_dialog_reject: stop the program download, terminate the model download.</summary>
    private void OnDialogReject()
    {
        if (_programJob is { IsDone: false }) _programJob.Cancel();
        if (_modelJob is { IsDone: false }) _modelJob.Cancel();
        IsDownloading = false;
    }

    private void DownloadModel(int row)
    {
        if (_modelTable == null) return;
        if (IsDownloading)
        {
            Toast.Warning(Tr("下载进行中"), Tr("请等待当前下载任务完成"), 3000);
            return;
        }

        IsDownloading = true;
        SetAllDownloadButtonsEnabled(false);

        var label = _models[row]!["label"]!.GetValue<string>();
        _progressBar.IsVisible = true;
        _progressLabel.IsVisible = true;
        _progressLabel.Text = Tr($"正在下载 {label} 模型...");
        _modelTable.SetButtonEnabled(row, false);

        var job = _modelJob = Bridge.Instance.Run("faster_whisper_download_model", new { row });
        job.Progress += (v, msg) =>
        {
            _progressBar.Value = v;
            _progressLabel.Text = msg;
        };
        job.Finished += async _ =>
        {
            IsDownloading = false;
            SetAllDownloadButtonsEnabled(true);
            _modelTable.SetStatus(row, Tr("已下载"), true);
            _modelTable.SetButtonText(row, Tr("重新下载"));
            _modelTable.SetButtonEnabled(row, true);

            // 更新主设置对话框的模型选择
            if (_settingWidget != null) await _settingWidget.RefreshModelsAsync();

            Toast.Success(Tr("下载成功"), Tr($"{label} 模型已下载完成"), 3000);
            _progressBar.IsVisible = false;
            _progressLabel.IsVisible = false;
        };
        job.Error += error =>
        {
            IsDownloading = false;
            SetAllDownloadButtonsEnabled(true);
            _modelTable.SetButtonEnabled(row, true);
            Toast.Error(Tr("下载失败"), error, 3000);
            _progressBar.IsVisible = false;
            _progressLabel.IsVisible = false;
        };
    }
}
