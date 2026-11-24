using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using FluentAvalonia.UI.Controls;
using VideoCaptioner.Avalonia.Services;
using VideoCaptioner.Avalonia.Views.Components;

namespace VideoCaptioner.Avalonia.Views.Home;

/// <summary>
/// Port of videocaptioner/ui/components/transcription_setting_card.py: a stacked widget that shows the
/// WhisperCpp / WhisperAPI / FasterWhisper setting widget of the selected transcription model (empty otherwise).
/// On the Windows 11 transcription page the widget is hosted below the media card; the page scrolls as a whole.
/// </summary>
public class TranscriptionSettingCard : UserControl
{
    // 空白页面作为默认显示：在线接口无需额外设置，显示一张说明卡片
    private readonly Control _emptyWidget = CreateEmptyWidget();
    private readonly WhisperCppSettingWidget _whisperCppWidget = new();
    private readonly WhisperAPISettingWidget _whisperApiWidget = new();
    // FasterWhisper 在 macOS 上不可用
    private readonly FasterWhisperSettingWidget? _fasterWhisperWidget = OperatingSystem.IsMacOS() ? null : new();

    public TranscriptionSettingCard()
    {
        Content = _emptyWidget;
    }

    /// <summary>切换对应的设置界面</summary>
    public void OnModelChanged(string value)
    {
        var widget = value switch
        {
            "WhisperCpp" => (Control?)_whisperCppWidget,
            "Whisper [API] ✨" => _whisperApiWidget,
            "FasterWhisper ✨" => _fasterWhisperWidget,
            _ => null,
        } ?? _emptyWidget;
        Content = widget;
        AlignHostedWidget(widget);
    }

    /// <summary>
    /// The setting widgets wrap their groups in a ScrollViewer with an inner margin (made for a stand-alone
    /// panel). Here they sit in the page column, so cancel that inner margin to line the setting cards up
    /// with the media card above.
    /// </summary>
    private void AlignHostedWidget(Control widget)
    {
        var inner = (widget as UserControl)?.Content is ScrollViewer { Content: Control c } ? c.Margin : default;
        Margin = new Thickness(-inner.Left, -inner.Top, -inner.Right, -inner.Bottom);
    }

    private static Control CreateEmptyWidget()
    {
        const string ctx = "TranscriptionSettingCard";
        return new Border
        {
            Classes = { "card" },
            Child = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*"),
                ColumnSpacing = 16,
                Children =
                {
                    new SymbolIcon { Symbol = Symbol.Globe, FontSize = 20, VerticalAlignment = VerticalAlignment.Center },
                    new StackPanel
                    {
                        [Grid.ColumnProperty] = 1,
                        Spacing = 2,
                        Children =
                        {
                            new TextBlock { Text = I18n.Tr("在线转录接口", ctx) },
                            new TextBlock
                            {
                                Text = I18n.Tr("当前转录模型使用在线服务，无需额外配置", ctx),
                                Classes = { "caption" },
                                TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
                            },
                        },
                    },
                },
            },
        };
    }
}
