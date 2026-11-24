# VideoCaptioner: Avalonia frontend

A 1:1 Avalonia UI (FluentAvaloniaUI) port of the VideoCaptioner PyQt GUI. It covers the home page (任务创建 / 语音转录 / 字幕优化与翻译 / 字幕视频合成), 批量处理, 字幕样式, 请求日志 and 设置.
The processing itself is still done by the original Python code: `bridge/vc_bridge.py` runs the original `videocaptioner.ui.thread.*` workers in a headless Qt loop and talks to the UI over stdin/stdout (JSON lines).

## Run
```
dotnet run --project VideoCaptioner.Avalonia
# or build, then start VideoCaptioner.Avalonia/bin/Debug/net8.0/VideoCaptioner.exe
```
Requirements: .NET 8 SDK and a Python environment with `videocaptioner` installed.
The Python interpreter is looked up in this order: the `VC_PYTHON` environment variable, then `bridge.json` next to the exe (`{"python": "..."}`), then `F:\dev\miniconda3\envs\videocaptioner\python.exe`.

Both apps share `%LOCALAPPDATA%\VideoCaptioner\VideoCaptioner\settings.json`, style presets, models and logs with the Python GUI.

See CONVENTIONS.md for the architecture and the widget mapping. Helper scripts for UI screenshots and automation are in `tools/`.
