using Avalonia.Controls;
using Avalonia.Data;
using FluentAvalonia.UI.Controls;
using VideoCaptioner.Avalonia.Services;

namespace VideoCaptioner.Avalonia.Views.Components;

/// <summary>Port of videocaptioner/ui/components/SubtitleSettingDialog.py (字幕设置).</summary>
public static class SubtitleSettingDialog
{
    private const string Ctx = "SubtitleSettingDialog";

    public static async Task ShowAsync()
    {
        var descLabel = new TextBlock
        {
            Text = I18n.Tr("调整字幕断句方式与每条字幕的长度", Ctx),
            Classes = { "caption" },
            Margin = new global::Avalonia.Thickness(0, 0, 0, 8),
        };

        // SwitchSettingCard(FIF.ALIGNMENT, 字幕分割, ..., cfg.need_split)
        var splitSwitch = new ToggleSwitch
        {
            OnContent = I18n.Tr("开"),
            OffContent = I18n.Tr("关"),
            [!ToggleSwitch.IsCheckedProperty] = new Binding("Value") { Source = Cfg.NeedSplit, Mode = BindingMode.TwoWay },
        };
        var splitCard = SettingRow(Symbol.AlignLeft, I18n.Tr("字幕分割", Ctx),
            I18n.Tr("字幕是否使用大语言模型进行智能断句", Ctx), splitSwitch);

        var cjkCard = SpinBoxCard(Cfg.MaxWordCountCjk, I18n.Tr("中文最大字数", Ctx),
            I18n.Tr("单条字幕的最大字数 (对于中日韩等字符)", Ctx));
        var englishCard = SpinBoxCard(Cfg.MaxWordCountEnglish, I18n.Tr("英文最大单词数", Ctx),
            I18n.Tr("单条字幕的最大单词数 (英文)", Ctx));

        var layout = new StackPanel
        {
            Spacing = 4,
            Width = 460,
            Children = { descLabel, splitCard, cjkCard, englishCard },
        };

        // 只显示取消按钮
        var dlg = new ContentDialog
        {
            Title = I18n.Tr("字幕设置", Ctx),
            Content = layout,
            CloseButtonText = I18n.Tr("关闭", Ctx),
            DefaultButton = ContentDialogButton.Close,
        };
        await dlg.ShowAsync();
    }

    /// <summary>SpinBoxSettingCard(configItem, FIF.TILES, title, content, minimum=8, maximum=50).</summary>
    private static Control SpinBoxCard(ConfigItem<int> item, string title, string content)
    {
        var spin = new NumberBox
        {
            Minimum = 8,
            Maximum = 50,
            SmallChange = 1,
            SimpleNumberFormat = "F0",
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            MinWidth = 96,
            Value = item.Value,
        };
        spin.ValueChanged += (_, e) =>
        {
            if (!double.IsNaN(e.NewValue)) item.Value = (int)Math.Round(e.NewValue);
        };
        return SettingRow(Symbol.FontSize, title, content, spin);
    }

    /// <summary>
    /// Compact Windows 11 setting row for dialogs (icon, title + description, control on the right).
    /// SettingsExpander wraps its footer below the text at dialog widths, so dialogs use this instead.
    /// </summary>
    private static Control SettingRow(Symbol symbol, string title, string content, Control control)
    {
        control.VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center;
        var icon = new SymbolIcon { Symbol = symbol, FontSize = 20, VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center };
        var text = new StackPanel
        {
            VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = title, Classes = { "body" }, TextWrapping = global::Avalonia.Media.TextWrapping.Wrap },
                new TextBlock { Text = content, Classes = { "caption" }, TextWrapping = global::Avalonia.Media.TextWrapping.Wrap },
            },
        };
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            ColumnSpacing = 16,
            Children = { icon, text, control },
        };
        Grid.SetColumn(text, 1);
        Grid.SetColumn(control, 2);
        return new Border
        {
            Classes = { "card" },
            Padding = new global::Avalonia.Thickness(16, 12),
            MinHeight = 68,
            Child = grid,
        };
    }
}
