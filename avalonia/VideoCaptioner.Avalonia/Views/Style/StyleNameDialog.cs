using Avalonia.Controls;
using FluentAvalonia.UI.Controls;
using VideoCaptioner.Avalonia.Services;

namespace VideoCaptioner.Avalonia.Views.Style;

/// <summary>Port of subtitle_style_interface.StyleNameDialog (MessageBoxBase with a name LineEdit).</summary>
public class StyleNameDialog : ContentDialog
{
    protected override Type StyleKeyOverride => typeof(ContentDialog);

    private const string Ctx = "StyleNameDialog";

    public TextBox NameLineEdit { get; }

    public StyleNameDialog()
    {
        NameLineEdit = new TextBox
        {
            Watermark = I18n.Tr("输入样式名称", Ctx),
            Classes = { "clearButton" },
        };
        // Windows 11 dialog: the title is the dialog title, a short hint above the input
        Title = I18n.Tr("新建样式", Ctx);
        Content = new StackPanel
        {
            Spacing = 12,
            MinWidth = 360,
            Children =
            {
                new TextBlock
                {
                    Text = I18n.Tr("新样式会以当前参数为基础保存为预设", Ctx),
                    Classes = { "body" },
                    TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
                },
                NameLineEdit,
            },
        };
        PrimaryButtonText = I18n.Tr("确定", Ctx);
        CloseButtonText = I18n.Tr("取消", Ctx);
        DefaultButton = ContentDialogButton.Primary;
        IsPrimaryButtonEnabled = false;
        NameLineEdit.TextChanged += (_, _) => IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(NameLineEdit.Text);
        Opened += (_, _) => NameLineEdit.Focus();
    }

    /// <summary>dialog.exec(): returns the trimmed name, or null when cancelled.</summary>
    public async Task<string?> ExecAsync()
    {
        var ok = await ShowAsync() == ContentDialogResult.Primary;
        return ok ? (NameLineEdit.Text ?? "").Trim() : null;
    }
}
