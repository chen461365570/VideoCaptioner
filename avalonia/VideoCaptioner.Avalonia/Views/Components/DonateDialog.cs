using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using FluentAvalonia.UI.Controls;
using VideoCaptioner.Avalonia.Services;

namespace VideoCaptioner.Avalonia.Views.Components;

/// <summary>Port of videocaptioner/ui/components/DonateDialog.py (MessageBoxBase with two QR codes).</summary>
public class DonateDialog : ContentDialog
{
    private const string Ctx = "DonateDialog";
    protected override Type StyleKeyOverride => typeof(ContentDialog);

    // Same files as the Python dialog: WECHAT_QR_PATH = donate_green.jpg, ALIPAY_QR_PATH = donate_blue.jpg
    private const string WechatQrPath = "avares://VideoCaptioner/Assets/donate_green.jpg";
    private const string AlipayQrPath = "avares://VideoCaptioner/Assets/donate_blue.jpg";

    public DonateDialog()
    {
        Resources["ContentDialogMaxWidth"] = 900.0;
        SetupUi();
    }

    private void SetupUi()
    {
        // Win11 dialog: title in the dialog chrome, a short description, two QR cards side by side
        Title = I18n.Tr("感谢支持", Ctx);

        var heart = new SymbolIcon { Symbol = Symbol.Emoji, FontSize = 20, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) };
        heart.Bind(SymbolIcon.ForegroundProperty, heart.GetResourceObservable("AccentTextFillColorPrimaryBrush"));
        var descLabel = new TextBlock
        {
            Text = I18n.Tr("目前本人精力有限，您的支持让我有动力继续折腾这个项目！\n感谢您对开源事业的热爱与支持！", Ctx),
            Classes = { "body" },
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 22,
        };
        descLabel.Bind(TextBlock.ForegroundProperty, descLabel.GetResourceObservable("TextFillColorSecondaryBrush"));
        var intro = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 12 };
        Grid.SetColumn(descLabel, 1);
        intro.Children.Add(heart);
        intro.Children.Add(descLabel);

        var qrLayout = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 16 };
        var alipay = QrColumn(AlipayQrPath, I18n.Tr("支付宝", Ctx), I18n.Tr("打开支付宝扫一扫", Ctx));
        var wechat = QrColumn(WechatQrPath, I18n.Tr("微信", Ctx), I18n.Tr("打开微信扫一扫", Ctx));
        Grid.SetColumn(wechat, 1);
        qrLayout.Children.Add(alipay);
        qrLayout.Children.Add(wechat);

        Content = new StackPanel
        {
            Spacing = 24,
            MinWidth = 800 - 48,
            Children = { intro, qrLayout },
        };

        // 隐藏是按钮，只显示取消按钮
        CloseButtonText = I18n.Tr("关闭", Ctx);
        DefaultButton = ContentDialogButton.Close;
    }

    /// <summary>A card with the QR code (scaled into 300x300, KeepAspectRatio) on a white tile + caption.</summary>
    private static Control QrColumn(string uri, string caption, string hint)
    {
        var image = new Image
        {
            Source = new Bitmap(AssetLoader.Open(new Uri(uri))),
            MaxWidth = 300,
            MaxHeight = 300,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        RenderOptions.SetBitmapInterpolationMode(image, BitmapInterpolationMode.HighQuality);
        var tile = new Border
        {
            CornerRadius = new CornerRadius(8),
            ClipToBounds = true,
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = image,
        };
        var hintText = new TextBlock { Text = hint, Classes = { "caption" }, HorizontalAlignment = HorizontalAlignment.Center };
        return new Border
        {
            Classes = { "card" },
            Padding = new Thickness(16, 20, 16, 16),
            Child = new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    tile,
                    new TextBlock
                    {
                        Text = caption, Classes = { "strong" }, HorizontalAlignment = HorizontalAlignment.Center,
                        Margin = new Thickness(0, 12, 0, 0),
                    },
                    hintText,
                },
            },
        };
    }
}
