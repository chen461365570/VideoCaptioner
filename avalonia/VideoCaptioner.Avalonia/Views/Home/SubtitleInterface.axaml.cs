using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using FluentAvalonia.UI.Controls;
using VideoCaptioner.Avalonia.Models;
using VideoCaptioner.Avalonia.Services;
using VideoCaptioner.Avalonia.Views.Components;

namespace VideoCaptioner.Avalonia.Views.Home;

/// <summary>One row of SubtitleTableModel (an ASRData.to_json() entry).</summary>
public sealed class SubtitleRow : INotifyPropertyChanged
{
    private string _original = "";
    private string _translated = "";

    public long StartTime { get; init; }
    public long EndTime { get; init; }

    /// <summary>QTime(0,0).addMSecs(ms).toString("hh:mm:ss.zzz")[:-2]</summary>
    public string StartText => FormatTime(StartTime);
    public string EndText => FormatTime(EndTime);

    public string Original
    {
        get => _original;
        set { if (_original != value) { _original = value; OnChanged(); } }
    }

    public string Translated
    {
        get => _translated;
        set { if (_translated != value) { _translated = value; OnChanged(); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private static string FormatTime(long ms)
    {
        // QTime wraps around at 24h
        var t = TimeSpan.FromMilliseconds(((ms % 86400000) + 86400000) % 86400000);
        return $"{t.Hours:00}:{t.Minutes:00}:{t.Seconds:00}.{t.Milliseconds / 100}";
    }

    public static SubtitleRow FromJson(JsonNode? n) => new()
    {
        StartTime = ToLong(n?["start_time"]),
        EndTime = ToLong(n?["end_time"]),
        Original = n?["original_subtitle"]?.ToString() ?? "",
        Translated = n?["translated_subtitle"]?.ToString() ?? "",
    };

    public JsonObject ToJson() => new()
    {
        ["start_time"] = StartTime,
        ["end_time"] = EndTime,
        ["original_subtitle"] = Original,
        ["translated_subtitle"] = Translated,
    };

    private static long ToLong(JsonNode? n)
    {
        if (n is not JsonValue v) return 0;
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<double>(out var d)) return (long)d;
        return long.TryParse(v.ToString(), out l) ? l : 0;
    }
}

/// <summary>Port of videocaptioner/ui/view/subtitle_interface.py (SubtitleInterface + SubtitleTableModel).</summary>
public partial class SubtitleInterface : UserControl
{
    private const string Ctx = "SubtitleInterface";
    private const int DurationError = 10000;
    private const int DurationWarning = 5000;
    private const int DurationInfo = 3000;
    private const int DurationSuccess = 2000;

    // OutputSubtitleFormatEnum / SupportedSubtitleFormats (videocaptioner.core.entities)
    private static readonly string[] OutputSubtitleFormats = ["srt", "ass", "vtt", "json", "txt"];
    private static readonly string[] SupportedSubtitleFormats = ["srt", "ass", "vtt"];

    /// <summary>(video path, subtitle output path)</summary>
    public event Action<string, string>? Finished;

    private ObservableCollection<SubtitleRow> _rows = [];
    private string? _subtitlePath;
    private string _customPromptText;

    /// <summary>Arguments used to (re)create the SubtitleTask in the backend (set_task).</summary>
    private JsonObject? _taskArgs;
    /// <summary>SubtitleTask as reported by the backend (subtitle_path, output_path, need_next_task, ...).</summary>
    private JsonNode? _task;
    private BridgeJob? _subtitleJob;
    private BridgeJob? _retranslateJob;

    public SubtitleInterface()
    {
        InitializeComponent();
        _customPromptText = Cfg.CustomPromptText.Value;
        InitUi();
        SetupSignals();
        UpdatePromptButtonStyle();
        SetValues();
    }

    private static string Tr(string s) => I18n.Tr(s, Ctx);

    // ------------------------------------------------------------------ UI
    private void InitUi()
    {
        SaveText.Text = Tr("保存");
        foreach (var format in OutputSubtitleFormats)
        {
            var item = new MenuItem { Header = format };
            var f = format;
            item.Click += (_, _) => OnSaveFormatClicked(f);
            ((MenuFlyout)SaveButton.Flyout!).Items.Add(item);
        }

        LayoutText.Text = Tr("字幕排布");
        foreach (var layout in new[] { "译文在上", "原文在上", "仅译文", "仅原文" })
        {
            var item = new MenuItem { Header = layout };
            var l = layout;
            item.Click += (_, _) => SignalBus.EmitSubtitleLayoutChanged(l);
            ((MenuFlyout)LayoutButton.Flyout!).Items.Add(item);
        }

        OptimizeText.Text = Tr("字幕校正");
        // ToggleSwitch: IsCheckedChanged also covers dragging the knob (the handlers are idempotent)
        OptimizeButton.IsCheckedChanged += (_, _) => OnSubtitleOptimizationChanged(OptimizeButton.IsChecked == true);
        TranslateText.Text = Tr("字幕翻译");
        TranslateButton.IsCheckedChanged += (_, _) => OnSubtitleTranslationChanged(TranslateButton.IsChecked == true);

        TargetLanguageText.Text = Tr("翻译语言");
        foreach (var lang in Options.TargetLanguage)
        {
            var item = new MenuItem { Header = lang };
            var l = lang;
            item.Click += (_, _) => SignalBus.EmitTargetLanguageChanged(l);
            ((MenuFlyout)TargetLanguageButton.Flyout!).Items.Add(item);
        }

        PromptText.Text = Tr("Prompt");
        PromptButton.Click += (_, _) => ShowPromptDialog();
        SettingButton.Click += (_, _) => ShowSubtitleSettings();
        OpenFolderButton.Click += (_, _) => OnOpenFolderClicked();
        FileSelectButton.Click += (_, _) => OnFileSelect();
        EmptySelectButton.Click += (_, _) => OnFileSelect();
        EmptyHintText.Text = Tr("支持 SRT、ASS、VTT 格式，加载后可以校正、翻译并逐行编辑字幕");

        StartText.Text = Tr("开始");
        StartButton.Click += (_, _) => StartSubtitleOptimization(needCreateTask: true);

        // table
        UpdateTranslatedHeader();
        SubtitleTable.ItemsSource = _rows;
        SubtitleTable.LoadingRow += (_, e) => e.Row.Header = (e.Row.GetIndex() + 1).ToString(); // 显示行号
        SubtitleTable.CellPointerPressed += OnCellPointerPressed;
        SubtitleTable.ContextRequested += ShowContextMenu;

        // bottom
        StatusLabel.Text = Tr("请拖入字幕文件");
        CancelText.Text = Tr("取消");
        CancelButton.Click += (_, _) => CancelOptimization();

        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, (_, _) => DropZone.Classes.Remove("dragOver"));
        AddHandler(DragDrop.DropEvent, OnDrop);
        KeyDown += OnKeyDown;
        UpdateTableState();

        // Development hook for screenshots: VC_DEV_SUBTITLE=<path> loads a subtitle file at startup
        if (Environment.GetEnvironmentVariable("VC_DEV_SUBTITLE") is { Length: > 0 } devSubtitle && File.Exists(devSubtitle))
            AttachedToVisualTree += (_, _) => { if (_subtitlePath == null) _ = LoadSubtitleFile(devSubtitle); };
    }

    /// <summary>Shows the table card when rows are loaded, otherwise the empty-state drop zone.</summary>
    private void UpdateTableState()
    {
        var hasRows = _rows.Count > 0;
        TableCard.IsVisible = hasRows;
        EmptyState.IsVisible = !hasRows;
        FileNameText.Text = string.IsNullOrEmpty(_subtitlePath) ? "" : Path.GetFileName(_subtitlePath);
        FilePathText.Text = string.IsNullOrEmpty(_subtitlePath) ? "" : Path.GetDirectoryName(_subtitlePath);
        RowCountText.Text = string.Format(Tr("{0} 条字幕"), _rows.Count);
    }

    private void SetValues()
    {
        LayoutText.Text = Cfg.SubtitleLayout.Value;
        TranslateButton.IsChecked = Cfg.NeedTranslate.Value;
        OptimizeButton.IsChecked = Cfg.NeedOptimize.Value;
        TargetLanguageText.Text = Cfg.TargetLanguage.Value;
        TargetLanguageButton.IsEnabled = Cfg.NeedTranslate.Value;
    }

    private void SetupSignals()
    {
        SignalBus.SubtitleLayoutChanged += OnSubtitleLayoutChanged;
        SignalBus.TargetLanguageChanged += OnTargetLanguageChanged;
        SignalBus.SubtitleOptimizationChanged += OnSubtitleOptimizationChanged;
        SignalBus.SubtitleTranslationChanged += OnSubtitleTranslationChanged;
        // headerData() reads cfg.need_translate on every repaint
        Cfg.NeedTranslate.ValueChanged += _ => UpdateTranslatedHeader();
    }

    private void UpdateTranslatedHeader() =>
        SubtitleTable.Columns[3].Header = Cfg.NeedTranslate.Value
            ? I18n.Tr("翻译字幕", "SubtitleTableModel")
            : I18n.Tr("优化字幕", "SubtitleTableModel");

    private async void ShowPromptDialog()
    {
        if (await PromptDialog.ShowAsync())
        {
            _customPromptText = Cfg.CustomPromptText.Value;
            UpdatePromptButtonStyle();
        }
    }

    private void UpdatePromptButtonStyle()
    {
        if (_customPromptText.Trim().Length > 0)
            PromptIcon.Foreground = new SolidColorBrush(Color.FromRgb(76, 255, 165));
        else
            PromptIcon.ClearValue(ForegroundProperty);
    }

    // ------------------------------------------------------------------ task / process
    /// <summary>set_task(TaskFactory.create_subtitle_task(subtitlePath, videoPath, needNextTask, taskId)).</summary>
    public void SetTask(string subtitlePath, string? videoPath, bool needNextTask, string? taskId)
    {
        StopSubtitleJob();
        StartButton.IsEnabled = true;
        _taskArgs = new JsonObject
        {
            ["file_path"] = subtitlePath,
            ["video_path"] = videoPath,
            ["need_next_task"] = needNextTask,
            ["task_id"] = taskId,
        };
        _task = new JsonObject
        {
            ["subtitle_path"] = subtitlePath,
            ["video_path"] = videoPath,
            ["need_next_task"] = needNextTask,
            ["task_id"] = taskId,
        };
        _subtitlePath = subtitlePath;
        _ = FetchTaskAsync(_taskArgs.DeepClone());
        UpdateInfo();
    }

    private async Task FetchTaskAsync(JsonNode args)
    {
        try
        {
            var task = await Bridge.Instance.RequestAsync("subtitle_task", args);
            if (task != null && task["subtitle_path"]?.ToString() == _subtitlePath) _task = task;
        }
        catch { /* the task is re-created by subtitle_run anyway */ }
    }

    /// <summary>更新页面信息</summary>
    private void UpdateInfo()
    {
        if (_task == null || _subtitlePath == null) return;
        _ = LoadIntoTableAsync(_subtitlePath);
    }

    /// <summary>主处理函数</summary>
    public void Process() => StartSubtitleOptimization(needCreateTask: false);

    private bool IsProcessing() =>
        _subtitleJob is { IsDone: false } || _retranslateJob is { IsDone: false };

    private void StartSubtitleOptimization(bool needCreateTask)
    {
        if (IsProcessing()) return;
        if (string.IsNullOrEmpty(_subtitlePath))
        {
            Toast.Warning(Tr("警告"), Tr("请先加载字幕文件"), DurationWarning);
            return;
        }
        StartButton.IsEnabled = false;
        ProgressResume();
        ProgressBar.Value = 0;
        CancelButton.IsVisible = true;

        JsonObject args;
        if (needCreateTask)
        {
            args = new JsonObject { ["file_path"] = _subtitlePath };
            // 将当前表格状态写回原文件，保留用户的合并/删除/编辑
            if (_rows.Count > 0) args["data"] = RowsToJson(_rows);
        }
        else
        {
            if (_taskArgs == null)
            {
                StartButton.IsEnabled = true;
                CancelButton.IsVisible = false;
                return;
            }
            args = (JsonObject)_taskArgs.DeepClone();
        }
        args["custom_prompt_text"] = _customPromptText;

        var job = Bridge.Instance.Run("subtitle_run", args);
        _subtitleJob = job;
        job.Event += (name, data) =>
        {
            if (job != _subtitleJob) return;
            switch (name)
            {
                case "task":
                    _task = data;
                    if (needCreateTask) _taskArgs = new JsonObject { ["file_path"] = _subtitlePath };
                    break;
                case "update":
                    UpdateData(data as JsonObject);
                    break;
                case "update_all":
                    UpdateAll(data as JsonObject);
                    break;
            }
        };
        job.Progress += (v, msg) => { if (job == _subtitleJob) OnSubtitleOptimizationProgress(v, msg); };
        job.Finished += data =>
        {
            if (job != _subtitleJob) return;
            OnSubtitleOptimizationFinished(data?["video_path"]?.ToString() ?? "", data?["output_path"]?.ToString() ?? "");
        };
        job.Error += err => { if (job == _subtitleJob) OnSubtitleOptimizationError(err); };

        Toast.Info(Tr("开始优化"), Tr("开始优化字幕"), DurationInfo);
    }

    private void OnSubtitleOptimizationFinished(string videoPath, string outputPath)
    {
        StartButton.IsEnabled = true;
        CancelButton.IsVisible = false;
        ProgressBar.Value = 100;
        if (_task?["need_next_task"]?.GetValue<bool>() == true)
            Finished?.Invoke(videoPath, outputPath);
        Toast.Success(Tr("优化完成"), Tr("优化完成字幕..."), DurationSuccess, ToastPosition.Bottom);
    }

    private void OnSubtitleOptimizationError(string error)
    {
        StartButton.IsEnabled = true;
        CancelButton.IsVisible = false; // 隐藏取消按钮
        ProgressError();
        // SubtitleThread emits progress(100, "字幕处理失败") right after error
        OnSubtitleOptimizationProgress(100, I18n.Tr("字幕处理失败", "SubtitleThread"));
        Toast.Error(Tr("优化失败"), Tr(error), DurationError);
    }

    private void OnSubtitleOptimizationProgress(int value, string status)
    {
        ProgressBar.Value = value;
        StatusLabel.Text = status;
    }

    private void StopSubtitleJob()
    {
        if (_subtitleJob is { IsDone: false } job) job.Cancel();
    }

    /// <summary>取消字幕校正</summary>
    private void CancelOptimization()
    {
        if (_subtitleJob == null) return;
        StopSubtitleJob();
        _subtitleJob = null;
        StartButton.IsEnabled = true;
        CancelButton.IsVisible = false;
        ProgressResume(); // 恢复正常状态
        ProgressBar.Value = 0;
        StatusLabel.Text = Tr("已取消校正");
        Toast.Warning(Tr("已取消"), Tr("字幕校正已取消"), DurationWarning);
    }

    private void ProgressResume() => ProgressBar.Classes.Remove("error");
    private void ProgressError() => ProgressBar.Classes.Add("error");

    // ------------------------------------------------------------------ model helpers
    private static JsonObject RowsToJson(IEnumerable<SubtitleRow> rows)
    {
        var o = new JsonObject();
        var i = 1;
        foreach (var r in rows) o[(i++).ToString()] = r.ToJson();
        return o;
    }

    private void SetRows(IEnumerable<SubtitleRow> rows)
    {
        _rows = new ObservableCollection<SubtitleRow>(rows);
        SubtitleTable.ItemsSource = _rows;
        UpdateTableState();
    }

    /// <summary>SubtitleTableModel.update_data: {key: translated_text}</summary>
    private void UpdateData(JsonObject? newData)
    {
        if (newData == null) return;
        foreach (var (key, value) in newData)
        {
            if (int.TryParse(key, out var k) && k >= 1 && k <= _rows.Count)
                _rows[k - 1].Translated = value?.ToString() ?? "";
        }
    }

    /// <summary>SubtitleTableModel.update_all</summary>
    private void UpdateAll(JsonObject? data)
    {
        if (data == null) return;
        SetRows(data.OrderBy(kv => int.TryParse(kv.Key, out var k) ? k : int.MaxValue)
            .Select(kv => SubtitleRow.FromJson(kv.Value)));
    }

    private async Task<bool> LoadIntoTableAsync(string filePath)
    {
        try
        {
            var res = await Bridge.Instance.RequestAsync("subtitle_load", new JsonObject { ["path"] = filePath });
            UpdateAll(res?["data"] as JsonObject);
            StatusLabel.Text = Tr("已加载文件");
            return true;
        }
        catch (Exception e)
        {
            Toast.Error(Tr("格式错误"), e.Message, DurationError);
            return false;
        }
    }

    private Task<bool> LoadSubtitleFile(string filePath)
    {
        _subtitlePath = filePath;
        return LoadIntoTableAsync(filePath);
    }

    // ------------------------------------------------------------------ toolbar actions
    private async void OnFileSelect()
    {
        var patterns = SupportedSubtitleFormats.Select(f => $"*.{f}").ToArray();
        var filePath = await Dialogs.OpenFileAsync(Tr("选择字幕文件"), (Tr("字幕文件"), patterns));
        if (!string.IsNullOrEmpty(filePath))
        {
            _subtitlePath = filePath;
            await LoadSubtitleFile(filePath);
        }
    }

    /// <summary>处理保存格式的选择</summary>
    private async void OnSaveFormatClicked(string format)
    {
        if (string.IsNullOrEmpty(_subtitlePath))
        {
            Toast.Warning(Tr("警告"), Tr("请先加载字幕文件"), DurationWarning);
            return;
        }

        // 获取保存路径
        var defaultName = Path.GetFileNameWithoutExtension(_subtitlePath);
        var filePath = await Dialogs.SaveFileAsync(Tr("保存字幕文件"), $"{defaultName}.{format}",
            (Tr("字幕文件"), [$"*.{format}"]));
        if (string.IsNullOrEmpty(filePath)) return;
        if (Path.GetExtension(filePath).Length == 0) filePath += "." + format;

        try
        {
            // 转换并保存字幕
            await Bridge.Instance.RequestAsync("subtitle_save", new JsonObject
            {
                ["data"] = RowsToJson(_rows),
                ["path"] = filePath,
            });
            Toast.Success(Tr("保存成功"), Tr("字幕已保存至:") + filePath, DurationSuccess);
        }
        catch (Exception e)
        {
            Toast.Error(Tr("保存失败"), Tr("保存字幕文件失败: ") + e.Message, DurationError);
        }
    }

    /// <summary>打开文件夹按钮点击事件</summary>
    private void OnOpenFolderClicked()
    {
        if (_task == null)
        {
            Toast.Warning(Tr("警告"), Tr("请先加载字幕文件"), DurationWarning);
            return;
        }
        var subtitlePath = _task["subtitle_path"]?.ToString() ?? _subtitlePath ?? "";
        var outputPath = _task["output_path"]?.ToString();
        string? targetDir;
        if (!string.IsNullOrEmpty(outputPath))
            targetDir = File.Exists(outputPath) || Directory.Exists(outputPath)
                ? Path.GetDirectoryName(outputPath)
                : Path.GetDirectoryName(subtitlePath);
        else
            targetDir = Path.GetDirectoryName(subtitlePath);
        _ = Bridge.Instance.RequestAsync("subtitle_open_folder", new JsonObject { ["path"] = targetDir ?? "" });
    }

    private async void ShowSubtitleSettings() => await SubtitleSettingDialog.ShowAsync();

    // ------------------------------------------------------------------ config handlers
    /// <summary>处理翻译语言变更</summary>
    private void OnTargetLanguageChanged(string language)
    {
        if (!Options.TargetLanguage.Contains(language)) return;
        TargetLanguageText.Text = language;
        Cfg.TargetLanguage.Value = language;
    }

    /// <summary>处理字幕优化开关变更</summary>
    private void OnSubtitleOptimizationChanged(bool isChecked)
    {
        Cfg.NeedOptimize.Value = isChecked;
        OptimizeButton.IsChecked = isChecked;
    }

    /// <summary>处理字幕翻译开关变更</summary>
    private void OnSubtitleTranslationChanged(bool isChecked)
    {
        Cfg.NeedTranslate.Value = isChecked;
        TranslateButton.IsChecked = isChecked;
        // 控制翻译语言选择按钮的启用状态
        TargetLanguageButton.IsEnabled = isChecked;
    }

    /// <summary>处理字幕排布变更</summary>
    private void OnSubtitleLayoutChanged(string layout)
    {
        Cfg.SubtitleLayout.Value = layout;
        LayoutText.Text = layout;
    }

    // ------------------------------------------------------------------ drag & drop
    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var hasFiles = e.Data.Contains(DataFormats.Files);
        e.DragEffects = hasFiles ? DragDropEffects.Copy : DragDropEffects.None;
        if (hasFiles) DropZone.Classes.Add("dragOver");
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        DropZone.Classes.Remove("dragOver");
        var files = e.Data.GetFiles()?.Select(f => f.TryGetLocalPath()).Where(p => p != null).Cast<string>().ToList() ?? [];
        foreach (var filePath in files)
        {
            if (!File.Exists(filePath)) continue;

            var fileExt = Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant();
            // 检查文件格式是否支持
            if (SupportedSubtitleFormats.Contains(fileExt))
            {
                if (await LoadSubtitleFile(filePath))
                    Toast.Success(Tr("导入成功"), Tr("成功导入") + Path.GetFileName(filePath), DurationSuccess, ToastPosition.Bottom);
                break;
            }
            Toast.Error(Tr("格式错误") + fileExt,
                Tr("支持的字幕格式:") + "{" + string.Join(", ", SupportedSubtitleFormats.Select(f => $"'{f}'")) + "}",
                DurationError);
        }
    }

    // ------------------------------------------------------------------ table interaction
    private List<int> SelectedRows() =>
        SubtitleTable.SelectedItems.OfType<SubtitleRow>().Select(r => _rows.IndexOf(r)).Where(i => i >= 0)
            .Distinct().OrderBy(i => i).ToList();

    private void OnCellPointerPressed(object? sender, DataGridCellPointerPressedEventArgs e)
    {
        var point = e.PointerPressedEventArgs.GetCurrentPoint(SubtitleTable);
        var rowIndex = e.Row.GetIndex();
        if (rowIndex < 0 || rowIndex >= _rows.Count) return;

        if (point.Properties.IsRightButtonPressed)
        {
            // QTableView selects the clicked cell on right press unless it is already selected
            if (!SubtitleTable.SelectedItems.Contains(_rows[rowIndex]))
                SubtitleTable.SelectedItem = _rows[rowIndex];
            return;
        }
        if (!point.Properties.IsLeftButtonPressed) return;

        // on_subtitle_clicked
        var item = _rows[rowIndex];
        var startTime = item.StartTime; // 毫秒
        var endTime = item.EndTime - 50 > startTime ? item.EndTime - 50 : item.EndTime;
        SignalBus.PlayVideoSegment((int)startTime, (int)endTime);
    }

    /// <summary>显示右键菜单</summary>
    private void ShowContextMenu(object? sender, ContextRequestedEventArgs e)
    {
        e.Handled = true;
        var rows = SelectedRows();
        if (rows.Count == 0) return;

        var menu = new MenuFlyout();
        var mergeAction = new MenuItem
        {
            Header = Tr("合并"),
            Icon = new SymbolIcon { Symbol = Symbol.Link },
            InputGesture = new KeyGesture(Key.M, KeyModifiers.Control),
            IsEnabled = rows.Count > 1,
        };
        var deleteAction = new MenuItem
        {
            Header = Tr("删除"),
            Icon = new SymbolIcon { Symbol = Symbol.Delete },
            InputGesture = new KeyGesture(Key.Delete),
        };
        var retranslateAction = new MenuItem
        {
            Header = Tr("重新翻译"),
            Icon = new SymbolIcon { Symbol = Symbol.Sync },
            InputGesture = new KeyGesture(Key.T, KeyModifiers.Control),
            IsEnabled = Cfg.NeedTranslate.Value && !IsProcessing(),
        };
        mergeAction.Click += (_, _) => MergeSelectedRows(rows);
        deleteAction.Click += (_, _) => DeleteSelectedRows(rows);
        retranslateAction.Click += (_, _) => RetranslateSelectedRows(rows);
        menu.Items.Add(mergeAction);
        menu.Items.Add(deleteAction);
        menu.Items.Add(retranslateAction);

        // 显示菜单
        menu.ShowAt(SubtitleTable, true);
    }

    /// <summary>合并选中的字幕行</summary>
    private void MergeSelectedRows(List<int> rows)
    {
        if (rows.Count < 2) return;

        var first = _rows[rows[0]];
        var last = _rows[rows[^1]];
        // 合并字幕内容
        var merged = new SubtitleRow
        {
            StartTime = first.StartTime,
            EndTime = last.EndTime,
            Original = string.Join(" ", rows.Select(r => _rows[r].Original)),
            Translated = string.Join(" ", rows.Select(r => _rows[r].Translated)),
        };

        // 保留 rows[0] 之前与 rows[-1] 之后的行，合并项放在 rows[0] 处
        var preserved = _rows.Take(rows[0]).Concat(_rows.Skip(rows[^1] + 1)).ToList();
        var newData = new List<SubtitleRow>();
        for (var i = 0; i < preserved.Count; i++)
        {
            if (i == rows[0]) newData.Add(merged);
            newData.Add(preserved[i]);
        }
        // 如果合并的是最后几行，需要确保合并项被添加
        if (rows[0] >= preserved.Count) newData.Add(merged);

        SubtitleTable.SelectedItems.Clear();
        SetRows(newData);

        Toast.Success(Tr("合并成功"), Tr("已成功合并选中的字幕行"), DurationSuccess);
    }

    /// <summary>删除选中的字幕行</summary>
    private void DeleteSelectedRows(List<int> rows)
    {
        if (rows.Count == 0) return;
        var set = rows.ToHashSet();
        var newData = _rows.Where((_, i) => !set.Contains(i)).ToList();
        SubtitleTable.SelectedItems.Clear();
        SetRows(newData);
    }

    /// <summary>重新翻译选中的字幕行</summary>
    private void RetranslateSelectedRows(List<int> rows)
    {
        if (rows.Count == 0 || _rows.Count == 0) return;
        if (IsProcessing()) return;

        // 提取选中行数据，保留原始键名（行号字符串）
        var selected = new JsonObject();
        foreach (var row in rows) selected[(row + 1).ToString()] = _rows[row].ToJson();

        StartButton.IsEnabled = false;
        StatusLabel.Text = Tr("正在重新翻译...");
        ProgressResume();
        ProgressBar.Value = 0;

        var job = Bridge.Instance.Run("subtitle_retranslate", new JsonObject
        {
            ["data"] = selected,
            ["file_path"] = _subtitlePath ?? "",
        });
        _retranslateJob = job;
        job.Progress += (v, msg) => { if (job == _retranslateJob) OnSubtitleOptimizationProgress(v, msg); };
        job.Finished += result =>
        {
            if (job != _retranslateJob) return;
            StartButton.IsEnabled = true;
            if (result is not JsonObject r) return; // 配置为空
            UpdateData(r);
            ProgressBar.Value = 100;
            StatusLabel.Text = Tr("重新翻译完成");
            Toast.Success(Tr("翻译完成"), Tr("已更新选中行的翻译"), DurationSuccess);
        };
        job.Error += error =>
        {
            if (job != _retranslateJob) return;
            StartButton.IsEnabled = true;
            ProgressError();
            StatusLabel.Text = Tr("重新翻译失败");
            Toast.Error(Tr("翻译失败"), error, DurationError);
        };
    }

    /// <summary>处理键盘事件</summary>
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers == KeyModifiers.Control && e.Key == Key.M)
        {
            var rows = SelectedRows();
            if (rows.Count > 1) MergeSelectedRows(rows);
            e.Handled = true;
        }
        else if (e.Key == Key.Delete)
        {
            var rows = SelectedRows();
            if (rows.Count > 0) DeleteSelectedRows(rows);
            e.Handled = true;
        }
        else if (e.KeyModifiers == KeyModifiers.Control && e.Key == Key.T)
        {
            if (Cfg.NeedTranslate.Value && !IsProcessing())
            {
                var rows = SelectedRows();
                if (rows.Count > 0) RetranslateSelectedRows(rows);
            }
            e.Handled = true;
        }
    }
}
