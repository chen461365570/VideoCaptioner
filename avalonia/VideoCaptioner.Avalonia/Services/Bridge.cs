using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Threading;

namespace VideoCaptioner.Avalonia.Services;

/// <summary>
/// A running backend command. All events are raised on the UI thread.
/// Mirrors the QThread signal set of the original workers: progress / finished / error.
/// </summary>
public sealed class BridgeJob
{
    internal BridgeJob(string id, string cmd)
    {
        Id = id;
        Command = cmd;
    }

    public string Id { get; }
    public string Command { get; }
    public bool IsDone { get; private set; }

    /// <summary>(value 0-100, message)</summary>
    public event Action<int, string>? Progress;
    /// <summary>Job finished successfully; payload is command specific.</summary>
    public event Action<JsonNode?>? Finished;
    public event Action<string>? Error;
    public event Action? Cancelled;
    /// <summary>Any other (custom) event: (name, data).</summary>
    public event Action<string, JsonNode?>? Event;

    internal readonly TaskCompletionSource<JsonNode?> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes with the finished/result payload; throws BridgeException on error/cancel.</summary>
    public Task<JsonNode?> Task => Completion.Task;

    public void Cancel() => Bridge.Instance.Cancel(this);

    internal void Dispatch(string evt, JsonNode? data)
    {
        switch (evt)
        {
            case "progress":
                Progress?.Invoke(data?["value"]?.GetValue<int>() ?? 0, data?["message"]?.GetValue<string>() ?? "");
                break;
            case "finished":
            case "result":
                IsDone = true;
                Finished?.Invoke(data);
                Completion.TrySetResult(data);
                break;
            case "error":
                IsDone = true;
                var msg = data?["message"]?.GetValue<string>() ?? "unknown error";
                Error?.Invoke(msg);
                Completion.TrySetException(new BridgeException(msg));
                break;
            case "cancelled":
                IsDone = true;
                Cancelled?.Invoke();
                Completion.TrySetCanceled();
                break;
            default:
                Event?.Invoke(evt, data);
                break;
        }
    }
}

public sealed class BridgeException(string message) : Exception(message);

/// <summary>
/// Owns the Python backend process (bridge/vc_bridge.py running in the videocaptioner conda env).
/// Python interpreter resolution: env VC_PYTHON → bridge.json {"python": "..."} next to the exe → default conda env.
/// </summary>
public sealed class Bridge : IDisposable
{
    public static Bridge Instance { get; } = new();

    private const string DefaultPython = @"F:\dev\miniconda3\envs\videocaptioner\python.exe";
    private readonly ConcurrentDictionary<string, BridgeJob> _jobs = new();
    private readonly TaskCompletionSource<JsonNode?> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _writeLock = new();
    private Process? _process;
    private long _nextId;

    /// <summary>Unsolicited backend events (id ""): ready, log, ...</summary>
    public event Action<string, JsonNode?>? GlobalEvent;
    /// <summary>stderr lines from the backend (python logging).</summary>
    public event Action<string>? StdErr;
    /// <summary>Backend exited unexpectedly.</summary>
    public event Action<int>? Exited;

    /// <summary>Completes when the backend reported "ready" (payload: {"commands": [...]}).</summary>
    public Task<JsonNode?> Ready => _ready.Task;

    /// <summary>Paths reported by the backend (videocaptioner.config), filled after start.</summary>
    public IReadOnlyDictionary<string, string> Paths { get; private set; } = new Dictionary<string, string>();

    public static string ResolvePython()
    {
        var env = Environment.GetEnvironmentVariable("VC_PYTHON");
        if (!string.IsNullOrWhiteSpace(env)) return env;
        var cfgFile = Path.Combine(AppContext.BaseDirectory, "bridge.json");
        if (File.Exists(cfgFile))
        {
            try
            {
                var p = JsonNode.Parse(File.ReadAllText(cfgFile))?["python"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(p)) return p;
            }
            catch { /* fall through */ }
        }
        return DefaultPython;
    }

    public void Start()
    {
        if (_process != null) return;
        var script = Path.Combine(AppContext.BaseDirectory, "bridge", "vc_bridge.py");
        var psi = new ProcessStartInfo(ResolvePython())
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            // Writable cwd: ffmpeg writes temp files relative to it (System32 is not writable).
            WorkingDirectory = Cfg.AppDataPath,
        };
        psi.ArgumentList.Add("-u");
        psi.ArgumentList.Add(script);
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PYTHONUTF8"] = "1";
        Directory.CreateDirectory(Cfg.AppDataPath);

        try
        {
            _process = Process.Start(psi) ?? throw new InvalidOperationException("failed to start python");
        }
        catch (Exception e)
        {
            _ready.TrySetException(new BridgeException($"无法启动后端 ({psi.FileName}): {e.Message}"));
            return;
        }
        _process.StandardInput.AutoFlush = true;
        _process.EnableRaisingEvents = true;
        _process.Exited += (_, _) =>
        {
            var code = SafeExitCode();
            _ready.TrySetException(new BridgeException($"后端进程已退出 (code {code})"));
            foreach (var job in _jobs.Values)
                Dispatcher.UIThread.Post(() => job.Dispatch("error", new JsonObject { ["message"] = $"后端进程已退出 (code {code})" }));
            _jobs.Clear();
            Dispatcher.UIThread.Post(() => Exited?.Invoke(code));
        };

        new Thread(ReadStdout) { IsBackground = true, Name = "bridge-stdout" }.Start();
        new Thread(ReadStderr) { IsBackground = true, Name = "bridge-stderr" }.Start();
        _ = LoadPathsAsync();
    }

    private int SafeExitCode()
    {
        try { return _process?.ExitCode ?? -1; } catch { return -1; }
    }

    private async Task LoadPathsAsync()
    {
        try
        {
            await Ready;
            var res = await RequestAsync("paths");
            if (res is JsonObject o)
                Paths = o.ToDictionary(kv => kv.Key, kv => kv.Value?.GetValue<string>() ?? "");
        }
        catch { /* surfaced through Ready */ }
    }

    private void ReadStdout()
    {
        var reader = _process!.StandardOutput;
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            JsonNode? msg;
            try { msg = JsonNode.Parse(line); }
            catch { StdErrLine(line); continue; }
            if (msg is null) continue;
            var id = msg["id"]?.GetValue<string>() ?? "";
            var evt = msg["event"]?.GetValue<string>() ?? "";
            var data = msg["data"];
            data?.Parent?.AsObject().Remove("data"); // detach so it can be reused

            if (id.Length == 0)
            {
                if (evt == "ready") _ready.TrySetResult(data);
                Dispatcher.UIThread.Post(() => GlobalEvent?.Invoke(evt, data));
                continue;
            }
            if (!_jobs.TryGetValue(id, out var job)) continue;
            if (evt is "finished" or "result" or "error" or "cancelled")
                _jobs.TryRemove(id, out _);
            Dispatcher.UIThread.Post(() => job.Dispatch(evt, data));
        }
    }

    private void ReadStderr()
    {
        var reader = _process!.StandardError;
        string? line;
        while ((line = reader.ReadLine()) != null) StdErrLine(line);
    }

    private void StdErrLine(string line)
    {
        Debug.WriteLine("[bridge] " + line);
        var h = StdErr;
        if (h != null) Dispatcher.UIThread.Post(() => h(line));
    }

    /// <summary>Start a streaming job. Subscribe to events before awaiting anything.</summary>
    public BridgeJob Run(string cmd, object? args = null)
    {
        var id = Interlocked.Increment(ref _nextId).ToString();
        var job = new BridgeJob(id, cmd);
        _jobs[id] = job;
        _ = SendWhenReadyAsync(job, cmd, args);
        return job;
    }

    /// <summary>Request/response helper: awaits the "result" (or "finished") payload.</summary>
    public Task<JsonNode?> RequestAsync(string cmd, object? args = null) => Run(cmd, args).Task;

    public void Cancel(BridgeJob job)
    {
        if (job.IsDone) return;
        _ = SendWhenReadyAsync(null, "cancel", new { target = job.Id });
    }

    private async Task SendWhenReadyAsync(BridgeJob? job, string cmd, object? args)
    {
        try
        {
            await Ready;
        }
        catch (Exception e)
        {
            if (job != null)
            {
                _jobs.TryRemove(job.Id, out _);
                Dispatcher.UIThread.Post(() => job.Dispatch("error", new JsonObject { ["message"] = e.Message }));
            }
            return;
        }
        var payload = new JsonObject
        {
            ["id"] = job?.Id ?? "",
            ["cmd"] = cmd,
            ["args"] = args is null ? new JsonObject() : args as JsonNode ?? JsonSerializer.SerializeToNode(args),
        };
        lock (_writeLock)
        {
            try { _process!.StandardInput.WriteLine(payload.ToJsonString()); }
            catch (Exception e)
            {
                if (job != null)
                    Dispatcher.UIThread.Post(() => job.Dispatch("error", new JsonObject { ["message"] = e.Message }));
            }
        }
    }

    public void Dispose()
    {
        try
        {
            if (_process is { HasExited: false })
            {
                _process.StandardInput.Close(); // bridge quits on EOF and kills its children
                if (!_process.WaitForExit(3000)) _process.Kill(entireProcessTree: true);
            }
        }
        catch { /* ignore */ }
    }
}
