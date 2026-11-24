using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using FluentAvalonia.UI.Controls;
using VideoCaptioner.Avalonia.Services;

namespace VideoCaptioner.Avalonia.Views.Components;

/// <summary>Port of LogDetailDialog (MessageBoxBase) from videocaptioner/ui/view/llm_logs_interface.py.</summary>
public class LogDetailDialog : ContentDialog
{
    private const string Ctx = "LogDetailDialog";
    protected override Type StyleKeyOverride => typeof(ContentDialog);

    private readonly JsonObject _logEntry;
    private readonly Panel _infoBarHost = new() { VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Center };

    public LogDetailDialog(JsonObject logEntry)
    {
        _logEntry = logEntry;
        Resources["ContentDialogMaxWidth"] = 900.0;
        SetupUi();
    }

    private JsonNode RequestNode => _logEntry["request"] ?? new JsonObject();
    private JsonNode ResponseNode => _logEntry["response"] ?? new JsonObject();

    private void SetupUi()
    {
        // Win11 dialog: title in the dialog chrome, a summary card, then one section per JSON payload
        Title = I18n.Tr("请求详情", Ctx);
        var view = new StackPanel { Spacing = 4, MinWidth = 760 };

        // 提取信息
        var timeStr = LlmLogsInterface.Str(_logEntry["time"]);
        var model = LlmLogsInterface.ModelOf(_logEntry);
        var duration = LlmLogsInterface.DurationOf(_logEntry);
        var stage = LlmLogsInterface.Str(_logEntry["stage"]);
        if (stage.Length == 0) stage = "-";
        var usage = (ResponseNode as JsonObject)?["usage"] as JsonObject;
        var promptTokens = LlmLogsInterface.FormatNumber(LlmLogsInterface.Num(usage?["prompt_tokens"]));
        var completionTokens = LlmLogsInterface.FormatNumber(LlmLogsInterface.Num(usage?["completion_tokens"]));

        // 顶部信息：summary card with label / value pairs (time, stage, model, duration, input/output tokens)
        var summary = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*,*"),
            RowDefinitions = new RowDefinitions("Auto,Auto"),
            ColumnSpacing = 16,
            RowSpacing = 12,
        };
        var fields = new (Symbol icon, string label, string value)[]
        {
            (Symbol.Clock, I18n.Tr("时间", Ctx), timeStr),
            (Symbol.Tag, I18n.Tr("阶段", Ctx), stage),
            (Symbol.Globe, I18n.Tr("模型", Ctx), model),
            (Symbol.Refresh, I18n.Tr("耗时", Ctx), duration),
            (Symbol.Upload, "input token", promptTokens),
            (Symbol.Download, "output token", completionTokens),
        };
        for (var i = 0; i < fields.Length; i++)
        {
            var (icon, label, value) = fields[i];
            var cell = SummaryCell(icon, label, string.IsNullOrEmpty(value) ? "-" : value);
            Grid.SetColumn(cell, i % 3);
            Grid.SetRow(cell, i / 3);
            summary.Children.Add(cell);
        }
        view.Children.Add(Card(summary, new Thickness(16, 12)));

        var requestJson = PyJson.Dumps(RequestNode, 2, ensureAscii: false);
        var responseJson = PyJson.Dumps(ResponseNode, 2, ensureAscii: false);
        view.Children.Add(SectionHeader("Request", I18n.Tr("复制请求", Ctx), requestJson));
        view.Children.Add(CodeCard(requestJson));
        view.Children.Add(SectionHeader("Response", I18n.Tr("复制响应", Ctx), responseJson));
        view.Children.Add(CodeCard(responseJson));

        Content = new Panel { Children = { view, _infoBarHost } };

        // 底部按钮：复制请求 | 复制响应 | 关闭
        PrimaryButtonText = I18n.Tr("复制请求", Ctx);
        SecondaryButtonText = I18n.Tr("复制响应", Ctx);
        CloseButtonText = I18n.Tr("关闭", Ctx);
        DefaultButton = ContentDialogButton.Close;
        PrimaryButtonClick += async (_, e) =>
        {
            e.Cancel = true;
            await CopyAsync(requestJson);
        };
        SecondaryButtonClick += async (_, e) =>
        {
            e.Cancel = true;
            await CopyAsync(responseJson);
        };
    }

    private static Control SummaryCell(Symbol icon, string label, string value)
    {
        var caption = new TextBlock { Text = label, Classes = { "caption" } };
        var valueText = new TextBlock
        {
            Text = value,
            FontSize = 14,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        ToolTip.SetTip(valueText, value);
        var symbol = new SymbolIcon { Symbol = icon, FontSize = 16, VerticalAlignment = VerticalAlignment.Center };
        symbol.Bind(SymbolIcon.ForegroundProperty, symbol.GetResourceObservable("TextFillColorSecondaryBrush"));
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 12 };
        var texts = new StackPanel { Spacing = 2, Children = { caption, valueText } };
        Grid.SetColumn(texts, 1);
        grid.Children.Add(symbol);
        grid.Children.Add(texts);
        return grid;
    }

    /// <summary>Section title with an inline copy button on the right (like WinUI Gallery code samples).</summary>
    private Control SectionHeader(string title, string copyTip, string text)
    {
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(1, 20, 0, 4) };
        header.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 14,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var copy = new Button
        {
            Padding = new Thickness(8, 4),
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children =
                {
                    new SymbolIcon { Symbol = Symbol.Copy, FontSize = 14, VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = I18n.Tr("复制", Ctx), FontSize = 12, VerticalAlignment = VerticalAlignment.Center },
                },
            },
        };
        if (Application.Current?.TryGetResource("TransparentButton", null, out var theme) == true && theme is ControlTheme ct)
            copy.Theme = ct;
        ToolTip.SetTip(copy, copyTip);
        copy.Click += async (_, _) => await CopyAsync(text);
        Grid.SetColumn(copy, 1);
        header.Children.Add(copy);
        return header;
    }

    private static Border Card(Control child, Thickness padding) => new()
    {
        Classes = { "card" },
        Padding = padding,
        Child = child,
    };

    /// <summary>Monospace, read-only JSON inside a card.</summary>
    private static Border CodeCard(string text)
    {
        // read-only, selectable (Ctrl+C works) like the original read-only TextEdit
        var code = new SelectableTextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Cascadia Mono, Cascadia Code, Consolas, Microsoft YaHei UI"),
            FontSize = 12,
            LineHeight = 18,
            Margin = new Thickness(14, 10),
        };
        var edit = new ScrollViewer
        {
            Height = 220,
            HorizontalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = code,
        };
        return Card(edit, new Thickness(0));
    }

    private async Task CopyAsync(string text)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard ?? Dialogs.Top?.Clipboard;
        if (clipboard != null) await clipboard.SetTextAsync(text);

        // InfoBar.success(parent=dialog, position=TOP, duration=1500): shown inside the dialog
        var bar = new InfoBar
        {
            Severity = InfoBarSeverity.Success,
            Title = "",
            Message = I18n.Tr("已复制", Ctx),
            IsOpen = true,
            IsClosable = true,
            MinWidth = 240,
        };
        _infoBarHost.Children.Clear();
        _infoBarHost.Children.Add(bar);
        DispatcherTimer.RunOnce(() => _infoBarHost.Children.Remove(bar), TimeSpan.FromMilliseconds(1500));
    }
}
