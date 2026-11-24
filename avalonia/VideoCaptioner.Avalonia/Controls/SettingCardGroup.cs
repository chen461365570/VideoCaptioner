using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Metadata;
using Avalonia.Controls.Presenters;

namespace VideoCaptioner.Avalonia.Controls;

/// <summary>
/// qfluentwidgets SettingCardGroup: a 20px title followed by vertically stacked cards (2px apart).
/// Children are the cards (ui:SettingsExpander is the SettingCard / ExpandGroupSettingCard equivalent).
/// Styled in Styles/Controls.axaml.
/// </summary>
[TemplatePart("PART_Items", typeof(ItemsPresenter))]
public class SettingCardGroup : ItemsControl
{
    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<SettingCardGroup, string?>(nameof(Title));

    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }
}
