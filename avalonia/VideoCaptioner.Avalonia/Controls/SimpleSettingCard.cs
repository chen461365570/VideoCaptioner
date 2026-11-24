using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;

namespace VideoCaptioner.Avalonia.Controls;

/// <summary>
/// Port of ui/components/SimpleSettingCard.py: a compact WinUI SettingsCard (48px row, 4px corners,
/// 14px header on the left, the control right-aligned) with the content text as tooltip (shown below, 100 ms delay).
/// </summary>
public class SimpleSettingCard : Border
{
    protected readonly Grid Layout = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 16 };

    public TextBlock Label { get; } = new() { Classes = { "body" }, VerticalAlignment = VerticalAlignment.Center, TextTrimming = global::Avalonia.Media.TextTrimming.CharacterEllipsis };

    /// <summary>CardWidget.clicked</summary>
    public event Action? Clicked;

    public SimpleSettingCard(string title, string content)
    {
        Title = title;
        ContentText = content;
        Classes.Add("card");
        Padding = new Thickness(16, 8, 16, 8);
        MinHeight = 48;
        CornerRadius = new CornerRadius(4);
        Label.Text = title;
        Layout.Children.Add(Label);
        Child = Layout;
        ToolTip.SetTip(this, content);
        ToolTip.SetPlacement(this, PlacementMode.Bottom);
        ToolTip.SetShowDelay(this, 100);
        PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton == MouseButton.Left && e.Source is not Button && IsPointerOver)
                Clicked?.Invoke();
        };
    }

    public string Title { get; }
    public string ContentText { get; }

    /// <summary>hBoxLayout.addWidget(control) after the stretch.</summary>
    protected void AddRight(Control control)
    {
        control.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(control, 1);
        Layout.Children.Add(control);
    }
}

/// <summary>ComboBoxSimpleSettingCard (max 6 visible items).</summary>
public class ComboBoxSimpleSettingCard : SimpleSettingCard
{
    private readonly List<string> _items;

    public ComboBox ComboBox { get; } = new() { MaxDropDownHeight = 6 * 33, MinWidth = 160 };

    /// <summary>valueChanged(str)</summary>
    public event Action<string>? ValueChanged;

    public ComboBoxSimpleSettingCard(string title, string content, IEnumerable<string>? items = null)
        : base(title, content)
    {
        _items = items?.ToList() ?? [];
        ComboBox.ItemsSource = _items;
        if (_items.Count > 0) ComboBox.SelectedIndex = 0;
        ComboBox.SelectionChanged += (_, _) => ValueChanged?.Invoke(Value());
        AddRight(ComboBox);
    }

    public void SetValue(string value) => ComboBox.SelectedIndex = _items.IndexOf(value);

    public string Value() => ComboBox.SelectedItem as string ?? "";
}

/// <summary>SwitchButtonSimpleSettingCard (开/关 switch; clicking the card toggles it).</summary>
public class SwitchButtonSimpleSettingCard : SimpleSettingCard
{
    public ToggleSwitch SwitchButton { get; } = new() { OnContent = "开", OffContent = "关", MinWidth = 0 };

    /// <summary>checkedChanged(bool)</summary>
    public event Action<bool>? CheckedChanged;

    public SwitchButtonSimpleSettingCard(string title, string content) : base(title, content)
    {
        SwitchButton.IsCheckedChanged += (_, _) => CheckedChanged?.Invoke(SwitchButton.IsChecked == true);
        AddRight(SwitchButton);
        Clicked += () => SwitchButton.IsChecked = SwitchButton.IsChecked != true;
    }

    public void SetChecked(bool value) => SwitchButton.IsChecked = value;

    public bool IsChecked() => SwitchButton.IsChecked == true;
}
