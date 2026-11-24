# VideoCaptioner: Windows 11 redesign guide

The UI was first ported 1:1 from qfluentwidgets. It is now being **redesigned** to look like a native Windows 11 / WinUI 3 app, such as the Windows 11 Settings app, the WinUI 3 Gallery, Photos or Media Player.
You may change layout, grouping, spacing and components to follow Win11 conventions. **You may not drop or change functionality**: every button, option, menu entry, dialog, keyboard shortcut, drag & drop, InfoBar message, config binding, SignalBus emit and bridge call must still exist and work the same. Keep the Chinese texts; you may add short Chinese descriptions or subtitles where Win11 would show one. Wrap them with I18n.Tr like the existing texts.

## Shell (already done by the lead, do not change)
- Mica window. The NavigationView pane expands with labels when the window is wide. Pages sit on NavigationView's rounded content layer (LayerFillColorDefault).
- System accent colour, and the font Segoe UI Variable / Microsoft YaHei UI.
- To open a page directly for screenshots, set env vars before starting the exe: `VC_START_PAGE=home|batch|style|logs|settings` and, for home, `VC_HOME_TAB=TaskCreationInterface|TranscriptionInterface|SubtitleInterface|VideoSynthesisInterface`.
- Screenshot: `powershell -ExecutionPolicy Bypass -File F:/dev/VideoCaptioner/avalonia/tools/shot.ps1 -ProcessId <pid> -Out <png>`. It brings the window to the front and copies the screen, and it shows Mica correctly. Kill only your own PID afterwards.

## Page anatomy (all top-level pages)
```
ScrollViewer (only if the page scrolls as a whole; tables/editors fill the height instead)
└ StackPanel/Grid  Margin="36,28,36,28"  MaxWidth≈1100 for form-like pages (HorizontalAlignment=Stretch)
   ├ TextBlock.pageTitle         "设置" / "批量处理" / ...  (28 SemiBold)
   ├ TextBlock.caption (optional) one-line description under the title
   ├ command row                  right-aligned primary action (Button.accent) + secondary buttons
   ├ TextBlock.section            "转录配置" (14 SemiBold, Margin 1,24,0,8)
   └ cards …                      Spacing="4" between cards of one section
```

## Components and tokens (use theme resources only; never hardcode colours)
| Need | Use |
|---|---|
| Setting row (icon, title, description, control on the right) | `ui:SettingsExpander` (Header, Description, IconSource, Footer). Group related settings as `ui:SettingsExpanderItem` children of an expander, like Win11 Settings (for example the LLM provider expander holding key/base/model). |
| Card / panel | `Border.card` (CardBackgroundFillColorDefaultBrush, CardStrokeColorDefaultBrush, radius 8, padding 16) |
| Card title | `TextBlock.cardTitle` (16 SemiBold); secondary text `TextBlock.caption` |
| Primary action | `Button Classes="accent"` with a SymbolIcon + text, one per view |
| Toolbar | `ui:CommandBar` with `ui:CommandBarButton` / `ui:CommandBarToggleButton` / `ui:CommandBarSeparator`, or a StackPanel of subtle buttons (Button Theme `{StaticResource TransparentButton}`) with 16px icons |
| Tabs inside a page | the existing SegmentedWidget (already restyled as a Win11 SelectorBar) |
| Drag & drop target | Grid with `Rectangle.dropZone` behind the content (add class `dragOver` while dragging), a large 48px `ui:SymbolIcon` and a two-line hint |
| Status / errors inside a page | inline `ui:InfoBar` (IsClosable etc.), plus the existing Toast calls |
| Progress | `ProgressBar` (Win11 thin bar) + TextBlock.caption status; `ui:ProgressRing` for indeterminate spinners |
| Tables | DataGrid inside `Border.card` with Padding 0 and ClipToBounds (global table styles already exist in Styles/Controls.axaml) |
| Tags / badges | Border radius 4, SubtleFillColorSecondaryBrush background, 12px caption text |
| Empty states | centred 48px SymbolIcon (TextFillColorTertiaryBrush) + BodyStrong title + caption line |
| Links | `HyperlinkButton` |

Spacing: 4 (between cards), 8 (between controls), 12/16 (inside cards), 24 (between sections), 36 (page side margin).
Corner radius: 4 for controls, 8 for cards and panels.
Icons: `ui:SymbolIcon` 16px in buttons and cards, 20px in setting rows (SettingsExpander does this itself).
Both themes must look right. Check Dark (the current config) and also Light: temporarily set `QFluentWidgets.ThemeMode` in settings.json, then **restore it**.

## Rules
- Only edit the files of your area (listed in your task). If you need a shared style, add it to `Styles/Controls.axaml` as a clearly separated, additive block prefixed with a comment naming your area. Never edit other agents' files.
- Keep the code-behind logic. Keep element names referenced from code-behind, or update the code-behind accordingly. Public APIs used by other views (SetTask/Process/Finished events, ShowTab, dialog classes and ctors) must stay unchanged.
- Build into your own dirs: `dotnet build F:/dev/VideoCaptioner/avalonia/VideoCaptioner.Avalonia -p:OutputPath=F:/tmp/vcr_<you>/bin/ -p:IntermediateOutputPath=F:/tmp/vcr_<you>/obj/`. Errors in files you don't own come from agents still working; ignore them or retry later.
- Do not change the user's settings permanently (back up settings.json and restore it byte-for-byte). Never print API keys, and delete screenshots that show them.
