using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using VideoCaptioner.Avalonia.Views.Components;
using VideoCaptioner.Avalonia.Models;
using VideoCaptioner.Avalonia.Services;

namespace VideoCaptioner.Avalonia.Views.Home;

/// <summary>Port of videocaptioner/ui/view/video_synthesis_interface.py (字幕视频合成).</summary>
public partial class VideoSynthesisInterface : UserControl
{
    private const string Ctx = "VideoSynthesisInterface";
    private const int InfobarDurationSuccess = 2000; // core.constant INFOBAR_DURATION_SUCCESS
    private const int InfobarDurationWarning = 5000; // INFOBAR_DURATION_WARNING
    private const int InfobarDurationError = 10000;  // INFOBAR_DURATION_ERROR

    // core.entities.SupportedSubtitleFormats / SupportedVideoFormats
    private static readonly string[] SubtitleFormats = ["srt", "ass", "vtt"];
    private static readonly string[] VideoFormats =
    [
        "mp4", "webm", "ogm", "mov", "mkv", "avi", "wmv", "flv", "m4v", "ts", "mpg", "mpeg", "vob", "asf",
        "rm", "rmvb", "m2ts", "mts", "dv", "gxf", "tod", "mxf", "f4v",
    ];

    /// <summary>C# view of the SynthesisTask created by TaskFactory in the backend.</summary>
    private sealed class SynthesisTaskInfo
    {
        public string VideoPath = "";
        public string SubtitlePath = "";
        public bool NeedNextTask;
        public string? TaskId;
        public string? OutputPath;
    }

    private SynthesisTaskInfo? _task;
    private BridgeJob? _job;
    /// <summary>True while controls are updated programmatically (QAction.setChecked does not emit triggered).</summary>
    private bool _syncing;

    private static string Tr(string s) => I18n.Tr(s, Ctx);

    public VideoSynthesisInterface()
    {
        InitializeComponent();
        SetupMenus();
        SetValue();
        SetupSignals();
    }

    private void SetupMenus()
    {
        RenderModeButton.ItemsSource = Options.SubtitleRenderMode;
        RenderModeButton.SelectionChanged += (_, _) =>
        {
            if (!_syncing && RenderModeButton.SelectedItem is string mode) OnRenderModeChanged(mode);
        };
        VideoQualityButton.ItemsSource = Options.VideoQuality;
        VideoQualityButton.SelectionChanged += (_, _) =>
        {
            if (!_syncing && VideoQualityButton.SelectedItem is string quality) OnVideoQualityActionChanged(quality);
        };
    }

    /// <summary>Update a control without running its user-interaction handler.</summary>
    private void Sync(Action update)
    {
        var old = _syncing;
        _syncing = true;
        try { update(); }
        finally { _syncing = old; }
    }

    private void SetChecked(ToggleSwitch toggle, bool isChecked) => Sync(() => toggle.IsChecked = isChecked);

    private void SetComboText(ComboBox combo, string text) => Sync(() => combo.SelectedItem = text);

    private static bool IsOn(ToggleSwitch toggle) => toggle.IsChecked == true;

    private void SetupSignals()
    {
        // 文件选择相关信号
        SubtitleButton.Click += async (_, _) => await ChooseSubtitleFile();
        VideoButton.Click += async (_, _) => await ChooseVideoFile();

        // 合成和文件夹相关信号
        SynthesizeButton.Click += (_, _) => StartVideoSynthesis(needCreateTask: true);
        FolderAction.Click += (_, _) => OpenVideoFolder();

        // Action.triggered: only user clicks, not programmatic setChecked
        SoftSubtitleAction.IsCheckedChanged += (_, _) => { if (!_syncing) OnSoftSubtitleActionTriggered(IsOn(SoftSubtitleAction)); };
        UseStyleAction.IsCheckedChanged += (_, _) => { if (!_syncing) OnUseStyleActionTriggered(IsOn(UseStyleAction)); };
        NeedVideoAction.IsCheckedChanged += (_, _) => { if (!_syncing) OnNeedVideoActionTriggered(IsOn(NeedVideoAction)); };

        // 全局 signalBus
        SignalBus.SoftSubtitleChanged += OnSoftSubtitleChanged;
        SignalBus.NeedVideoChanged += OnNeedVideoChanged;
        SignalBus.VideoQualityChanged += OnVideoQualityChanged;
        SignalBus.UseSubtitleStyleChanged += OnUseStyleChanged;
        SignalBus.SubtitleRenderModeChanged += OnRenderModeChangedExternal;

        // 拖放
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, (_, _) => DropZone.Classes.Remove("dragOver"));
        AddHandler(DragDrop.DropEvent, OnDrop);

        // Development hook for screenshots: VC_DEV_PLAYER=<video path> opens the video player window
        if (Environment.GetEnvironmentVariable("VC_DEV_PLAYER") is { Length: > 0 } devVideo && File.Exists(devVideo))
            AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() =>
            {
                var player = new MyVideoWidget();
                player.Show();
                player.SetVideo(devVideo);
                player.Play();
            }, DispatcherPriority.Background);
    }

    /// <summary>设置初始值</summary>
    private void SetValue()
    {
        SetChecked(SoftSubtitleAction, Cfg.SoftSubtitle.Value);
        SetChecked(NeedVideoAction, Cfg.NeedVideo.Value);
        SetComboText(VideoQualityButton, Cfg.VideoQuality.Value);

        // 设置样式相关初始值
        SetChecked(UseStyleAction, Cfg.UseSubtitleStyle.Value);
        SetComboText(RenderModeButton, Cfg.SubtitleRenderMode.Value);
        UpdateSynthesisControlsState();
    }

    // ------------------------------------------------------------------ 命令栏

    /// <summary>处理软字幕按钮点击（更新配置+显示InfoBar）</summary>
    private void OnSoftSubtitleActionTriggered(bool isChecked)
    {
        Cfg.SoftSubtitle.Value = isChecked;

        if (isChecked)
        {
            // 开启软字幕时自动关闭使用样式
            if (IsOn(UseStyleAction))
            {
                SetChecked(UseStyleAction, false);
                Cfg.UseSubtitleStyle.Value = false;
                UpdateStyleControlsState();
            }
            Toast.Info(Tr("开启软字幕"), Tr("字幕作为独立轨道嵌入视频，不包含字幕样式"), 3000, ToastPosition.Bottom);
        }
        else
        {
            Toast.Info(Tr("开启硬烧录字幕"), Tr("字幕直接烧录到视频画面中，包含字幕样式"), 3000, ToastPosition.Bottom);
        }
    }

    /// <summary>处理外部软字幕配置变更（仅更新UI状态）</summary>
    private void OnSoftSubtitleChanged(bool isChecked) => SetChecked(SoftSubtitleAction, isChecked);

    /// <summary>处理视频合成按钮点击（更新配置+显示InfoBar）</summary>
    private void OnNeedVideoActionTriggered(bool isChecked)
    {
        Cfg.NeedVideo.Value = isChecked;
        UpdateSynthesisControlsState();

        if (isChecked)
            Toast.Info(Tr("开启视频合成"), Tr("将进行视频与字幕的合成操作"), 3000, ToastPosition.Bottom);
        else
            Toast.Info(Tr("关闭视频合成"), Tr("仅生成字幕文件，不生成新的视频文件"), 3000, ToastPosition.Bottom);
    }

    /// <summary>处理外部视频合成配置变更（仅更新UI状态）</summary>
    private void OnNeedVideoChanged(bool isChecked)
    {
        SetChecked(NeedVideoAction, isChecked);
        UpdateSynthesisControlsState();
    }

    /// <summary>处理质量选择</summary>
    private void OnVideoQualityActionChanged(string qualityText)
    {
        if (!Options.VideoQuality.Contains(qualityText)) return;
        Cfg.VideoQuality.Value = qualityText;
        SetComboText(VideoQualityButton, qualityText);
    }

    /// <summary>处理外部质量配置变更（仅更新UI状态）</summary>
    private void OnVideoQualityChanged(string qualityText) => SetComboText(VideoQualityButton, qualityText);

    /// <summary>处理使用样式开关点击</summary>
    private void OnUseStyleActionTriggered(bool isChecked)
    {
        Cfg.UseSubtitleStyle.Value = isChecked;
        UpdateStyleControlsState();

        if (isChecked)
        {
            // 启用样式时自动关闭软字幕
            if (IsOn(SoftSubtitleAction))
            {
                SetChecked(SoftSubtitleAction, false);
                Cfg.SoftSubtitle.Value = false;
            }
            Toast.Info(Tr("启用字幕样式"), Tr("已自动切换为硬字幕渲染"), 3000, ToastPosition.Bottom);
        }
        else
        {
            Toast.Info(Tr("关闭字幕样式"), Tr("将使用默认字幕渲染"), 3000, ToastPosition.Bottom);
        }
    }

    /// <summary>处理外部使用样式配置变更（仅更新 UI）</summary>
    private void OnUseStyleChanged(bool isChecked)
    {
        SetChecked(UseStyleAction, isChecked);
        UpdateStyleControlsState();
    }

    /// <summary>处理渲染模式选择（本界面触发）</summary>
    private void OnRenderModeChanged(string modeText)
    {
        if (!Options.SubtitleRenderMode.Contains(modeText)) return;
        Cfg.SubtitleRenderMode.Value = modeText;
        SetComboText(RenderModeButton, modeText);
        SignalBus.EmitSubtitleRenderModeChanged(modeText);
    }

    /// <summary>处理外部渲染模式变更（仅更新 UI）</summary>
    private void OnRenderModeChangedExternal(string modeText) => SetComboText(RenderModeButton, modeText);

    /// <summary>更新所有合成相关控件的启用/禁用状态</summary>
    private void UpdateSynthesisControlsState()
    {
        var needVideo = IsOn(NeedVideoAction);

        // 合成视频关闭时，禁用所有相关选项
        SoftSubtitleCard.IsEnabled = needVideo;
        UseStyleCard.IsEnabled = needVideo;
        VideoQualityCard.IsEnabled = needVideo;

        // 渲染模式按钮需要同时满足：合成视频开启 且 使用样式开启
        UpdateStyleControlsState();
    }

    /// <summary>更新样式相关控件的启用/禁用状态</summary>
    private void UpdateStyleControlsState()
    {
        RenderModeCard.IsEnabled = IsOn(NeedVideoAction) && IsOn(UseStyleAction);
    }

    // ------------------------------------------------------------------ 文件选择

    private async Task ChooseSubtitleFile()
    {
        var file = await Dialogs.OpenFileAsync(Tr("选择字幕文件"),
            (Tr("字幕文件"), SubtitleFormats.Select(f => "*." + f).ToArray()));
        if (!string.IsNullOrEmpty(file)) SubtitleInput.Text = file;
    }

    private async Task ChooseVideoFile()
    {
        var file = await Dialogs.OpenFileAsync(Tr("选择视频文件"),
            (Tr("视频文件"), VideoFormats.Select(f => "*." + f).ToArray()));
        if (!string.IsNullOrEmpty(file)) VideoInput.Text = file;
    }

    // ------------------------------------------------------------------ 任务

    private SynthesisTaskInfo? CreateTask()
    {
        var subtitleFile = SubtitleInput.Text ?? "";
        var videoFile = VideoInput.Text ?? "";
        if (subtitleFile.Length == 0 || videoFile.Length == 0)
        {
            Toast.Error(Tr("错误"), Tr("请选择字幕文件和视频文件"), InfobarDurationError, ToastPosition.Top);
            return null;
        }
        return new SynthesisTaskInfo { VideoPath = videoFile, SubtitlePath = subtitleFile };
    }

    /// <summary>set_task(TaskFactory.create_synthesis_task(video, subtitle, need_next_task, task_id))</summary>
    public void SetTask(string videoPath, string subtitlePath, bool needNextTask, string? taskId)
    {
        _task = new SynthesisTaskInfo
        {
            VideoPath = videoPath,
            SubtitlePath = subtitlePath,
            NeedNextTask = needNextTask,
            TaskId = taskId,
        };
        UpdateInfo();
    }

    private void UpdateInfo()
    {
        if (_task == null) return;
        VideoInput.Text = _task.VideoPath;
        SubtitleInput.Text = _task.SubtitlePath;
    }

    private void StartVideoSynthesis(bool needCreateTask)
    {
        SynthesizeButton.IsEnabled = false;
        ProgressBar.Classes.Remove("error"); // resume()
        ProgressBar.Value = 0;               // reset()
        if (needCreateTask)
            _task = CreateTask();

        if (_task == null)
        {
            SynthesizeButton.IsEnabled = true;
            return;
        }

        var task = _task;
        var job = Bridge.Instance.Run("synthesis", new
        {
            video_path = task.VideoPath,
            subtitle_path = task.SubtitlePath,
            need_next_task = task.NeedNextTask,
            task_id = task.TaskId,
        });
        _job = job;
        job.Event += (name, data) =>
        {
            if (name == "task") ReadTask(task, data);
        };
        job.Progress += OnVideoSynthesisProgress;
        job.Finished += data =>
        {
            ReadTask(task, data);
            OnVideoSynthesisFinished();
        };
        job.Error += OnVideoSynthesisError;
    }

    private static void ReadTask(SynthesisTaskInfo task, JsonNode? data)
    {
        if (data?["output_path"]?.GetValue<string>() is { Length: > 0 } output) task.OutputPath = output;
        if (data?["task_id"]?.GetValue<string>() is { Length: > 0 } id) task.TaskId = id;
    }

    public void Process() => StartVideoSynthesis(needCreateTask: false);

    private void OnVideoSynthesisFinished()
    {
        _job = null;
        SynthesizeButton.IsEnabled = true;
        ProgressBar.Value = 100;
        OpenVideoFolder();
        Toast.Success(Tr("成功"), Tr("视频合成已完成"), InfobarDurationSuccess, ToastPosition.Top);
    }

    private void OnVideoSynthesisProgress(int progress, string message)
    {
        ProgressBar.Value = progress;
        StatusLabel.Text = I18n.Tr(message, "VideoSynthesisThread");
    }

    private void OnVideoSynthesisError(string error)
    {
        _job = null;
        SynthesizeButton.IsEnabled = true;
        ProgressBar.Classes.Add("error");
        Toast.Error(Tr("错误"), error, InfobarDurationError, ToastPosition.Top);
    }

    private void OpenVideoFolder()
    {
        if (_task is { } task && (!string.IsNullOrEmpty(task.OutputPath) || !string.IsNullOrEmpty(task.VideoPath)))
        {
            // output_path exists -> its folder, otherwise the video's folder
            string? targetDir;
            if (!string.IsNullOrEmpty(task.OutputPath) && File.Exists(task.OutputPath))
                targetDir = Path.GetDirectoryName(task.OutputPath);
            else if (!string.IsNullOrEmpty(task.VideoPath))
                targetDir = Path.GetDirectoryName(task.VideoPath);
            else
                targetDir = Path.GetDirectoryName(task.OutputPath!);
            if (!string.IsNullOrEmpty(targetDir))
                Dialogs.OpenUrl(targetDir); // open_folder -> os.startfile
        }
        else
        {
            Toast.Warning(Tr("警告"), Tr("没有可用的视频文件夹"), InfobarDurationWarning, ToastPosition.Top);
        }
    }

    // ------------------------------------------------------------------ 拖放

#pragma warning disable CS0618 // DragEventArgs.Data: IDataObject API is still the portable one in 11.3
    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var hasFiles = e.Data.Contains(DataFormats.Files);
        e.DragEffects = hasFiles ? DragDropEffects.Copy : DragDropEffects.None;
        if (hasFiles) DropZone.Classes.Add("dragOver");
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        DropZone.Classes.Remove("dragOver");
        var files = e.Data.GetFiles()?.Select(f => f.TryGetLocalPath()).Where(p => p != null).Cast<string>().ToList() ?? [];
        e.Handled = true;
        foreach (var filePath in files)
        {
            if (!File.Exists(filePath)) continue;

            var fileExt = Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant();

            // 检查文件格式是否支持
            if (SubtitleFormats.Contains(fileExt))
            {
                SubtitleInput.Text = filePath;
                Toast.Success(Tr("导入成功"), Tr("字幕文件已放入输入框"), InfobarDurationSuccess);
                break;
            }
            if (VideoFormats.Contains(fileExt))
            {
                VideoInput.Text = filePath;
                Toast.Success(Tr("导入成功"), Tr("视频文件已输入框"), InfobarDurationSuccess);
                break;
            }
            Toast.Error(Tr("格式错误") + fileExt, Tr("请拖入视频或者字幕文件"), InfobarDurationError);
        }
    }
#pragma warning restore CS0618
}
