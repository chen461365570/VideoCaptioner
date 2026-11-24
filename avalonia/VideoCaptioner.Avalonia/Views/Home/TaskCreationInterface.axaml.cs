using Avalonia.Controls;
using Avalonia.Input;
using FluentAvalonia.UI.Controls;
using VideoCaptioner.Avalonia.Services;
using VideoCaptioner.Avalonia.Views.Components;

namespace VideoCaptioner.Avalonia.Views.Home;

/// <summary>Port of videocaptioner/ui/view/task_creation_interface.py (任务创建界面).</summary>
public partial class TaskCreationInterface : UserControl
{
    private const string Ctx = "TaskCreationInterface";
    // core/constant.py
    private const int DurationError = 10000, DurationWarning = 5000, DurationInfo = 3000, DurationSuccess = 2000;

    private LogWindow? _logWindow;

    public TaskCreationInterface()
    {
        InitializeComponent();
        SetupSignals();
        SetupValues();
        _ = LoadVersionAsync();
    }

    /// <summary>Emitted with the local media file path to process (python: finished = pyqtSignal(str)).</summary>
    public event Action<string>? Finished;

    private void SetupSignals()
    {
        StartButton.Click += (_, _) => OnStartClicked();
        SearchInput.TextChanged += (_, _) => OnSearchInputChanged();
        LogButton.Click += (_, _) => ShowLogWindow();
        DonateButton.Click += async (_, _) => await ShowDonateDialogAsync();
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        // drop zone highlight while a file is dragged over the page
        AddHandler(DragDrop.DragEnterEvent, (_, e) => DropZone.Classes.Set("dragOver", MediaFiles.HasFiles(e)));
        AddHandler(DragDrop.DragLeaveEvent, (_, _) => DropZone.Classes.Set("dragOver", false));
    }

    private bool _syncingQuick;

    /// <summary>
    /// 流程设置 pane: shortcuts to the config items the pipeline reads (the same items as the settings page).
    /// Changes go through Cfg, so the other pages follow via the Cfg → SignalBus wiring of SettingInterface.
    /// </summary>
    private void SetupQuickSettings()
    {
        QuickModelCombo.ItemsSource = Models.Options.AvailableTranscribeModel;
        QuickLanguageCombo.ItemsSource = Models.Options.TargetLanguage;

        void Sync()
        {
            _syncingQuick = true;
            QuickModelCombo.SelectedItem = Cfg.TranscribeModel.Value;
            QuickOptimizeSwitch.IsChecked = Cfg.NeedOptimize.Value;
            QuickTranslateSwitch.IsChecked = Cfg.NeedTranslate.Value;
            QuickLanguageCombo.SelectedItem = Cfg.TargetLanguage.Value;
            QuickLanguageCombo.IsEnabled = Cfg.NeedTranslate.Value;
            QuickVideoSwitch.IsChecked = Cfg.NeedVideo.Value;
            _syncingQuick = false;
        }

        Sync();
        Cfg.TranscribeModel.ValueChanged += _ => Sync();
        Cfg.NeedOptimize.ValueChanged += _ => Sync();
        Cfg.NeedTranslate.ValueChanged += _ => Sync();
        Cfg.TargetLanguage.ValueChanged += _ => Sync();
        Cfg.NeedVideo.ValueChanged += _ => Sync();

        QuickModelCombo.SelectionChanged += (_, _) =>
        {
            if (!_syncingQuick && QuickModelCombo.SelectedItem is string model) Cfg.TranscribeModel.Value = model;
        };
        QuickLanguageCombo.SelectionChanged += (_, _) =>
        {
            if (!_syncingQuick && QuickLanguageCombo.SelectedItem is string language) Cfg.TargetLanguage.Value = language;
        };
        QuickOptimizeSwitch.IsCheckedChanged += (_, _) =>
        {
            if (!_syncingQuick) Cfg.NeedOptimize.Value = QuickOptimizeSwitch.IsChecked == true;
        };
        QuickTranslateSwitch.IsCheckedChanged += (_, _) =>
        {
            if (!_syncingQuick) Cfg.NeedTranslate.Value = QuickTranslateSwitch.IsChecked == true;
        };
        QuickVideoSwitch.IsCheckedChanged += (_, _) =>
        {
            if (!_syncingQuick) Cfg.NeedVideo.Value = QuickVideoSwitch.IsChecked == true;
        };
        MoreSettingsButton.Click += (_, _) => (TopLevel.GetTopLevel(this) as MainWindow)?.SwitchTo("settings");
    }

    private void SetupValues()
    {
        SetupQuickSettings();
        SearchInput.Text = "";
        OnSearchInputChanged();
    }

    private async Task LoadVersionAsync()
    {
        var version = "";
        try
        {
            var paths = await Bridge.Instance.RequestAsync("paths");
            version = paths?["VERSION"]?.GetValue<string>() ?? "";
        }
        catch { /* backend not available: keep empty version */ }
        InfoLabel.Text = I18n.Tr($"©VideoCaptioner {version} • By Weifeng", Ctx);
    }

    private bool IsFolderMode => StartIcon.Symbol == Symbol.Folder;

    private async void OnStartClicked()
    {
        if (IsFolderMode)
        {
            var filePath = await MediaFiles.PickMediaAsync(Ctx);
            if (!string.IsNullOrEmpty(filePath)) SearchInput.Text = filePath;
            return;
        }
        Process();
    }

    private void OnSearchInputChanged()
    {
        var empty = string.IsNullOrEmpty(SearchInput.Text);
        StartIcon.Symbol = empty ? Symbol.Folder : Symbol.Play;
        StartButtonText.Text = empty ? I18n.Tr("选择文件", Ctx) : I18n.Tr("开始处理", Ctx);
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = MediaFiles.HasFiles(e) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        DropZone.Classes.Set("dragOver", false);
        foreach (var filePath in MediaFiles.DroppedPaths(e))
        {
            if (!File.Exists(filePath)) continue;

            var fileExt = MediaFiles.Ext(filePath);
            // 检查文件格式是否支持
            if (MediaFiles.IsSupported(fileExt))
            {
                SearchInput.Text = filePath;
                StatusLabel.Text = I18n.Tr("导入成功", Ctx);
                Toast.Success(I18n.Tr("导入成功", Ctx), I18n.Tr("导入媒体文件成功", Ctx), DurationSuccess);
                break;
            }
            Toast.Error(I18n.Tr("格式错误", Ctx) + fileExt, I18n.Tr("不支持该文件格式", Ctx), DurationError);
        }
        e.Handled = true;
    }

    private static bool IsValidUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == "http" || u.Scheme == "https") && u.Host.Length > 0;

    private void ProcessFile(string filePath) => Finished?.Invoke(filePath);

    private void ProcessUrl(string url)
    {
        // 检测 cookies.txt 文件
        if (!File.Exists(Path.Combine(Cfg.AppDataPath, "cookies.txt")))
            Toast.Warning(I18n.Tr("警告", Ctx), I18n.Tr("建议根据文档配置cookies.txt文件，以可以下载高清视频", Ctx), DurationWarning);

        // 创建视频下载线程
        var job = Bridge.Instance.Run("video_download", new { url, work_dir = Cfg.WorkDir.Value });
        job.Progress += OnCreateTaskProgress;
        job.Error += OnCreateTaskError;
        job.Finished += data => OnVideoDownloadFinished(data?.GetValue<string>() ?? "");

        Toast.Info(I18n.Tr("开始下载", Ctx), I18n.Tr("开始下载视频...", Ctx), DurationInfo);
    }

    /// <summary>视频下载完成的回调函数</summary>
    private void OnVideoDownloadFinished(string videoFilePath)
    {
        if (!string.IsNullOrEmpty(videoFilePath))
        {
            Finished?.Invoke(videoFilePath);
            Toast.Success(I18n.Tr("下载成功", Ctx), I18n.Tr("视频下载完成，开始自动处理...", Ctx), DurationSuccess, ToastPosition.Bottom);
        }
        else
        {
            Toast.Error(I18n.Tr("错误", Ctx), I18n.Tr("视频下载失败", Ctx), DurationError);
        }
    }

    private void OnCreateTaskProgress(int value, string status)
    {
        ProgressBar.IsVisible = true;
        StatusLabel.IsVisible = true;
        ProgressBar.Value = value;
        StatusLabel.Text = status;
    }

    private void OnCreateTaskError(string error)
    {
        Toast.Error(I18n.Tr("错误", Ctx), I18n.Tr(error, Ctx), DurationError);
    }

    /// <summary>set_task(task): show the task's file path in the search box.</summary>
    public void SetTask(string filePath)
    {
        SearchInput.Text = filePath;
    }

    public void Process()
    {
        var searchInput = SearchInput.Text ?? "";
        if (File.Exists(searchInput))
            ProcessFile(searchInput);
        else if (IsValidUrl(searchInput))
            ProcessUrl(searchInput);
        else
            Toast.Error(I18n.Tr("错误", Ctx), I18n.Tr("请输入音视频文件路径或URL", Ctx), DurationError);
    }

    /// <summary>显示日志窗口</summary>
    private void ShowLogWindow()
    {
        if (_logWindow == null)
        {
            _logWindow = new LogWindow();
            _logWindow.Closed += (_, _) => _logWindow = null;
            _logWindow.Show();
        }
        else
        {
            _logWindow.Activate();
        }
    }

    /// <summary>显示捐助窗口</summary>
    private static async Task ShowDonateDialogAsync()
    {
        await new DonateDialog().ShowAsync();
    }
}
