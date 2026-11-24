using Avalonia.Controls;
using FluentAvalonia.UI.Controls;
using VideoCaptioner.Avalonia.Controls;
using VideoCaptioner.Avalonia.Services;

namespace VideoCaptioner.Avalonia.Views.Components;

/// <summary>
/// Port of videocaptioner/ui/components/TranscriptionSettingDialog.py:
/// "转录设置" title + output format card; only the "关闭" button is shown.
/// Usage: <c>await new TranscriptionSettingDialog().ShowAsync();</c>
/// </summary>
public class TranscriptionSettingDialog : ContentDialog
{
    protected override Type StyleKeyOverride => typeof(ContentDialog);

    private const string Ctx = "TranscriptionSettingDialog";
    private static string Tr(string s) => I18n.Tr(s, Ctx);

    public ComboBoxSettingCard OutputFormatCard { get; }

    public TranscriptionSettingDialog()
    {
        OutputFormatCard = new ComboBoxSettingCard(Cfg.TranscribeOutputFormat, Symbol.Save,
            Tr("输出格式"), Tr("选择转录字幕的输出格式"));

        // Win11 dialog: "转录设置" as the dialog title, the option as a settings card
        Title = Tr("转录设置");
        Content = new StackPanel
        {
            Spacing = 4,
            Width = 480,
            Children = { OutputFormatCard },
        };
        CloseButtonText = Tr("关闭");
    }
}
