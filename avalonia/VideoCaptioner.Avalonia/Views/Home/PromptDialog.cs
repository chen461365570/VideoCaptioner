using Avalonia.Controls;
using Avalonia.Media;
using FluentAvalonia.UI.Controls;
using VideoCaptioner.Avalonia.Services;

namespace VideoCaptioner.Avalonia.Views.Home;

/// <summary>Port of PromptDialog (videocaptioner/ui/view/subtitle_interface.py): 文稿提示.</summary>
public static class PromptDialog
{
    private const string Ctx = "PromptDialog";

    /// <summary>Shows the dialog; on 确定 saves the text to cfg.custom_prompt_text and returns true.</summary>
    public static async Task<bool> ShowAsync()
    {
        var descLabel = new TextBlock
        {
            Text = I18n.Tr("提供术语表、原始文稿或修正要求，辅助校正字幕和翻译", Ctx),
            Classes = { "caption" },
            TextWrapping = TextWrapping.Wrap,
        };
        var textEdit = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Width = 480,
            MinHeight = 380,
            Height = 380,
            VerticalContentAlignment = global::Avalonia.Layout.VerticalAlignment.Top,
            Watermark = I18n.Tr(
                "请输入文稿提示（辅助校正字幕和翻译）\n\n" +
                "支持以下内容:\n" +
                "1. 术语表 - 专业术语、人名、特定词语的修正对照表\n" +
                "示例:\n机器学习->Machine Learning\n马斯克->Elon Musk\n打call->应援\n\n" +
                "2. 原字幕文稿 - 视频的原有文稿或相关内容\n" +
                "示例: 完整的演讲稿、课程讲义等\n\n" +
                "3. 修正要求 - 内容相关的具体修正要求\n" +
                "示例: 统一人称代词、规范专业术语等\n\n" +
                "注意: 使用小型LLM模型时建议控制文稿在1千字内。对于不同字幕文件,请使用与该字幕相关的文稿提示。", Ctx),
            Text = Cfg.CustomPromptText.Value,
        };
        var layout = new StackPanel { Spacing = 12, Children = { descLabel, textEdit } };

        var dlg = new ContentDialog
        {
            Title = I18n.Tr("文稿提示", Ctx),
            Content = layout,
            PrimaryButtonText = I18n.Tr("确定", Ctx),
            CloseButtonText = I18n.Tr("取消", Ctx),
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return false;
        // 在点击确定按钮时保存提示文本到配置
        Cfg.CustomPromptText.Value = textEdit.Text ?? "";
        return true;
    }
}
