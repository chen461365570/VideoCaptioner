# VideoCaptioner Avalonia frontend: conventions

This folder ports the PyQt5 + PyQt-Fluent-Widgets GUI in `../videocaptioner/ui/` to Avalonia 11.3 + FluentAvaloniaUI 2.4.
The goal is a **1:1 copy**: same pages, same layout and widget order, same texts (Chinese source strings), same defaults, same behaviour.
The backend is **not** reimplemented. The original Python code runs behind a JSON-lines bridge.

## Layout
```
avalonia/
  bridge/vc_bridge.py          Python backend host (headless Qt loop, runs the original QThread workers)
  bridge/handlers/<area>.py    command handlers, one module per UI area (auto-loaded)
  VideoCaptioner.Avalonia/     the .NET 8 app
    Services/AppConfig.cs      Cfg: typed settings.json access (same file as the Python app)
    Services/Bridge.cs         Bridge.Instance.Run(cmd, args) / RequestAsync(cmd, args)
    Services/Toast.cs          InfoBar replacement: Toast.Success/Warning/Error/Info(title, content, durationMs, position)
    Services/Dialogs.cs        MessageBoxAsync, ShowContentAsync, Open/Save file + folder pickers, OpenUrl, OpenFolder
    Services/I18n.cs           I18n.Tr("中文原文", "PythonClassName") and XAML {svc:Tr 中文原文}
    Services/SignalBus.cs      port of ui/common/signal_bus.py (static events + Emit* methods)
    Controls/SettingCardGroup  qfluentwidgets SettingCardGroup (Title + stacked cards)
    Controls/SegmentedWidget   qfluentwidgets SegmentedWidget / Pivot
    Styles/Controls.axaml      shared styles: TextBlock classes caption/subtitle/body/strong, Border.card
    Models/Options.g.cs        option lists generated from the real Python enums (bridge/gen_enums.py)
    Views/...                  one view per Python view/component
```

## Python → Avalonia widget mapping
| qfluentwidgets | Avalonia / FluentAvalonia |
|---|---|
| SettingCard / SwitchSettingCard / ComboBoxSettingCard / PushSettingCard / RangeSettingCard / HyperlinkSettingCard | `ui:SettingsExpander` (Header, Description, IconSource, Footer = right-hand control, IsClickEnabled for push cards) |
| ExpandGroupSettingCard / ExpandSettingCard | `ui:SettingsExpander` with `ui:SettingsExpanderItem` children |
| SettingCardGroup | `c:SettingCardGroup Title=...` |
| SwitchButton | `ToggleSwitch` (OnContent/OffContent "开"/"关" like qfluentwidgets) |
| ComboBox / EditableComboBox | `ComboBox` / `ui:FAComboBox IsEditable=True` |
| LineEdit / PasswordLineEdit / SearchLineEdit | `TextBox` (PasswordChar / Classes="revealPasswordButton") |
| SpinBox / DoubleSpinBox / Slider | `ui:NumberBox` / `Slider` |
| PushButton / PrimaryPushButton / ToolButton / TransparentToolButton | `Button` / `Button Classes="accent"` / `Button` with `ui:SymbolIcon` |
| ProgressBar / IndeterminateProgressBar / ProgressRing | `ProgressBar` / `ProgressBar IsIndeterminate` / `ui:ProgressRing` |
| TableWidget / TableView | `DataGrid` (package Avalonia.Controls.DataGrid is already referenced transitively; add a PackageReference 11.3.13 + StyleInclude if needed) or ListBox with a grid template |
| InfoBar | `Toast.*` |
| MessageBox / MessageBoxBase / Dialog | `Dialogs.MessageBoxAsync` / `ui:ContentDialog` subclass |
| FluentIcon.X | `ui:SymbolIcon Symbol="..."` (pick the closest FluentAvalonia Symbol) |
| CardWidget | `Border Classes="card"` |
| BodyLabel/CaptionLabel/SubtitleLabel/StrongBodyLabel | `TextBlock Classes="body"/"caption"/"subtitle"/"strong"` |
| QMediaPlayer + QVideoWidget | LibVLCSharp.Avalonia `VideoView` (packages already referenced) |
| QThread worker | a bridge command, see below |

## Config
`Cfg.<Item>` mirrors `ui/common/config.py` (the same key names, defaults and validators). Values are the **enum values** (Chinese strings such as `"FasterWhisper ✨"`), exactly as the Python app stores them.
Setting `.Value` persists immediately. XAML binding: `{Binding Value, Source={x:Static svc:Cfg.NeedOptimize}}` (Mode=TwoWay for inputs).
Option lists for combo boxes live in `Models.Options.*`. If you need an item that is missing from Cfg, add it to `AppConfig.cs` next to its group. Keep that edit small and only add lines.

## Bridge protocol
- C#: `var job = Bridge.Instance.Run("transcribe", new { file_path, need_next_task = true, task_id });`
  then use `job.Progress += (v, msg) => ...; job.Finished += data => ...; job.Error += msg => ...; job.Event += (name, data) => ...;` and `job.Cancel()`.
  For one-shot request/response calls use `await Bridge.Instance.RequestAsync("cmd", args)`, which returns a JsonNode or throws BridgeException.
  All events arrive on the UI thread.
- Python: in `bridge/handlers/<area>.py`
  ```python
  from vc_bridge import command, run_qthread, to_jsonable
  @command("transcribe")
  def transcribe(job, args):
      from videocaptioner.ui.task_factory import TaskFactory
      from videocaptioner.ui.thread.transcript_thread import TranscriptThread
      task = TaskFactory.create_transcribe_task(args["file_path"], need_next_task=args.get("need_next_task", False), task_id=args.get("task_id"))
      run_qthread(job, TranscriptThread(task), finished=lambda t: to_jsonable(t))
  ```
  `cfg` is reloaded from settings.json before every command, so TaskFactory sees what the Avalonia UI just saved.
  Handlers run on the Qt main thread and must not block. Put long work in a QThread or a Python thread, and call `job.emit/progress/finished/error` (these calls are thread-safe).
  Always reuse the original `videocaptioner.ui.thread.*` workers, `TaskFactory` and `videocaptioner.core.*`. Do not reimplement backend logic in C#.
  The Python package in use is the installed copy: `F:/dev/miniconda3/envs/videocaptioner/Lib/site-packages/videocaptioner` (identical to `../videocaptioner`).
- Standalone test of a handler: `printf '{"id":"1","cmd":"ping"}\n' | F:/dev/miniconda3/envs/videocaptioner/python.exe bridge/vc_bridge.py`

## Shared-file rules (several agents work in parallel)
- Only edit files you own. Shared files (MainWindow, HomeInterface, Services/*, Styles/Controls.axaml, vc_bridge.py, csproj) are owned by the lead. If you need a change there, keep it minimal and additive, and mention it in your report. Adding a style block to Controls.axaml or a Cfg item is OK.
- Put new page-private controls in `Views/<Area>/` or `Views/Components/`.
- Build in your own output dirs so builds do not lock each other:
  `dotnet build VideoCaptioner.Avalonia -p:OutputPath=F:/tmp/vc_<you>/bin/ -p:IntermediateOutputPath=F:/tmp/vc_<you>/obj/`
  (restore first once with `dotnet restore VideoCaptioner.Avalonia` if needed). Errors in files you do not own come from other agents who are still working. Ignore them, or wait and retry.
- Write complete, compiling files and do not leave half-finished edits.
- Run and look at your page: start `F:/tmp/vc_<you>/bin/VideoCaptioner.exe`, then run
  `powershell -ExecutionPolicy Bypass -File avalonia/tools/shot.ps1 -ProcessId <pid> -Out <png>` and view the PNG. Kill your own process afterwards (by PID only).
