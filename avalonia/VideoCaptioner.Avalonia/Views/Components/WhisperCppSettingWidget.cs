using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using FluentAvalonia.UI.Controls;
using VideoCaptioner.Avalonia.Controls;
using VideoCaptioner.Avalonia.Services;

namespace VideoCaptioner.Avalonia.Views.Components;

/// <summary>Port of videocaptioner/ui/components/WhisperCppSettingWidget.py (WhisperCppSettingWidget).</summary>
public class WhisperCppSettingWidget : UserControl
{
    private const string Ctx = "WhisperCppSettingWidget";
    private static string Tr(string s) => I18n.Tr(s, Ctx);

    public ComboBoxSettingCard ModelCard { get; }
    public ComboBoxSettingCard LanguageCard { get; }
    public HyperlinkCard ManageModelCard { get; }
    // Avalonia addition: Vulkan whisper-cli (bridge/handlers/whisper_cpp_gpu.py)
    public ComboBoxSettingCard DeviceCard { get; }
    public SwitchSettingCard VadFilterCard { get; }
    public DoubleSpinBoxSettingCard VadThresholdCard { get; }
    private readonly SettingCardGroup _vadGroup;

    public WhisperCppSettingWidget()
    {
        var settingGroup = new SettingCardGroup { Title = Tr("Whisper CPP 设置") };

        ModelCard = new ComboBoxSettingCard(Cfg.WhisperModel, Symbol.Code, Tr("模型"), Tr("选择Whisper模型"),
            options: []);
        LanguageCard = new ComboBoxSettingCard(Cfg.TranscribeLanguage, Symbol.Character, Tr("源语言"),
            Tr("音视频中说话的语言，默认根据前30秒自动识别"));
        LanguageCard.ComboBox.MaxDropDownHeight = 6 * 33; // setMaxVisibleItems(6)
        ManageModelCard = new HyperlinkCard("", Tr("管理模型"), Symbol.Download, Tr("模型管理"),
            Tr("下载或更新 Whisper CPP 模型"));
        DeviceCard = new ComboBoxSettingCard(Cfg.WhisperCppDevice, Symbol.Setting, Tr("运行设备"),
            Tr("Vulkan 显卡（A 卡 / N 卡均可）或 CPU"), options: []) { IsVisible = false };

        _vadGroup = new SettingCardGroup { Title = Tr("VAD设置"), IsVisible = false };
        VadFilterCard = new SwitchSettingCard(Symbol.Checkmark, Tr("VAD过滤"), Tr("过滤无人声语音片断，减少幻觉（silero）"),
            Cfg.WhisperCppVadFilter);
        VadThresholdCard = new DoubleSpinBoxSettingCard(Cfg.WhisperCppVadThreshold, Symbol.Volume, Tr("VAD阈值"),
            Tr("语音概率阈值，高于此值视为语音"), minimum: 0.00, maximum: 1.00, decimals: 2, step: 0.05);

        // Win11 layout: "管理模型" is a row of the model expander
        ModelCard.Items.Add(ManageModelCard);
        ModelCard.IsExpanded = true;
        settingGroup.Items.Add(ModelCard);
        settingGroup.Items.Add(DeviceCard);
        settingGroup.Items.Add(LanguageCard);
        VadFilterCard.Items.Add(VadThresholdCard);
        VadFilterCard.IsExpanded = true;
        _vadGroup.Items.Add(VadFilterCard);

        Content = new ScrollViewer
        {
            HorizontalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = new StackPanel { Children = { settingGroup, _vadGroup } },
        };

        ManageModelCard.LinkClicked += ShowDownloadDialog;
        _ = RefreshModelsAsync();
    }

    /// <summary>检查未下载的模型并从下拉框中移除 (only downloaded ggml models are selectable).</summary>
    public async Task RefreshModelsAsync()
    {
        if (await RefreshVulkanAsync()) return;
        try
        {
            var res = await Bridge.Instance.RequestAsync("whisper_cpp_models");
            var downloaded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in res?["models"]?.AsArray() ?? [])
                if (m?["downloaded"]?.GetValue<bool>() == true)
                    downloaded.Add(m["label"]!.GetValue<string>());
            ModelCard.SetItems(Models.Options.WhisperModel.Where(downloaded.Contains).ToList());
        }
        catch
        {
            // backend unavailable: leave the list empty like a fresh install
        }
    }

    /// <summary>
    /// With the Vulkan whisper-cli installed, the model list becomes every ggml file in the model folder
    /// and the device / VAD cards are shown. Returns false when it is not installed.
    /// </summary>
    private async Task<bool> RefreshVulkanAsync()
    {
        JsonNode? res;
        try
        {
            res = await Bridge.Instance.RequestAsync("whisper_cpp_gpu_status");
        }
        catch
        {
            return false;
        }
        if (string.IsNullOrEmpty(res?["program"]?.GetValue<string>())) return false;

        var models = res!["models"]?.AsArray().Select(m => m!.GetValue<string>()).ToList() ?? [];
        ModelCard.ConfigItem = Cfg.WhisperCppModel;
        ModelCard.SetItems(models, models.Select(m => m.Replace("ggml-", "").Replace(".bin", "")).ToList());
        if (models.Count > 0 && !models.Contains(Cfg.WhisperCppModel.Value))
            Cfg.WhisperCppModel.Value = models[0];

        var devices = res["devices"]?.AsArray().Select(d => d!.GetValue<string>()).ToList() ?? [];
        DeviceCard.SetItems(["auto", "cpu", .. devices], [Tr("自动"), "CPU", .. devices]);
        DeviceCard.IsVisible = true;

        VadFilterCard.IsEnabled = res["has_vad_model"]?.GetValue<bool>() ?? false;
        _vadGroup.IsVisible = true;
        return true;
    }

    private void ShowDownloadDialog()
    {
        _ = new WhisperCppDownloadDialog(this).ShowAsync();
    }
}

/// <summary>Port of WhisperCppDownloadDialog (MessageBoxBase with program status, model table and progress).</summary>
public class WhisperCppDownloadDialog : ContentDialog
{
    protected override Type StyleKeyOverride => typeof(ContentDialog);

    private const string Ctx = "WhisperCppDownloadDialog";
    private static string Tr(string s) => I18n.Tr(s, Ctx);

    /// <summary>类变量跟踪下载状态</summary>
    public static bool IsDownloading { get; private set; }

    private readonly WhisperCppSettingWidget? _settingWidget;
    private readonly StackPanel _layout = new() { MinWidth = 620, Spacing = 8 };
    private readonly ProgressBar _progressBar = new() { IsVisible = false, Minimum = 0, Maximum = 100 };
    private readonly TextBlock _progressLabel = new() { IsVisible = false, Classes = { "caption" }, TextWrapping = TextWrapping.Wrap };
    private ModelDownloadTable? _modelTable;
    private JsonArray _models = [];
    private string _modelPath = "";

    public WhisperCppDownloadDialog(WhisperCppSettingWidget? settingWidget = null)
    {
        _settingWidget = settingWidget;
        Resources["ContentDialogMaxWidth"] = 760.0;
        Title = Tr("模型管理");
        CloseButtonText = Tr("关闭");
        Content = _layout;
        Opened += async (_, _) => await SetupUiAsync();
    }

    private async Task SetupUiAsync()
    {
        JsonNode? info;
        try
        {
            info = await Bridge.Instance.RequestAsync("whisper_cpp_models");
        }
        catch (Exception e)
        {
            Toast.Error(Tr("下载失败"), e.Message, 3000);
            return;
        }
        _models = info?["models"]?.AsArray() ?? [];
        _modelPath = info?["model_path"]?.GetValue<string>() ?? "";

        // ---- 程序
        _layout.Children.Add(SectionTitle(Tr("WhisperCpp程序")));
        var hasProgram = info?["has_program"]?.GetValue<bool>() ?? false;
        if (hasProgram)
        {
            var versions = string.Join(" + ", info?["installed_versions"]?.AsArray().Select(v => v?.GetValue<string>()) ?? []);
            _layout.Children.Add(StatusBar(Tr($"已安装版本: {versions}"), InfoBarSeverity.Success));
        }
        else
        {
            _layout.Children.Add(StatusBar(Tr("未下载 WhisperCpp 程序"), InfoBarSeverity.Warning));
        }

        // ---- 模型
        var openFolderBtn = FolderLinkButton(Tr("打开模型文件夹"));
        openFolderBtn.Click += (_, _) => { if (Directory.Exists(_modelPath)) Dialogs.OpenUrl(_modelPath); };
        _layout.Children.Add(SectionTitle(Tr("模型下载"), openFolderBtn, top: 16));

        _modelTable = new ModelDownloadTable(Tr("模型名称"), Tr("大小"), Tr("状态"), Tr("操作"));
        foreach (var m in _models)
        {
            var downloaded = m?["downloaded"]?.GetValue<bool>() ?? false;
            _modelTable.AddRow(m!["label"]!.GetValue<string>(), m["size"]!.GetValue<string>(),
                downloaded ? Tr("已下载") : Tr("未下载"), downloaded, downloaded ? Tr("重新下载") : Tr("下载"));
        }
        _modelTable.DownloadClicked += DownloadModel;
        _layout.Children.Add(_modelTable);

        // ---- 进度
        _layout.Children.Add(ProgressCard(_progressBar, _progressLabel));
    }

    /// <summary>Section title of a download dialog (16 SemiBold) with an optional right-aligned action.</summary>
    internal static Control SectionTitle(string text, Control? action = null, double top = 0)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, top, 0, 0), MinHeight = 32 };
        row.Children.Add(new TextBlock { Text = text, Classes = { "cardTitle" }, VerticalAlignment = VerticalAlignment.Center });
        if (action != null)
        {
            Grid.SetColumn(action, 1);
            row.Children.Add(action);
        }
        return row;
    }

    /// <summary>Program status as an inline Windows 11 InfoBar.</summary>
    internal static InfoBar StatusBar(string title, InfoBarSeverity severity) => new()
    {
        Title = title,
        Severity = severity,
        IsOpen = true,
        IsClosable = false,
    };

    /// <summary>Progress card: status caption above a thin ProgressBar; visible while the bar is.</summary>
    internal static Border ProgressCard(ProgressBar bar, TextBlock label)
    {
        label.IsVisible = true;
        var card = new Border
        {
            Classes = { "card" },
            Margin = new Thickness(0, 8, 0, 0),
            Child = new StackPanel { Spacing = 10, Children = { label, bar } },
        };
        card[!IsVisibleProperty] = bar[!IsVisibleProperty];
        return card;
    }

    internal static HyperlinkButton FolderLinkButton(string text) => new()
    {
        VerticalAlignment = VerticalAlignment.Center,
        Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { new SymbolIcon { Symbol = Symbol.OpenFolder, FontSize = 16 }, new TextBlock { Text = text } },
        },
    };

    private void DownloadModel(int row)
    {
        if (_modelTable == null) return;
        if (IsDownloading)
        {
            Toast.Warning(Tr("下载进行中"), Tr("请等待当前下载任务完成"), 3000);
            return;
        }

        IsDownloading = true;
        _modelTable.SetAllButtonsEnabled(false);

        var model = _models[row]!;
        var label = model["label"]!.GetValue<string>();
        _progressBar.IsVisible = true;
        _progressLabel.IsVisible = true;
        _progressLabel.Text = Tr($"正在下载 {label} 模型...");
        _modelTable.SetButtonEnabled(row, false);

        var job = Bridge.Instance.Run("whisper_cpp_download_model", new { row });
        job.Progress += (v, msg) =>
        {
            _progressBar.Value = v;
            _progressLabel.Text = msg;
        };
        job.Finished += async _ =>
        {
            IsDownloading = false;
            _modelTable.SetAllButtonsEnabled(true);
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
            _modelTable.SetAllButtonsEnabled(true);
            _modelTable.SetButtonEnabled(row, true);
            Toast.Error(Tr("下载失败"), error, 3000);
            _progressBar.IsVisible = false;
            _progressLabel.IsVisible = false;
        };
    }
}
