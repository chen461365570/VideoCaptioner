using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using VideoCaptioner.Avalonia.Services;
using VideoCaptioner.Avalonia.Views.Components;

namespace VideoCaptioner.Avalonia.Views;

/// <summary>One table row of the LLM request log.</summary>
public sealed class LlmLogRow
{
    public required JsonObject Entry { get; init; }
    public string Time { get; init; } = "";
    public string TaskId { get; init; } = "";
    public string FileName { get; init; } = "";
    public string Stage { get; init; } = "";
    public string Model { get; init; } = "";
    public string Duration { get; init; } = "";
    public string Tokens { get; init; } = "";
    /// <summary>No stage recorded: show a plain dash instead of an empty tag.</summary>
    public bool NoStage => Stage is "" or "-";
}

/// <summary>
/// Port of videocaptioner/ui/view/llm_logs_interface.py (LLMLogsInterface).
/// The log is plain JSONL (config.LLM_LOG_FILE), so it is read directly here like the Python view does.
/// </summary>
public partial class LlmLogsInterface : UserControl, IWindowTitleProvider
{
    private const string Ctx = "LLMLogsInterface";
    private const int PageSize = 50;

    private List<JsonObject> _allLogs = [];
    private List<JsonObject> _filteredLogs = [];
    private int _currentPage;

    private string? _llmLogFile;
    private FileSystemWatcher? _fileWatcher;
    private DispatcherTimer? _reloadDebounce;

    public string? WindowTitle { get; } = I18n.Tr("LLM 请求日志", Ctx);

    public LlmLogsInterface()
    {
        InitializeComponent();
        ConnectSignals();
        _ = InitAsync();
    }

    private void ConnectSignals()
    {
        RefreshBtn.Click += (_, _) => OnRefreshClicked();
        ClearBtn.Click += async (_, _) => await ClearLogsAsync();
        SearchEdit.TextChanged += (_, _) => FilterLogs();
        Table.DoubleTapped += (_, e) => ShowDetail(e);
        // side pane: summary of the selected request + a button for the same detail dialog as double-click
        Table.SelectionChanged += (_, _) => UpdateSelectionSummary();
        DetailBtn.Click += async (_, _) =>
        {
            if (Table.SelectedItem is LlmLogRow row) await new LogDetailDialog(row.Entry).ShowAsync();
        };
        PrevBtn.Click += (_, _) => PrevPage();
        NextBtn.Click += (_, _) => NextPage();
    }

    /// <summary>LLM_LOG_FILE / LOG_PATH come from videocaptioner.config via the backend "paths" command.</summary>
    private async Task InitAsync()
    {
        string logPath;
        try
        {
            var paths = await Bridge.Instance.RequestAsync("paths");
            _llmLogFile = paths?["LLM_LOG_FILE"]?.GetValue<string>();
            logPath = paths?["LOG_PATH"]?.GetValue<string>() ?? Path.GetDirectoryName(_llmLogFile) ?? "";
        }
        catch
        {
            // Same location as config.py (APPDATA_PATH / "logs" / "llm_requests.jsonl").
            logPath = Path.Combine(Cfg.AppDataPath, "logs");
            _llmLogFile = Path.Combine(logPath, "llm_requests.jsonl");
        }
        LoadLogs();
        SetupFileWatcher(logPath);

        // Development hook for screenshots: VC_LOGS_OPEN_DETAIL=1 opens the newest entry's detail dialog.
        if (Environment.GetEnvironmentVariable("VC_LOGS_OPEN_DETAIL") == "1" && _filteredLogs.Count > 0)
        {
            await Task.Delay(1500);
            await new LogDetailDialog(_filteredLogs[0]).ShowAsync();
        }
    }

    /// <summary>QFileSystemWatcher on the log file and its directory: reload when the file changes or is created.</summary>
    private void SetupFileWatcher(string logPath)
    {
        if (_llmLogFile == null || !Directory.Exists(logPath)) return;
        _reloadDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _reloadDebounce.Tick += (_, _) =>
        {
            _reloadDebounce.Stop();
            LoadLogs();
        };
        _fileWatcher = new FileSystemWatcher(logPath, Path.GetFileName(_llmLogFile))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.CreationTime,
        };
        FileSystemEventHandler onChange = (_, _) => Dispatcher.UIThread.Post(() =>
        {
            _reloadDebounce.Stop();
            _reloadDebounce.Start();
        });
        _fileWatcher.Changed += onChange;
        _fileWatcher.Created += onChange;
        _fileWatcher.Renamed += (s, e) => onChange(s, e);
        _fileWatcher.EnableRaisingEvents = true;
    }

    private void OnRefreshClicked()
    {
        LoadLogs();
        Toast.Success("", I18n.Tr("刷新成功", Ctx), 1000, ToastPosition.Top);
    }

    private void LoadLogs()
    {
        _allLogs = [];
        if (_llmLogFile == null || !File.Exists(_llmLogFile))
        {
            UpdateTable();
            return;
        }

        try
        {
            using var stream = new FileStream(_llmLogFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, new UTF8Encoding(false));
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                line = line.Trim();
                if (line.Length == 0) continue;
                try
                {
                    if (JsonNode.Parse(line) is JsonObject obj) _allLogs.Add(obj);
                }
                catch (JsonException)
                {
                }
            }
        }
        catch (Exception e)
        {
            Toast.Error(I18n.Tr("错误", Ctx), e.Message, 3000, ToastPosition.Top);
            return;
        }

        _allLogs.Reverse();
        FilterLogs();
    }

    private void FilterLogs()
    {
        var searchText = (SearchEdit.Text ?? "").ToLowerInvariant();
        if (searchText.Length == 0)
        {
            _filteredLogs = [.. _allLogs];
        }
        else
        {
            _filteredLogs = [];
            foreach (var log in _allLogs)
            {
                var request = log["request"] as JsonObject;
                var model = Str(request?["model"]).ToLowerInvariant();
                var taskId = Str(log["task_id"]).ToLowerInvariant();
                var fileName = Str(log["file_name"]).ToLowerInvariant();
                var stage = Str(log["stage"]).ToLowerInvariant();
                var messages = PyJson.Dumps(request?.ContainsKey("messages") == true ? request["messages"] : new JsonArray());
                var response = PyJson.Dumps(log.ContainsKey("response") ? log["response"] : new JsonObject());

                if (model.Contains(searchText) || taskId.Contains(searchText) || fileName.Contains(searchText)
                    || stage.Contains(searchText) || messages.ToLowerInvariant().Contains(searchText)
                    || response.ToLowerInvariant().Contains(searchText))
                    _filteredLogs.Add(log);
            }
        }

        _currentPage = 0;
        UpdateTable();
    }

    private void UpdateTable()
    {
        var totalPages = Math.Max(1, (_filteredLogs.Count + PageSize - 1) / PageSize);
        var startIdx = _currentPage * PageSize;
        var endIdx = Math.Min(startIdx + PageSize, _filteredLogs.Count);

        var rows = new List<LlmLogRow>();
        for (var i = startIdx; i < endIdx; i++)
        {
            var log = _filteredLogs[i];

            // 时间（不显示年份：MM-DD HH:MM:SS）
            var timeStr = Str(log["time"]);
            if (timeStr.Length > 5) timeStr = timeStr[5..];

            var usage = (log["response"] as JsonObject)?["usage"] as JsonObject;
            var totalTokens = Num(usage?["total_tokens"]);
            if (totalTokens == 0)
                totalTokens = Num(usage?["prompt_tokens"]) + Num(usage?["completion_tokens"]);

            rows.Add(new LlmLogRow
            {
                Entry = log,
                Time = timeStr,
                TaskId = OrDash(Str(log["task_id"])),
                FileName = OrDash(Str(log["file_name"])),
                Stage = OrDash(Str(log["stage"])),
                Model = ModelOf(log),
                Duration = DurationOf(log),
                Tokens = FormatNumber(totalTokens),
            });
        }
        Table.ItemsSource = rows;

        PageLabel.Text = $"{_currentPage + 1} / {totalPages}";
        PrevBtn.IsEnabled = _currentPage > 0;
        NextBtn.IsEnabled = _currentPage < totalPages - 1;
        StatusLabel.Text = $"共 {_filteredLogs.Count} 条";

        // Win11 empty state inside the table card (no log file yet, or nothing matches the search)
        var empty = _filteredLogs.Count == 0;
        EmptyState.IsVisible = empty;
        Table.IsVisible = !empty;
        if (empty)
        {
            var searching = !string.IsNullOrEmpty(SearchEdit.Text);
            EmptyTitle.Text = searching ? I18n.Tr("没有匹配的日志", Ctx) : I18n.Tr("暂无请求日志", Ctx);
            EmptyCaption.Text = searching
                ? I18n.Tr("换个关键词试试，可搜索任务ID、文件名、模型或消息内容", Ctx)
                : I18n.Tr("调用大模型处理字幕后，请求记录会自动显示在这里", Ctx);
        }
    }

    private void UpdateSelectionSummary()
    {
        var row = Table.SelectedItem as LlmLogRow;
        SelectionSummary.IsVisible = row != null;
        NoSelectionHint.IsVisible = row == null;
        if (row == null) return;
        SummaryFile.Text = row.FileName;
        SummaryModel.Text = row.Model;
        SummaryTime.Text = row.Time;
        SummaryStage.Text = row.Stage;
        SummaryDuration.Text = row.Duration;
        SummaryTokens.Text = row.Tokens;
        SummaryTaskId.Text = row.TaskId;
    }

    private async void ShowDetail(TappedEventArgs e)
    {
        if ((e.Source as Control)?.DataContext is not LlmLogRow row) return;
        await new LogDetailDialog(row.Entry).ShowAsync();
    }

    private void PrevPage()
    {
        if (_currentPage <= 0) return;
        _currentPage--;
        UpdateTable();
    }

    private void NextPage()
    {
        var totalPages = (_filteredLogs.Count + PageSize - 1) / PageSize;
        if (_currentPage >= totalPages - 1) return;
        _currentPage++;
        UpdateTable();
    }

    private async Task ClearLogsAsync()
    {
        var ok = await Dialogs.MessageBoxAsync(I18n.Tr("确认清空", Ctx), I18n.Tr("确定要清空所有日志吗？此操作不可恢复。", Ctx));
        if (!ok) return;
        try
        {
            if (_llmLogFile != null && File.Exists(_llmLogFile)) File.Delete(_llmLogFile);
            _allLogs = [];
            _filteredLogs = [];
            UpdateTable();
            Toast.Success("", I18n.Tr("日志已清空", Ctx), 2000, ToastPosition.Top);
        }
        catch (Exception ex)
        {
            Toast.Error(I18n.Tr("错误", Ctx), ex.Message, 3000, ToastPosition.Top);
        }
    }

    // ---------------------------------------------------------------- helpers shared with LogDetailDialog

    /// <summary>str value of a JSON field ("" for missing/null, like log.get(key, "")).</summary>
    internal static string Str(JsonNode? node) => node switch
    {
        null => "",
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        _ => node.ToJsonString(),
    };

    internal static double Num(JsonNode? node)
    {
        if (node is JsonValue v)
        {
            if (v.TryGetValue<double>(out var d)) return d;
            if (v.TryGetValue<long>(out var l)) return l;
        }
        return 0;
    }

    internal static string FormatNumber(double v) =>
        v == Math.Floor(v) ? ((long)v).ToString(CultureInfo.InvariantCulture) : v.ToString(CultureInfo.InvariantCulture);

    private static string OrDash(string s) => s.Length == 0 ? "-" : s;

    /// <summary>log.get("request", {}).get("model", "未知")</summary>
    internal static string ModelOf(JsonObject log)
    {
        var request = log["request"] as JsonObject;
        return request != null && request.ContainsKey("model") ? Str(request["model"]) : "未知";
    }

    /// <summary>f"{duration_ms / 1000:.1f}s"</summary>
    internal static string DurationOf(JsonObject log) =>
        (Num(log["duration_ms"]) / 1000).ToString("0.0", CultureInfo.InvariantCulture) + "s";
}

/// <summary>Python json.dumps formatting (used for search matching and the detail view).</summary>
internal static class PyJson
{
    /// <summary>json.dumps(obj) (ensure_ascii=True, separators ", " / ": ") or json.dumps(obj, indent, ensure_ascii=False).</summary>
    public static string Dumps(JsonNode? node, int? indent = null, bool ensureAscii = true)
    {
        var sb = new StringBuilder();
        Write(sb, node, indent, ensureAscii, 0);
        return sb.ToString();
    }

    private static void Write(StringBuilder sb, JsonNode? node, int? indent, bool ascii, int level)
    {
        switch (node)
        {
            case null:
                sb.Append("null");
                break;
            case JsonObject obj:
                if (obj.Count == 0) { sb.Append("{}"); break; }
                sb.Append('{');
                var first = true;
                foreach (var (key, value) in obj)
                {
                    if (!first) sb.Append(indent == null ? ", " : ",");
                    first = false;
                    NewLine(sb, indent, level + 1);
                    WriteString(sb, key, ascii);
                    sb.Append(": ");
                    Write(sb, value, indent, ascii, level + 1);
                }
                NewLine(sb, indent, level);
                sb.Append('}');
                break;
            case JsonArray arr:
                if (arr.Count == 0) { sb.Append("[]"); break; }
                sb.Append('[');
                for (var i = 0; i < arr.Count; i++)
                {
                    if (i > 0) sb.Append(indent == null ? ", " : ",");
                    NewLine(sb, indent, level + 1);
                    Write(sb, arr[i], indent, ascii, level + 1);
                }
                NewLine(sb, indent, level);
                sb.Append(']');
                break;
            case JsonValue v when v.TryGetValue<string>(out var s):
                WriteString(sb, s, ascii);
                break;
            default:
                sb.Append(node.ToJsonString()); // numbers / booleans keep their source text
                break;
        }
    }

    private static void NewLine(StringBuilder sb, int? indent, int level)
    {
        if (indent == null) return;
        sb.Append('\n').Append(' ', indent.Value * level);
    }

    private static void WriteString(StringBuilder sb, string s, bool ascii)
    {
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (c < 0x20 || (ascii && c > 0x7E))
                        sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else
                        sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }
}
