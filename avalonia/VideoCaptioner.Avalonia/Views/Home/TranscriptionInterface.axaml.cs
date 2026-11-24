using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using FluentAvalonia.UI.Controls;
using VideoCaptioner.Avalonia.Models;
using VideoCaptioner.Avalonia.Services;
using VideoCaptioner.Avalonia.Views.Components;

namespace VideoCaptioner.Avalonia.Views.Home;

/// <summary>
/// Port of videocaptioner/ui/view/transcription_interface.py: TranscriptionInterface (转录界面)
/// with its VideoInfoCard (video info + start button). The card is inlined in the axaml; its
/// methods keep the Python names and translation context "VideoInfoCard".
/// </summary>
public partial class TranscriptionInterface : UserControl
{
    private const string Ctx = "TranscriptionInterface";
    private const string CardCtx = "VideoInfoCard";
    // core/constant.py
    private const int DurationError = 10000, DurationWarning = 5000, DurationSuccess = 2000;

    /// <summary>TranscribeTask of the card (python VideoInfoCard.task).</summary>
    private sealed class TranscribeTask
    {
        public required string FilePath { get; set; }
        public bool NeedNextTask { get; init; }
        public string? TaskId { get; init; }
        public string? OutputPath { get; set; }
    }

    private bool _isProcessing;
    private TranscribeTask? _task;
    private JsonNode? _videoInfo;
    private int _selectedAudioTrackIndex; // 默认选择第一条音轨
    private BridgeJob? _transcriptJob;
    private BridgeJob? _videoInfoJob;
    private readonly FAMenuFlyout _modelMenu = new();
    private readonly FAMenuFlyout _gpuMenu = new();
    /// <summary>bridge "gpu_list" entries: GPU name, the engine that can use it and its device setting.</summary>
    private readonly List<(string Name, string Engine, string Device, RadioMenuFlyoutItem Item)> _gpus = [];

    /// <summary>Graphics card glyph (FluentAvalonia's Symbol set has none).</summary>
    private static readonly Geometry GpuIcon = StreamGeometry.Parse(
        "M1,3 H2.2 V13 H1 Z M3,4 H14 A1,1 0 0 1 15,5 V10 A1,1 0 0 1 14,11 H3 Z " +
        "M4.2,5.2 V9.8 H13.8 V5.2 Z M6.5,5.9 A1.6,1.6 0 1 1 6.5,9.1 A1.6,1.6 0 1 1 6.5,5.9 Z " +
        "M11.2,5.9 A1.6,1.6 0 1 1 11.2,9.1 A1.6,1.6 0 1 1 11.2,5.9 Z M4,11.6 H9 V12.8 H4 Z");

    /// <summary>FluentIcon.ROBOT (FluentAvalonia's Symbol set has no robot icon).</summary>
    private static readonly Geometry RobotIcon = StreamGeometry.Parse(
        "M4,5 H12 A2,2 0 0 1 14,7 V12 A2,2 0 0 1 12,14 H4 A2,2 0 0 1 2,12 V7 A2,2 0 0 1 4,5 Z " +
        "M4,6.2 A0.8,0.8 0 0 0 3.2,7 V12 A0.8,0.8 0 0 0 4,12.8 H12 A0.8,0.8 0 0 0 12.8,12 V7 A0.8,0.8 0 0 0 12,6.2 Z " +
        "M7.4,3 H8.6 V5 H7.4 Z M8,0.8 A1.1,1.1 0 1 1 8,3 A1.1,1.1 0 1 1 8,0.8 Z " +
        "M5.5,8.1 A1.1,1.1 0 1 1 5.5,10.3 A1.1,1.1 0 1 1 5.5,8.1 Z M10.5,8.1 A1.1,1.1 0 1 1 10.5,10.3 A1.1,1.1 0 1 1 10.5,8.1 Z");

    public TranscriptionInterface()
    {
        InitializeComponent();
        SetupCommandBar();
        SetupSignals();
        SetValue();
        UpdateEmptyState();

        // Development hook for screenshots: VC_HOME_FILE=<media path> loads a file into the card
        if (Environment.GetEnvironmentVariable("VC_HOME_FILE") is { Length: > 0 } devFile && File.Exists(devFile))
            AttachedToVisualTree += (_, _) => { if (_videoInfo == null && _videoInfoJob == null) UpdateInfo(devFile); };
    }

    /// <summary>(subtitle output path, video path)</summary>
    public event Action<string, string>? Finished;

    // ------------------------------------------------------------------ TranscriptionInterface

    private void SetupCommandBar()
    {
        // 只显示当前平台可用的模型（macOS 上不显示 FasterWhisper）
        foreach (var model in Options.AvailableTranscribeModel)
        {
            var isOnline = model is "Whisper [API] ✨" or "B 接口" or "J 接口";
            var item = new MenuFlyoutItem
            {
                Text = model,
                IconSource = isOnline
                    ? new SymbolIconSource { Symbol = Symbol.Globe }
                    : new PathIconSource { Data = RobotIcon },
            };
            var text = model;
            item.Click += (_, _) => OnTranscriptionModelChanged(text);
            _modelMenu.Items.Add(item);
        }
        _modelMenu.Placement = PlacementMode.BottomEdgeAlignedLeft;
        ModelButton.Flyout = _modelMenu;

        GpuButtonIcon.Data = GpuIcon;
        _gpuMenu.Placement = PlacementMode.BottomEdgeAlignedLeft;
        GpuButton.Flyout = _gpuMenu;
        Cfg.TranscribeModel.ValueChanged += _ => UpdateGpuSelection();
        Cfg.FasterWhisperDevice.ValueChanged += _ => UpdateGpuSelection();
        Cfg.WhisperCppDevice.ValueChanged += _ => UpdateGpuSelection();
        _ = LoadGpusAsync();
    }

    /// <summary>
    /// Fill the 显卡 menu. An engine is tied to a GPU vendor (FasterWhisper = NVIDIA CUDA, WhisperCpp =
    /// any Vulkan GPU), so picking a GPU switches the transcription model and its device setting together.
    /// </summary>
    private async Task LoadGpusAsync()
    {
        JsonNode? res;
        try
        {
            res = await Bridge.Instance.RequestAsync("gpu_list");
        }
        catch
        {
            return; // backend unavailable: the button stays hidden
        }
        foreach (var gpu in res?["gpus"]?.AsArray() ?? [])
        {
            var name = gpu!["name"]!.GetValue<string>();
            var engine = gpu["engine"]!.GetValue<string>();
            var device = gpu["device"]!.GetValue<string>();
            if (!Options.AvailableTranscribeModel.Contains(engine)) continue;
            var backend = engine == "WhisperCpp" ? "WhisperCpp · Vulkan" : "FasterWhisper · CUDA";
            var item = new RadioMenuFlyoutItem { Text = $"{name}  ({backend})", GroupName = "TranscribeGpu" };
            item.Click += (_, _) => SelectGpu(engine, device);
            _gpuMenu.Items.Add(item);
            _gpus.Add((name, engine, device, item));
        }
        GpuButton.IsVisible = _gpus.Count > 0;
        UpdateGpuSelection();
    }

    private void SelectGpu(string engine, string device)
    {
        if (engine == "WhisperCpp") Cfg.WhisperCppDevice.Value = device;
        else Cfg.FasterWhisperDevice.Value = device;
        OnTranscriptionModelChanged(engine);
        // after the click: RadioMenuFlyoutItem toggles its own IsChecked once the Click handlers return
        global::Avalonia.Threading.Dispatcher.UIThread.Post(UpdateGpuSelection);
    }

    /// <summary>The GPU in use follows from the current model + device; none for online models, CPU or "auto".</summary>
    private void UpdateGpuSelection()
    {
        var model = Cfg.TranscribeModel.Value;
        var device = model == "WhisperCpp" ? Cfg.WhisperCppDevice.Value : Cfg.FasterWhisperDevice.Value;
        string? current = null;
        foreach (var gpu in _gpus)
        {
            gpu.Item.IsChecked = gpu.Engine == model && gpu.Device == device;
            if (gpu.Item.IsChecked) current = gpu.Name;
        }
        GpuButtonText.Text = current ?? I18n.Tr("显卡", Ctx);
    }

    private void SetupSignals()
    {
        OpenFileButton.Click += async (_, _) => await OnFileSelectAsync();
        EmptyOpenButton.Click += async (_, _) => await OnFileSelectAsync();
        SettingButton.Click += async (_, _) => await ShowOutputSettingsAsync();
        StartButton.Click += (_, _) => OnStartButtonClicked();
        OpenFolderButton.Click += (_, _) => OnOpenFolderClicked();

        // 全局信号连接
        SignalBus.TranscriptionModelChanged += OnTranscriptionModelChanged;

        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        // drop zone highlight while a file is dragged over the page
        AddHandler(DragDrop.DragEnterEvent, (_, e) => DropZone.Classes.Set("dragOver", MediaFiles.HasFiles(e)));
        AddHandler(DragDrop.DragLeaveEvent, (_, _) => DropZone.Classes.Set("dragOver", false));
    }

    /// <summary>Empty state (drop zone) until a file or task is loaded, then the media card.</summary>
    private void UpdateEmptyState()
    {
        var hasMedia = _task != null || _videoInfo != null;
        EmptyState.IsVisible = !hasMedia;
        MediaCard.IsVisible = hasMedia;
    }

    /// <summary>显示转录设置对话框</summary>
    private static async Task ShowOutputSettingsAsync()
    {
        await new TranscriptionSettingDialog().ShowAsync();
    }

    /// <summary>设置转录模型</summary>
    private void SetValue()
    {
        OnTranscriptionModelChanged(Cfg.TranscribeModel.Value);
    }

    /// <summary>处理转录模型改变</summary>
    private void OnTranscriptionModelChanged(string modelName)
    {
        ModelButtonText.Text = I18n.Tr(modelName, Ctx);
        TranscriptionSettingCard.OnModelChanged(modelName);
        if (Options.TranscribeModel.Contains(modelName))
            Cfg.TranscribeModel.Value = modelName;
    }

    /// <summary>转录完成处理 (VideoInfoCard.finished)</summary>
    private void OnInterfaceTranscriptFinished(TranscribeTask task)
    {
        _isProcessing = false;
        if (task.NeedNextTask)
        {
            Finished?.Invoke(task.OutputPath ?? "", task.FilePath);
            Toast.Success(I18n.Tr("转录完成", Ctx), I18n.Tr("开始字幕优化...", Ctx), DurationSuccess, ToastPosition.Bottom);
        }
    }

    /// <summary>文件选择处理</summary>
    private async Task OnFileSelectAsync()
    {
        var filePath = await MediaFiles.PickMediaAsync(Ctx);
        if (!string.IsNullOrEmpty(filePath)) UpdateInfo(filePath);
    }

    /// <summary>设置UI: VideoInfoThread(file_path)</summary>
    private void UpdateInfo(string filePath)
    {
        var job = Bridge.Instance.Run("video_info", new { file_path = filePath });
        _videoInfoJob = job;
        job.Finished += info => { if (info != null) CardUpdateInfo(info); };
        job.Error += OnVideoInfoError;
    }

    /// <summary>处理视频信息提取错误</summary>
    private void OnVideoInfoError(string errorMsg)
    {
        _isProcessing = false;
        Toast.Error(I18n.Tr("错误", Ctx), I18n.Tr(errorMsg, Ctx), DurationError);
    }

    /// <summary>设置任务并更新UI (HomeInterface creates the task with TaskFactory.create_transcribe_task).</summary>
    public void SetTask(string filePath, bool needNextTask, string? taskId)
    {
        _task = new TranscribeTask { FilePath = filePath, NeedNextTask = needNextTask, TaskId = taskId };
        CardSetTask();
        UpdateEmptyState();
        UpdateInfo(filePath);
    }

    /// <summary>主处理函数</summary>
    public void Process()
    {
        _isProcessing = true;
        StartTranscription(needCreateTask: false);
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = MediaFiles.HasFiles(e) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        DropZone.Classes.Set("dragOver", false);
        if (_isProcessing)
        {
            Toast.Warning(I18n.Tr("警告", Ctx), I18n.Tr("正在处理中，请等待当前任务完成", Ctx), DurationWarning);
            return;
        }

        foreach (var filePath in MediaFiles.DroppedPaths(e))
        {
            if (!File.Exists(filePath)) continue;

            var fileExt = MediaFiles.Ext(filePath);
            // 检查文件格式是否支持
            if (MediaFiles.IsSupported(fileExt))
            {
                UpdateInfo(filePath);
                Toast.Success(I18n.Tr("导入成功", Ctx), I18n.Tr("开始语音转文字", Ctx), DurationSuccess);
                break;
            }
            Toast.Error(I18n.Tr("格式错误", Ctx) + fileExt, I18n.Tr("请拖入音频或视频文件", Ctx), DurationError);
        }
    }

    // ------------------------------------------------------------------ VideoInfoCard

    /// <summary>更新视频信息显示</summary>
    private void CardUpdateInfo(JsonNode videoInfo)
    {
        CardResetUi();
        _videoInfo = videoInfo;

        var fileName = Str(videoInfo, "file_name");
        var dot = fileName.LastIndexOf('.');
        VideoTitle.Text = dot >= 0 ? fileName[..dot] : fileName;
        ResolutionInfo.Content = I18n.Tr("画质: ", CardCtx) + $"{Int(videoInfo, "width")}x{Int(videoInfo, "height")}";
        var filePath = Str(videoInfo, "file_path");
        VideoPath.Text = filePath;
        VideoPath.IsVisible = filePath.Length > 0;
        var fileSizeMb = File.Exists(filePath) ? new FileInfo(filePath).Length / 1024.0 / 1024.0 : 0;
        FileSizeInfo.Content = I18n.Tr("大小: ", CardCtx) + $"{fileSizeMb:0.0} MB";
        var duration = TimeSpan.FromSeconds((int)(videoInfo["duration_seconds"]?.GetValue<double>() ?? 0));
        DurationInfo.Content = I18n.Tr("时长: ", CardCtx) + FormatTimedelta(duration);

        // 更新音轨选择按钮
        UpdateAudioTracks(videoInfo);

        StartButton.IsEnabled = !_isProcessing;
        UpdateThumbnail(Str(videoInfo, "thumbnail_path"));
        UpdateEmptyState();
    }

    /// <summary>str(datetime.timedelta(seconds=n)): H:MM:SS or "N day(s), H:MM:SS".</summary>
    private static string FormatTimedelta(TimeSpan t)
    {
        var hms = $"{t.Hours}:{t.Minutes:00}:{t.Seconds:00}";
        return t.Days > 0 ? $"{t.Days} day{(t.Days == 1 ? "" : "s")}, {hms}" : hms;
    }

    /// <summary>更新音轨选择按钮</summary>
    private void UpdateAudioTracks(JsonNode videoInfo)
    {
        var audioStreams = videoInfo["audio_streams"] as JsonArray ?? [];

        if (audioStreams.Count > 1)
        {
            // 多音轨，显示选择按钮，默认选择第一条音轨（数组索引0）
            _selectedAudioTrackIndex = 0;
            UpdateAudioTrackButtonText(audioStreams, 0);

            // 创建下拉菜单
            var menu = new FAMenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
            for (var i = 0; i < audioStreams.Count; i++)
            {
                var lang = Str(audioStreams[i], "language");
                // 构建菜单项文本（使用序号 i+1）
                var text = I18n.Tr("音轨", CardCtx) + (i + 1);
                if (lang.Length > 0) text += $" ({lang})";

                var action = new MenuFlyoutItem { Text = text };
                var arrayIdx = i;
                action.Click += (_, _) => OnAudioTrackSelected(arrayIdx, audioStreams);
                menu.Items.Add(action);
            }
            // 绑定菜单到按钮
            AudioTrackButton.Flyout = menu;
            AudioTrackButton.IsVisible = true;
        }
        else
        {
            AudioTrackButton.IsVisible = false;
            _selectedAudioTrackIndex = 0;
        }
    }

    /// <summary>更新音轨按钮显示文本</summary>
    private void UpdateAudioTrackButtonText(JsonArray audioStreams, int arrayIndex)
    {
        if (arrayIndex >= audioStreams.Count) return;
        var lang = Str(audioStreams[arrayIndex], "language");
        var text = $"{I18n.Tr("音轨", CardCtx)} {arrayIndex + 1}";
        if (lang.Length > 0) text += $" ({lang})";
        AudioTrackButton.Content = text;
    }

    /// <summary>音轨选择事件处理: 数组索引用于 ffmpeg -map 0:a:N</summary>
    private void OnAudioTrackSelected(int arrayIndex, JsonArray audioStreams)
    {
        _selectedAudioTrackIndex = arrayIndex;
        UpdateAudioTrackButtonText(audioStreams, arrayIndex);
    }

    /// <summary>更新视频缩略图</summary>
    private void UpdateThumbnail(string thumbnailPath)
    {
        try
        {
            if (!string.IsNullOrEmpty(thumbnailPath) && File.Exists(thumbnailPath))
            {
                using var fs = File.OpenRead(thumbnailPath);
                VideoThumbnail.Source = new Bitmap(fs);
                return;
            }
        }
        catch { /* fall back to the audio thumbnail */ }
        using var asset = AssetLoader.Open(new Uri("avares://VideoCaptioner/Assets/audio-thumbnail.png"));
        VideoThumbnail.Source = new Bitmap(asset);
    }

    /// <summary>开始转录按钮点击事件</summary>
    private void OnStartButtonClicked()
    {
        ProgressRing.Value = 0;
        ProgressRing.IsVisible = true;
        StartButton.IsEnabled = false;
        StartTranscription();
    }

    /// <summary>打开文件夹按钮点击事件</summary>
    private void OnOpenFolderClicked()
    {
        if (_task is { OutputPath.Length: > 0 })
        {
            var targetDir = File.Exists(_task.OutputPath)
                ? Path.GetDirectoryName(_task.OutputPath)
                : Path.GetDirectoryName(_task.FilePath);
            if (!string.IsNullOrEmpty(targetDir)) Dialogs.OpenUrl(targetDir);
        }
        else
        {
            Toast.Warning(I18n.Tr("警告", CardCtx), I18n.Tr("没有可用的字幕文件夹", CardCtx), DurationWarning);
        }
    }

    /// <summary>开始转录过程</summary>
    private void StartTranscription(bool needCreateTask = true)
    {
        _isProcessing = true;
        StartButton.IsEnabled = false;

        if (needCreateTask)
        {
            var filePath = _videoInfo != null ? Str(_videoInfo, "file_path") : "";
            _task = new TranscribeTask { FilePath = filePath, NeedNextTask = false };
        }

        if (_task == null) return;
        var task = _task;

        // TranscriptThread(task); the backend builds the task with TaskFactory.create_transcribe_task
        var job = Bridge.Instance.Run("transcribe", new
        {
            file_path = task.FilePath,
            need_next_task = task.NeedNextTask,
            task_id = task.TaskId,
            // 将选中的音轨索引传递给 task
            selected_audio_track_index = _selectedAudioTrackIndex,
        });
        _transcriptJob = job;
        job.Event += (name, data) =>
        {
            if (name == "task") task.OutputPath = data?["output_path"]?.GetValue<string>();
        };
        job.Progress += OnTranscriptProgress;
        job.Error += OnTranscriptError;
        job.Finished += data =>
        {
            task.OutputPath = data?["output_path"]?.GetValue<string>() ?? task.OutputPath;
            OnTranscriptFinished(task);
        };
    }

    /// <summary>更新转录进度</summary>
    private void OnTranscriptProgress(int value, string message)
    {
        StartButtonText.Text = I18n.Tr(message, "TranscriptThread");
        ProgressRing.Value = value;
    }

    /// <summary>处理转录错误</summary>
    private void OnTranscriptError(string error)
    {
        _isProcessing = false;
        StartButton.IsEnabled = true;
        StartButtonText.Text = I18n.Tr("重新转录", CardCtx);
        ProgressRing.IsVisible = false;
        Toast.Error(I18n.Tr("转录失败", CardCtx), I18n.Tr(error, CardCtx), DurationError);
    }

    /// <summary>转录完成处理</summary>
    private void OnTranscriptFinished(TranscribeTask task)
    {
        StartButton.IsEnabled = true;
        StartButtonText.Text = I18n.Tr("转录完成", CardCtx);
        ProgressRing.IsVisible = false;
        OnInterfaceTranscriptFinished(task);
    }

    /// <summary>重置UI状态</summary>
    private void CardResetUi()
    {
        StartButton.IsEnabled = true;
        StartButtonText.Text = I18n.Tr("开始转录", CardCtx);
        ProgressRing.Value = 0;
        ProgressRing.IsVisible = false;
    }

    /// <summary>VideoInfoCard.set_task: 设置任务并更新UI</summary>
    private void CardSetTask()
    {
        CardResetUi();
    }

    /// <summary>closeEvent: stop the running transcription (TranscriptThread.terminate).</summary>
    public void Stop()
    {
        _transcriptJob?.Cancel();
        _videoInfoJob?.Cancel();
    }

    private static string Str(JsonNode? n, string key)
    {
        var v = n?[key];
        return v is JsonValue jv && jv.TryGetValue<string>(out var s) ? s : v?.ToString() ?? "";
    }

    private static long Int(JsonNode n, string key)
    {
        var v = n[key];
        if (v is JsonValue jv)
        {
            if (jv.TryGetValue<long>(out var l)) return l;
            if (jv.TryGetValue<double>(out var d)) return (long)d;
        }
        return 0;
    }
}
