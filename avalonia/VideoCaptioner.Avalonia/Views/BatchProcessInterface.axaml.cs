using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using FluentAvalonia.UI.Controls;
using VideoCaptioner.Avalonia.Models;
using VideoCaptioner.Avalonia.Services;

namespace VideoCaptioner.Avalonia.Views;

/// <summary>One row of the task table (QTableWidget row: file name item, ProgressBar cell widget, status item).</summary>
public partial class BatchTaskRow : ObservableObject
{
    public static readonly IBrush WaitingBrush = Brushes.Gray;                                  // Qt.gray
    // Windows 11 success colour for the current theme
    public static IBrush CompletedBrush =>
        global::Avalonia.Application.Current is { } app &&
        app.TryGetResource("SystemFillColorSuccessBrush", app.ActualThemeVariant, out var r) && r is IBrush b
            ? b : new SolidColorBrush(Color.Parse("#0F7B0F"));

    public BatchTaskRow(string filePath)
    {
        FilePath = filePath;
        FileName = Path.GetFileName(filePath);
        Folder = Path.GetDirectoryName(filePath) ?? "";
        var ext = Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant();
        FileSymbol = Options.SupportedSubtitleFormats.Contains(ext) ? Symbol.ClosedCaption
            : Options.SupportedAudioFormats.Contains(ext) ? Symbol.Audio
            : Symbol.Video;
    }

    /// <summary>Column 0 tooltip: the full path (the row key, like item(row, 0).toolTip()).</summary>
    public string FilePath { get; }
    public string FileName { get; }
    /// <summary>Folder shown as the secondary line under the file name.</summary>
    public string Folder { get; }
    public Symbol FileSymbol { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText))]
    private int _progress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWaiting), nameof(IsCompleted), nameof(IsFailed), nameof(IsRunning), nameof(StatusSymbol))]
    private string _status = BatchProcessInterface.StatusWaiting;

    [ObservableProperty] private string? _statusToolTip;
    [ObservableProperty] private IBrush _statusBrush = WaitingBrush;

    public string ProgressText => $"{Progress}%";
    public bool IsWaiting => Status == BatchProcessInterface.StatusWaiting;
    public bool IsCompleted => Status == BatchProcessInterface.StatusCompleted;
    public bool IsFailed => Status == BatchProcessInterface.StatusFailed;
    /// <summary>Any other status text comes from the running BatchProcessThread (处理中 / stage messages).</summary>
    public bool IsRunning => !IsWaiting && !IsCompleted && !IsFailed;
    public Symbol StatusSymbol => IsCompleted ? Symbol.Checkmark : IsFailed ? Symbol.Dismiss : IsRunning ? Symbol.Sync : Symbol.Clock;
}

/// <summary>Port of videocaptioner/ui/view/batch_process_interface.py (BatchProcessInterface).</summary>
public partial class BatchProcessInterface : UserControl, IWindowTitleProvider
{
    private const string Ctx = "BatchProcessInterface";

    // core.entities.BatchTaskType / BatchTaskStatus values (Options is generated from the Python enums)
    private static readonly string TypeTranscribe = Options.BatchTaskType[0];  // 批量转录
    private static readonly string TypeSubtitle = Options.BatchTaskType[1];    // 批量字幕
    private static readonly string TypeTransSub = Options.BatchTaskType[2];    // 转录+字幕
    private static readonly string TypeFullProcess = Options.BatchTaskType[3]; // 全流程处理
    internal static readonly string StatusWaiting = Options.BatchTaskStatus[0];   // 等待中
    internal static readonly string StatusFailed = Options.BatchTaskStatus[3];     // 失败
    internal static readonly string StatusCompleted = Options.BatchTaskStatus[2];  // 已完成

    // core.constant
    private const int InfobarDurationWarning = 5000;
    private const int InfobarDurationInfo = 3000;
    private const int InfobarDurationSuccess = 2000;

    private readonly ObservableCollection<BatchTaskRow> _rows = [];
    private readonly Dictionary<string, string> _taskTypeDescriptions;
    private BridgeJob? _batchThread;  // long-lived "batch_session" job owning the BatchProcessThread
    private bool _suppressTypeChanged;

    public string? WindowTitle { get; } = I18n.Tr("批量处理", Ctx);

    public BatchProcessInterface()
    {
        InitializeComponent();

        _taskTypeDescriptions = new Dictionary<string, string>
        {
            [TypeTranscribe] = I18n.Tr("仅进行语音识别，生成字幕文件", Ctx),
            [TypeSubtitle] = I18n.Tr("对已有字幕进行分割、优化或翻译", Ctx),
            [TypeTransSub] = I18n.Tr("先转录再处理字幕，不合成视频", Ctx),
            [TypeFullProcess] = I18n.Tr("转录 → 字幕处理 → 合成视频", Ctx),
        };

        _suppressTypeChanged = true;
        TaskTypeCombo.ItemsSource = Options.BatchTaskType;
        TaskTypeCombo.SelectedItem = TypeFullProcess;
        _suppressTypeChanged = false;

        TaskList.ItemsSource = _rows;

        AddFileBtn.Click += async (_, _) => await OnAddFileClickedAsync();
        StartAllBtn.Click += (_, _) => StartAllTasks();
        ClearBtn.Click += (_, _) => ClearTasks();
        TaskTypeCombo.SelectionChanged += (_, _) =>
        {
            if (!_suppressTypeChanged) OnTaskTypeChanged(CurrentTaskType);
        };

        TaskList.DoubleTapped += OnTableDoubleClicked;
        TaskList.ContextRequested += ShowContextMenu;
        TaskList.AddHandler(Button.ClickEvent, OnRowActionClick);

        AddHandler(DragDrop.DragEnterEvent, OnDragEnter);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);

        // Empty state: the drop zone doubles as a big "添加文件" button
        EmptyState.Tapped += async (_, _) => await OnAddFileClickedAsync();
        _rows.CollectionChanged += (_, _) => UpdateSummary();
        TaskTypeCombo.SelectionChanged += (_, _) => UpdateTaskTypeHint();
        UpdateTaskTypeHint();
        UpdateSummary();

        EnsureBatchThread();

        // Development hook for screenshots: VC_BATCH_DEMO_FILES="a.mp4;b.srt" fills the list
        // (no backend call); VC_BATCH_DEMO_STATES=1 also shows the running/completed/failed looks.
        if (Environment.GetEnvironmentVariable("VC_BATCH_DEMO_FILES") is { Length: > 0 } demo)
        {
            AddFiles(demo.Split(';', StringSplitOptions.RemoveEmptyEntries));
            if (Environment.GetEnvironmentVariable("VC_BATCH_DEMO_STATES") == "1")
            {
                var paths = _rows.Select(r => r.FilePath).ToList();
                if (paths.Count > 0) OnTaskCompleted(paths[0]);
                if (paths.Count > 0) UpdateTaskProgress(paths[0], 100, StatusCompleted);
                if (paths.Count > 1) UpdateTaskProgress(paths[1], 46, "字幕翻译中");
                if (paths.Count > 2) { UpdateTaskProgress(paths[2], 12, Options.BatchTaskStatus[1]); OnTaskError(paths[2], "demo error"); }
            }
        }
    }

    /// <summary>Caption next to the task type picker and the accepted formats in the empty state.</summary>
    private void UpdateTaskTypeHint()
    {
        var taskType = CurrentTaskType;
        TaskTypeHint.Text = _taskTypeDescriptions.TryGetValue(taskType, out var d) ? d : "";
        var formats = taskType == TypeSubtitle
            ? Options.SupportedSubtitleFormats
            : Options.SupportedVideoFormats.Take(6).Concat(Options.SupportedAudioFormats.Take(4)).ToArray();
        EmptyFormatsHint.Text = string.Join("  ", formats.Select(f => f.ToUpperInvariant()))
                                + (taskType == TypeSubtitle ? "" : "  …");
    }

    /// <summary>File count caption in the command row; switches between the list and the empty drop zone.</summary>
    private void UpdateSummary()
    {
        var total = _rows.Count;
        EmptyState.IsVisible = total == 0;
        ListPanel.IsVisible = total > 0;
        if (total == 0)
        {
            CountLabel.Text = I18n.Tr("尚未添加文件", Ctx);
            return;
        }
        var done = _rows.Count(r => r.IsCompleted);
        CountLabel.Text = done > 0
            ? string.Format(I18n.Tr("{0} 个文件 · {1} 个已完成", Ctx), total, done)
            : string.Format(I18n.Tr("{0} 个文件", Ctx), total);
    }

    private string CurrentTaskType => TaskTypeCombo.SelectedItem as string ?? TypeFullProcess;

    // ---------------------------------------------------------------- batch thread (bridge)

    /// <summary>self.batch_thread = BatchProcessThread(): one long-lived backend job per interface.</summary>
    private BridgeJob EnsureBatchThread()
    {
        if (_batchThread is { IsDone: false }) return _batchThread;
        var job = Bridge.Instance.Run("batch_session");
        job.Event += (name, data) =>
        {
            var path = data?["file_path"]?.GetValue<string>() ?? "";
            switch (name)
            {
                case "task_progress":
                    UpdateTaskProgress(path, data?["progress"]?.GetValue<int>() ?? 0, data?["status"]?.GetValue<string>() ?? "");
                    break;
                case "task_error":
                    OnTaskError(path, data?["error"]?.GetValue<string>() ?? "");
                    break;
                case "task_completed":
                    OnTaskCompleted(path);
                    break;
            }
        };
        _batchThread = job;
        return job;
    }

    private void AddBatchTask(string filePath, string taskType)
    {
        var session = EnsureBatchThread();
        _ = SendAsync("batch_add", new { session = session.Id, file_path = filePath, task_type = taskType });
    }

    private static async Task SendAsync(string cmd, object args)
    {
        try
        {
            await Bridge.Instance.RequestAsync(cmd, args);
        }
        catch (Exception e)
        {
            Toast.Error(I18n.Tr("错误"), e.Message);
        }
    }

    // ---------------------------------------------------------------- files

    private async Task OnAddFileClickedAsync()
    {
        var taskType = CurrentTaskType;
        (string, string[])[] filters = [];
        if (taskType == TypeTranscribe || taskType == TypeTransSub || taskType == TypeFullProcess)
        {
            var formats = Options.SupportedAudioFormats.Concat(Options.SupportedVideoFormats).Select(f => $"*.{f}").ToArray();
            filters = [("音视频文件", formats)];
        }
        else if (taskType == TypeSubtitle)
        {
            var formats = Options.SupportedSubtitleFormats.Select(f => $"*.{f}").ToArray();
            filters = [("字幕文件", formats)];
        }

        var files = await Dialogs.OpenFilesAsync("选择文件", true, filters);
        if (files.Count > 0) AddFiles(files);
    }

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        if (!e.Data.Contains(DataFormats.Files)) return;
        DropZone.Classes.Set("dragOver", true);
        DropOverlay.IsVisible = _rows.Count > 0;
    }

    private void OnDragLeave(object? sender, DragEventArgs e) => EndDragVisual();

    private void EndDragVisual()
    {
        DropZone.Classes.Set("dragOver", false);
        DropOverlay.IsVisible = false;
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.Data.Contains(DataFormats.Files) ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        EndDragVisual();
        var files = e.Data.GetFiles()?.Select(f => f.TryGetLocalPath()).Where(p => p != null).Cast<string>().ToList() ?? [];
        AddFiles(files);
    }

    public void AddFiles(IEnumerable<string> filePaths)
    {
        var taskType = CurrentTaskType;

        // 展开文件夹为其中的文件（最多 3 层深度）
        const int maxDepth = 3;
        var expanded = new List<string>();
        foreach (var path in filePaths)
        {
            if (Directory.Exists(path)) Walk(path, 0, maxDepth, expanded);
            else expanded.Add(path);
        }

        // 检查文件是否存在并收集不存在的文件
        var nonExistentFiles = new List<string>();
        var validFiles = new List<string>();
        foreach (var filePath in expanded)
        {
            if (!File.Exists(filePath) && !Directory.Exists(filePath))
                nonExistentFiles.Add(Path.GetFileName(filePath));
            else
                validFiles.Add(filePath);
        }

        if (nonExistentFiles.Count > 0)
            Toast.Warning("文件不存在", $"以下文件不存在：\n{string.Join(", ", nonExistentFiles)}",
                InfobarDurationWarning, ToastPosition.Top);

        if (validFiles.Count == 0) return;

        // 对有效文件按文件名排序
        validFiles = validFiles.OrderBy(f => Path.GetFileName(f).ToLowerInvariant(), StringComparer.Ordinal).ToList();

        // 如果表格为空，自动检测文件类型并设置任务类型
        if (_rows.Count == 0 && TaskTypeCombo.SelectedIndex == 0)
        {
            var firstFile = validFiles[0].ToLowerInvariant();
            var isSubtitle = Options.SupportedSubtitleFormats.Any(fmt => firstFile.EndsWith($".{fmt}", StringComparison.Ordinal));
            if (isSubtitle)
            {
                TaskTypeCombo.SelectedItem = TypeSubtitle;
                taskType = TypeSubtitle;
            }
        }

        // 过滤文件类型
        validFiles = FilterFiles(validFiles, taskType);
        if (validFiles.Count == 0)
        {
            Toast.Warning("无效文件", "请选择正确的文件类型", InfobarDurationWarning, ToastPosition.Top);
            return;
        }

        foreach (var filePath in validFiles)
        {
            // 检查是否已存在相同任务
            if (_rows.Any(r => r.FilePath == filePath))
            {
                Toast.Warning("任务已存在", "任务已存在", InfobarDurationWarning, ToastPosition.TopRight);
                continue;
            }
            _rows.Add(new BatchTaskRow(filePath));  // add_task_to_table
        }
    }

    /// <summary>os.walk with the depth cut-off of add_files: files at depth 0..maxDepth-1.</summary>
    private static void Walk(string dir, int depth, int maxDepth, List<string> output)
    {
        if (depth >= maxDepth) return;
        try
        {
            output.AddRange(Directory.EnumerateFiles(dir));
            foreach (var sub in Directory.EnumerateDirectories(dir))
                Walk(sub, depth + 1, maxDepth, output);
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }
    }

    private static List<string> FilterFiles(IEnumerable<string> filePaths, string taskType)
    {
        IEnumerable<string> validExtensions = [];
        if (taskType == TypeTranscribe || taskType == TypeTransSub || taskType == TypeFullProcess)
            validExtensions = Options.SupportedAudioFormats.Concat(Options.SupportedVideoFormats).Select(f => $".{f}");
        else if (taskType == TypeSubtitle)
            validExtensions = Options.SupportedSubtitleFormats.Select(f => $".{f}");
        var exts = validExtensions.ToList();
        return filePaths.Where(f => exts.Any(ext => f.ToLowerInvariant().EndsWith(ext, StringComparison.Ordinal))).ToList();
    }

    // ---------------------------------------------------------------- table interaction

    private static BatchTaskRow? RowFromSource(object? source) =>
        (source as Control)?.DataContext as BatchTaskRow;

    private void ShowContextMenu(object? sender, ContextRequestedEventArgs e)
    {
        var row = RowFromSource(e.Source);
        if (row == null) return;
        e.Handled = true;
        OpenRowMenu(row, e.Source as Control ?? TaskList);
    }

    /// <summary>Row action buttons: 开始 / 打开输出文件夹 / 更多 (the same menu as the right-click menu).</summary>
    private void OnRowActionClick(object? sender, RoutedEventArgs e)
    {
        if (e.Source is not Button { Tag: string action } button || button.DataContext is not BatchTaskRow row) return;
        e.Handled = true;
        switch (action)
        {
            case "start":
                if (row.Status == StatusWaiting) StartTask(row.FilePath);
                break;
            case "folder":
                OpenOutputFolder(row.FilePath);
                break;
            case "more":
                OpenRowMenu(row, button);
                break;
        }
    }

    private void OpenRowMenu(BatchTaskRow row, Control target)
    {
        var filePath = row.FilePath;

        var startAction = new MenuItem { Header = "开始", Icon = new SymbolIcon { Symbol = Symbol.Play } };
        startAction.Click += (_, _) => StartTask(filePath);
        var cancelAction = new MenuItem { Header = "取消", Icon = new SymbolIcon { Symbol = Symbol.Dismiss } };
        cancelAction.Click += (_, _) => CancelTask(filePath);
        var openFolderAction = new MenuItem { Header = "打开输出文件夹", Icon = new SymbolIcon { Symbol = Symbol.Folder } };
        openFolderAction.Click += (_, _) => OpenOutputFolder(filePath);

        if (row.Status != StatusWaiting) startAction.IsEnabled = false;

        var menu = new ContextMenu
        {
            ItemsSource = new Control[] { startAction, cancelAction, new Separator(), openFolderAction },
        };
        menu.Open(target);
    }

    private void OnTableDoubleClicked(object? sender, TappedEventArgs e)
    {
        var row = RowFromSource(e.Source);
        if (row != null) OpenOutputFolder(row.FilePath);
    }

    private void OpenOutputFolder(string filePath)
    {
        // 全流程和其他任务的输出都在文件同目录下
        var outputDir = Path.GetDirectoryName(filePath) ?? "";
        Dialogs.OpenUrl(outputDir);  // platform_utils.open_folder -> os.startfile
    }

    // ---------------------------------------------------------------- batch thread signals

    private BatchTaskRow? FindRow(string filePath) => _rows.FirstOrDefault(r => r.FilePath == filePath);

    private void UpdateTaskProgress(string filePath, int progress, string status)
    {
        var row = FindRow(filePath);
        if (row == null) return;
        row.Progress = progress;
        row.Status = status;
        UpdateSummary();
    }

    private void OnTaskError(string filePath, string error)
    {
        var row = FindRow(filePath);
        if (row == null) return;
        row.Status = StatusFailed;
        row.StatusToolTip = error;
        UpdateSummary();
    }

    private void OnTaskCompleted(string filePath)
    {
        var row = FindRow(filePath);
        if (row == null) return;
        row.Status = StatusCompleted;
        row.StatusBrush = BatchTaskRow.CompletedBrush;
        UpdateSummary();
    }

    // ---------------------------------------------------------------- actions

    private void StartAllTasks()
    {
        if (_rows.Count == 0)
        {
            Toast.Warning("无任务", "请先添加需要处理的文件", InfobarDurationWarning, ToastPosition.Top);
            return;
        }

        var waitingTasks = _rows.Count(r => r.Status == StatusWaiting);
        if (waitingTasks == 0)
        {
            Toast.Warning("无待处理任务", "所有任务已经在处理或已完成", InfobarDurationWarning, ToastPosition.Top);
            return;
        }

        Toast.Success(I18n.Tr("开始处理", Ctx), $"开始处理 {waitingTasks} 个任务", InfobarDurationSuccess, ToastPosition.Top);
        foreach (var row in _rows.ToList())
        {
            if (row.Status == StatusWaiting)
                AddBatchTask(row.FilePath, CurrentTaskType);
        }
    }

    private void StartTask(string filePath)
    {
        var fileName = Path.GetFileName(filePath);
        Toast.Success(I18n.Tr("开始处理", Ctx), $"开始处理文件：{fileName}", InfobarDurationSuccess, ToastPosition.Top);
        AddBatchTask(filePath, CurrentTaskType);
    }

    private void CancelTask(string filePath)
    {
        if (_batchThread is { IsDone: false } session)
            _ = SendAsync("batch_stop", new { session = session.Id, file_path = filePath });
        var row = FindRow(filePath);
        if (row != null) _rows.Remove(row);
    }

    private void ClearTasks()
    {
        if (_batchThread is { IsDone: false } session)
            _ = SendAsync("batch_stop_all", new { session = session.Id });
        _rows.Clear();
    }

    private void OnTaskTypeChanged(string taskType)
    {
        if (_taskTypeDescriptions.TryGetValue(taskType, out var description) && description.Length > 0)
            Toast.Info(taskType, description, InfobarDurationInfo, ToastPosition.Bottom);
        ClearTasks();
    }
}
