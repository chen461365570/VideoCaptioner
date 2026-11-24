using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using FluentAvalonia.UI.Controls;
using VideoCaptioner.Avalonia.Services;

namespace VideoCaptioner.Avalonia.Controls;

/// <summary>
/// Translations of the few qfluentwidgets built-in strings (FluentTranslator) used by the setting cards.
/// </summary>
public static class FluentTr
{
    private static readonly Dictionary<string, (string Cn, string Hk)> Map = new()
    {
        ["On"] = ("开", "開"),
        ["Off"] = ("关", "關"),
        ["Default color"] = ("默认颜色", "默認顏色"),
        ["Custom color"] = ("自定义颜色", "自定義顏色"),
        ["Choose color"] = ("选择颜色", "選擇顏色"),
        ["OK"] = ("确定", "確定"),
        ["Cancel"] = ("取消", "取消"),
    };

    public static string Tr(string source) =>
        Map.TryGetValue(source, out var t)
            ? I18n.Locale switch { "zh_CN" => t.Cn, "zh_HK" => t.Hk, _ => source }
            : source;
}

/// <summary>
/// qfluentwidgets SettingCard, rendered like the WinUI 3 / Windows 11 Settings SettingsCard:
/// 68px min height, 16px padding, 20px icon, header 14 / description 12 (secondary), the control right-aligned.
/// Implemented on top of ui:SettingsExpander (Header = title, Description = content, Footer = right control).
/// A SettingCard added to the Items of another SettingCard becomes a flat child row of that expander
/// (like the rows under "LLM 提供商" in the Win11 Settings app); hiding the card hides the row.
/// Config-bound cards subscribe to their ConfigItem only while attached, so short-lived dialogs do not leak.
/// </summary>
public class SettingCard : SettingsExpander
{
    protected override Type StyleKeyOverride => typeof(SettingsExpander);

    /// <summary>Standard widths of the right-hand controls (WinUI SettingsCard content).</summary>
    public const double ComboWidth = 200;
    public const double TextWidth = 320;
    public const double ButtonMinWidth = 120;
    public const double NumberWidth = 120;

    /// <summary>Padding of a child row: text aligned with the header text, control aligned with the header control.</summary>
    public static readonly Thickness ItemPadding = new(58, 8, 58, 8);

    private readonly List<(INotifyPropertyChanged Item, Action Refresh)> _hooks = [];
    private bool _attached;
    private Symbol? _symbol;
    private bool _isNested;

    public SettingCard()
    {
        Classes.Add("vcCard");
        // WinUI SettingsCard: 20px header icon (FluentAvalonia defaults to 24)
        Resources["SettingsExpanderItemIconSize"] = 20.0;
    }

    public SettingCard(Symbol icon, string title, string? content = null) : this()
    {
        Symbol = icon;
        Header = title;
        Description = string.IsNullOrEmpty(content) ? null : content;
    }

    /// <summary>
    /// True when the card is shown as a child row of another expander (set automatically when a card is
    /// added to a SettingCard's Items): no border, no icon, text aligned with the parent header.
    /// </summary>
    public bool IsNested
    {
        get => _isNested;
        set
        {
            if (_isNested == value) return;
            _isNested = value;
            Classes.Set("nested", value);
            if (value && Application.Current?.TryFindResource("VcNestedSettingCardTheme", out var theme) == true
                      && theme is ControlTheme ct)
                Theme = ct;
            else
                ClearValue(ThemeProperty);
        }
    }

    /// <summary>Child SettingCards are hosted as flat rows; the row follows the card's visibility.</summary>
    protected override void PrepareContainerForItemOverride(Control container, object? item, int index)
    {
        base.PrepareContainerForItemOverride(container, item, index);
        if (item is SettingCard card && container is SettingsExpanderItem row)
        {
            card.IsNested = true;
            row.Classes.Add("vcNestedRow");
            row.Padding = new Thickness(0);
            row.MinHeight = 0;
            row.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            row[!IsVisibleProperty] = card[!IsVisibleProperty];
        }
    }

    protected override void ClearContainerForItemOverride(Control container)
    {
        base.ClearContainerForItemOverride(container);
        container.ClearValue(IsVisibleProperty);
        container.Classes.Remove("vcNestedRow");
    }

    /// <summary>FluentIcon of the card (IconSource = SymbolIconSource).</summary>
    public Symbol Symbol
    {
        get => _symbol ?? Symbol.Setting;
        set
        {
            _symbol = value;
            IconSource = new SymbolIconSource { Symbol = value };
        }
    }

    public string? Title
    {
        get => Header as string;
        set => Header = value;
    }

    /// <summary>SettingCard.setContent</summary>
    public void SetContent(string? content) => Description = string.IsNullOrEmpty(content) ? null : content;

    /// <summary>Guard used while pushing config values into the control.</summary>
    protected bool Updating { get; set; }

    /// <summary>Refresh the control whenever <paramref name="item"/> changes (configItem.valueChanged.connect(setValue)).</summary>
    protected void Watch(INotifyPropertyChanged? item, Action refresh)
    {
        if (item is null) return;
        _hooks.Add((item, refresh));
        if (_attached) item.PropertyChanged += OnItemChanged;
        RunRefresh(refresh);
    }

    protected void UnwatchAll()
    {
        if (_attached)
            foreach (var (item, _) in _hooks) item.PropertyChanged -= OnItemChanged;
        _hooks.Clear();
    }

    private void RunRefresh(Action refresh)
    {
        Updating = true;
        try { refresh(); }
        finally { Updating = false; }
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        foreach (var (item, refresh) in _hooks)
            if (ReferenceEquals(item, sender)) RunRefresh(refresh);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_attached) return;
        _attached = true;
        foreach (var (item, refresh) in _hooks.DistinctBy(h => h.Item))
            item.PropertyChanged += OnItemChanged;
        foreach (var (_, refresh) in _hooks) RunRefresh(refresh);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (!_attached) return;
        _attached = false;
        foreach (var (item, _) in _hooks.DistinctBy(h => h.Item))
            item.PropertyChanged -= OnItemChanged;
    }
}

/// <summary>qfluentwidgets SwitchSettingCard (SwitchButton with 开/关 text).</summary>
public class SwitchSettingCard : SettingCard
{
    private ConfigItem<bool>? _item;

    public ToggleSwitch SwitchButton { get; } = new()
    {
        OnContent = null,
        OffContent = null,
        MinWidth = 0,
        Classes = { "vcCardSwitch" },
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>The 开/关 state text, left of the switch like Windows 11 Settings.</summary>
    public TextBlock StateLabel { get; } = new() { VerticalAlignment = VerticalAlignment.Center, Text = FluentTr.Tr("Off") };

    /// <summary>checkedChanged(bool)</summary>
    public event Action<bool>? CheckedChanged;

    public SwitchSettingCard()
    {
        // bare 40x20 switch in a 32px row: no header margin, no min width, no content column spacer
        SwitchButton.Resources["ToggleSwitchTopHeaderMargin"] = new Thickness(0);
        SwitchButton.Resources["ToggleSwitchThemeMinWidth"] = 0.0;
        SwitchButton.Margin = new Thickness(0, 0, -12, 0);
        SwitchButton.Height = 32;
        Footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            Children = { StateLabel, SwitchButton },
        };
        SwitchButton.IsCheckedChanged += (_, _) =>
        {
            var v = SwitchButton.IsChecked == true;
            StateLabel.Text = FluentTr.Tr(v ? "On" : "Off");
            if (!Updating && _item != null) _item.Value = v;
            CheckedChanged?.Invoke(v);
        };
    }

    public SwitchSettingCard(Symbol icon, string title, string? content, ConfigItem<bool>? item) : this()
    {
        Symbol = icon;
        Title = title;
        SetContent(content);
        ConfigItem = item;
    }

    public ConfigItem<bool>? ConfigItem
    {
        get => _item;
        set
        {
            UnwatchAll();
            _item = value;
            Watch(value, () => SwitchButton.IsChecked = _item!.Value);
        }
    }

    public bool IsChecked
    {
        get => SwitchButton.IsChecked == true;
        set => SwitchButton.IsChecked = value;
    }
}

/// <summary>
/// qfluentwidgets ComboBoxSettingCard(configItem, icon, title, content, texts):
/// the combo shows <see cref="Texts"/>, the config stores the matching entry of <see cref="Options"/>.
/// </summary>
public class ComboBoxSettingCard : SettingCard
{
    private ConfigItem<string>? _item;
    private IReadOnlyList<string>? _options;
    private IReadOnlyList<string>? _texts;

    public ComboBox ComboBox { get; } = new() { MinWidth = ComboWidth, MaxDropDownHeight = 360, VerticalAlignment = VerticalAlignment.Center };

    /// <summary>comboBox.currentTextChanged(str) (display text).</summary>
    public event Action<string>? CurrentTextChanged;

    public ComboBoxSettingCard()
    {
        Footer = ComboBox;
        ComboBox.SelectionChanged += (_, _) =>
        {
            var i = ComboBox.SelectedIndex;
            var opts = EffectiveOptions;
            if (i < 0 || i >= opts.Count) return;
            if (!Updating && _item != null) _item.Value = opts[i];
            CurrentTextChanged?.Invoke(EffectiveTexts[i]);
        };
    }

    public ComboBoxSettingCard(ConfigItem<string> item, Symbol icon, string title, string? content,
        IReadOnlyList<string>? texts = null, IReadOnlyList<string>? options = null) : this()
    {
        Symbol = icon;
        Title = title;
        SetContent(content);
        _options = options;
        _texts = texts;
        ConfigItem = item;
    }

    public ConfigItem<string>? ConfigItem
    {
        get => _item;
        set
        {
            UnwatchAll();
            _item = value;
            Rebuild();
            Watch(value, SyncFromConfig);
        }
    }

    /// <summary>Config values (defaults to ConfigItem.Options).</summary>
    public IReadOnlyList<string>? Options
    {
        get => _options;
        set { _options = value; Rebuild(); }
    }

    /// <summary>Display texts (defaults to the options themselves).</summary>
    public IReadOnlyList<string>? Texts
    {
        get => _texts;
        set { _texts = value; Rebuild(); }
    }

    private IReadOnlyList<string> EffectiveOptions => _options ?? _item?.Options ?? [];
    private IReadOnlyList<string> EffectiveTexts => _texts ?? EffectiveOptions;

    public string CurrentText => ComboBox.SelectedIndex >= 0 && ComboBox.SelectedIndex < EffectiveTexts.Count
        ? EffectiveTexts[ComboBox.SelectedIndex] : "";

    /// <summary>Replace the option list (config values and display texts).</summary>
    public void SetItems(IReadOnlyList<string> options, IReadOnlyList<string>? texts = null)
    {
        _options = options;
        _texts = texts;
        Rebuild();
    }

    private void Rebuild()
    {
        Updating = true;
        try
        {
            ComboBox.ItemsSource = EffectiveTexts.ToList();
            SyncFromConfig();
        }
        finally { Updating = false; }
    }

    private void SyncFromConfig()
    {
        if (_item == null) return;
        var idx = EffectiveOptions.ToList().IndexOf(_item.Value);
        ComboBox.SelectedIndex = idx;
    }
}

/// <summary>Port of ui/components/LineEditSettingCard.py.</summary>
public class LineEditSettingCard : SettingCard
{
    private ConfigItem<string>? _item;

    public TextBox LineEdit { get; } = new() { MinWidth = ComboWidth, Width = TextWidth, VerticalAlignment = VerticalAlignment.Center };

    /// <summary>textChanged(str)</summary>
    public event Action<string>? TextChanged;

    public LineEditSettingCard()
    {
        Footer = LineEdit;
        LineEdit.TextChanged += (_, _) =>
        {
            var t = LineEdit.Text ?? "";
            if (!Updating && _item != null) _item.Value = t;
            if (!Updating) TextChanged?.Invoke(t);
        };
    }

    public LineEditSettingCard(ConfigItem<string> item, Symbol icon, string title, string? content = null,
        string placeholder = "") : this()
    {
        Symbol = icon;
        Title = title;
        SetContent(content);
        Placeholder = placeholder;
        ConfigItem = item;
    }

    public ConfigItem<string>? ConfigItem
    {
        get => _item;
        set
        {
            UnwatchAll();
            _item = value;
            Watch(value, () =>
            {
                if (LineEdit.Text != _item!.Value) LineEdit.Text = _item.Value;
            });
        }
    }

    public string Placeholder
    {
        get => LineEdit.Watermark ?? "";
        set => LineEdit.Watermark = value;
    }

    /// <summary>Secret value (API keys): masked with a reveal button, like a WinUI PasswordBox.</summary>
    public bool IsPassword
    {
        get => LineEdit.PasswordChar != default;
        set
        {
            LineEdit.PasswordChar = value ? '●' : default;
            LineEdit.Classes.Set("revealPasswordButton", value);
        }
    }

    /// <summary>lineEdit.setReadOnly</summary>
    public bool IsReadOnly
    {
        get => LineEdit.IsReadOnly;
        set => LineEdit.IsReadOnly = value;
    }

    /// <summary>lineEdit.setMinimumWidth (also used as the fixed editor width).</summary>
    public double EditorWidth
    {
        get => LineEdit.Width;
        set { LineEdit.MinWidth = Math.Min(value, LineEdit.MinWidth); LineEdit.Width = value; }
    }

    public string Text
    {
        get => LineEdit.Text ?? "";
        set => LineEdit.Text = value;
    }
}

/// <summary>Port of ui/components/EditComboBoxSettingCard.py (EditableComboBox + contains-completer).</summary>
public class EditComboBoxSettingCard : SettingCard
{
    private ConfigItem<string>? _item;
    private List<string> _items = [];

    public FAComboBox ComboBox { get; } = new()
    {
        IsEditable = true,
        MinWidth = ComboWidth,
        Width = TextWidth,
        MaxDropDownHeight = 360,
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>currentTextChanged(str)</summary>
    public event Action<string>? CurrentTextChanged;

    public EditComboBoxSettingCard()
    {
        Footer = ComboBox;
        ComboBox.PropertyChanged += (_, e) =>
        {
            if (e.Property != FAComboBox.TextProperty) return;
            var t = ComboBox.Text ?? "";
            if (!Updating && _item != null) _item.Value = t;
            if (!Updating) CurrentTextChanged?.Invoke(t);
        };
        ComboBox.SelectionChanged += (_, _) =>
        {
            if (Updating || ComboBox.SelectedItem is not string s) return;
            if (ComboBox.Text != s) ComboBox.Text = s;
        };
    }

    public EditComboBoxSettingCard(ConfigItem<string> item, Symbol icon, string title, string? content = null,
        IEnumerable<string>? items = null) : this()
    {
        Symbol = icon;
        Title = title;
        SetContent(content);
        SetItems(items ?? []);
        ConfigItem = item;
    }

    public ConfigItem<string>? ConfigItem
    {
        get => _item;
        set
        {
            UnwatchAll();
            _item = value;
            Watch(value, () =>
            {
                if (ComboBox.Text != _item!.Value) ComboBox.Text = _item.Value;
            });
        }
    }

    public IReadOnlyList<string> Items => _items;

    public string Text
    {
        get => ComboBox.Text ?? "";
        set => ComboBox.Text = value;
    }

    public double EditorWidth
    {
        get => ComboBox.Width;
        set { ComboBox.MinWidth = Math.Min(value, ComboBox.MinWidth); ComboBox.Width = value; }
    }

    /// <summary>addItems</summary>
    public void AddItems(IEnumerable<string> items)
    {
        _items.AddRange(items);
        ApplyItems();
    }

    /// <summary>setItems: replace the list, keep the current text.</summary>
    public void SetItems(IEnumerable<string> items)
    {
        _items = items.ToList();
        ApplyItems();
    }

    private void ApplyItems()
    {
        var text = ComboBox.Text;
        Updating = true;
        try
        {
            ComboBox.ItemsSource = _items.ToList();
            ComboBox.SelectedIndex = -1;
            ComboBox.Text = text;
        }
        finally { Updating = false; }
    }
}

/// <summary>Port of SpinBoxSettingCard.py SpinBoxSettingCard (CompactSpinBox bound to a ConfigItem&lt;int&gt;).</summary>
public class SpinBoxSettingCard : SettingCard
{
    private ConfigItem<int>? _item;

    public NumberBox SpinBox { get; } = new()
    {
        SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
        MinWidth = NumberWidth,
        SmallChange = 1,
        SimpleNumberFormat = "F0",
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>valueChanged(int)</summary>
    public event Action<int>? ValueChanged;

    public SpinBoxSettingCard()
    {
        Footer = SpinBox;
        SpinBox.ValueChanged += (_, e) =>
        {
            if (double.IsNaN(e.NewValue)) { SpinBox.Value = _item?.Value ?? SpinBox.Minimum; return; }
            var v = (int)Math.Round(e.NewValue);
            if (!Updating && _item != null) _item.Value = v;
            if (!Updating) ValueChanged?.Invoke(v);
        };
    }

    public SpinBoxSettingCard(ConfigItem<int> item, Symbol icon, string title, string? content = null,
        int minimum = 0, int maximum = 100) : this()
    {
        Symbol = icon;
        Title = title;
        SetContent(content);
        Minimum = minimum;
        Maximum = maximum;
        ConfigItem = item;
    }

    public int Minimum { get => (int)SpinBox.Minimum; set => SpinBox.Minimum = value; }
    public int Maximum { get => (int)SpinBox.Maximum; set => SpinBox.Maximum = value; }

    public ConfigItem<int>? ConfigItem
    {
        get => _item;
        set
        {
            UnwatchAll();
            _item = value;
            Watch(value, () => SpinBox.Value = _item!.Value);
        }
    }
}

/// <summary>Port of SpinBoxSettingCard.py DoubleSpinBoxSettingCard (CompactDoubleSpinBox bound to a ConfigItem&lt;double&gt;).</summary>
public class DoubleSpinBoxSettingCard : SettingCard
{
    private ConfigItem<double>? _item;
    private int _decimals = 1;

    public NumberBox SpinBox { get; } = new()
    {
        SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
        MinWidth = NumberWidth,
        SmallChange = 0.1,
        SimpleNumberFormat = "F1",
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>valueChanged(float)</summary>
    public event Action<double>? ValueChanged;

    public DoubleSpinBoxSettingCard()
    {
        Footer = SpinBox;
        SpinBox.ValueChanged += (_, e) =>
        {
            if (double.IsNaN(e.NewValue)) { SpinBox.Value = _item?.Value ?? SpinBox.Minimum; return; }
            var v = Math.Round(e.NewValue, _decimals);
            if (!Updating && _item != null) _item.Value = v;
            if (!Updating) ValueChanged?.Invoke(v);
        };
    }

    public DoubleSpinBoxSettingCard(ConfigItem<double> item, Symbol icon, string title, string? content = null,
        double minimum = 0, double maximum = 100, int decimals = 1, double step = 0.1) : this()
    {
        Symbol = icon;
        Title = title;
        SetContent(content);
        Minimum = minimum;
        Maximum = maximum;
        Decimals = decimals;
        Step = step;
        ConfigItem = item;
    }

    public double Minimum { get => SpinBox.Minimum; set => SpinBox.Minimum = value; }
    public double Maximum { get => SpinBox.Maximum; set => SpinBox.Maximum = value; }
    public double Step { get => SpinBox.SmallChange; set => SpinBox.SmallChange = value; }

    public int Decimals
    {
        get => _decimals;
        set { _decimals = value; SpinBox.SimpleNumberFormat = "F" + value; }
    }

    public ConfigItem<double>? ConfigItem
    {
        get => _item;
        set
        {
            UnwatchAll();
            _item = value;
            Watch(value, () => SpinBox.Value = _item!.Value);
        }
    }
}

/// <summary>qfluentwidgets RangeSettingCard: value label + slider (range from the RangeConfigItem).</summary>
public class RangeSettingCard : SettingCard
{
    private ConfigItem<int>? _item;

    public Slider Slider { get; } = new()
    {
        Width = ComboWidth,
        TickFrequency = 1,
        IsSnapToTickEnabled = true,
        SmallChange = 1,
        VerticalAlignment = VerticalAlignment.Center,
    };

    public TextBlock ValueLabel { get; } = new() { VerticalAlignment = VerticalAlignment.Center, MinWidth = 24, TextAlignment = TextAlignment.Right };

    /// <summary>valueChanged(int)</summary>
    public event Action<int>? ValueChanged;

    public RangeSettingCard()
    {
        Footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            Children = { ValueLabel, Slider },
        };
        Slider.PropertyChanged += (_, e) =>
        {
            if (e.Property != RangeBase.ValueProperty) return;
            var v = (int)Math.Round(Slider.Value);
            ValueLabel.Text = v.ToString();
            if (!Updating && _item != null) _item.Value = v;
            if (!Updating) ValueChanged?.Invoke(v);
        };
    }

    public RangeSettingCard(ConfigItem<int> item, Symbol icon, string title, string? content = null) : this()
    {
        Symbol = icon;
        Title = title;
        SetContent(content);
        ConfigItem = item;
    }

    public ConfigItem<int>? ConfigItem
    {
        get => _item;
        set
        {
            UnwatchAll();
            _item = value;
            if (value != null)
            {
                Slider.Minimum = value.Min ?? 0;
                Slider.Maximum = value.Max ?? 100;
            }
            Watch(value, () =>
            {
                Slider.Value = _item!.Value;
                ValueLabel.Text = _item.Value.ToString();
            });
        }
    }
}

/// <summary>qfluentwidgets PushSettingCard / PrimaryPushSettingCard (button on the right; clicked = button.clicked).</summary>
public class PushSettingCard : SettingCard
{
    public Button Button { get; } = new() { VerticalAlignment = VerticalAlignment.Center, MinWidth = ButtonMinWidth, HorizontalContentAlignment = HorizontalAlignment.Center };

    /// <summary>clicked</summary>
    public event Action? Clicked;

    public PushSettingCard()
    {
        Footer = Button;
        Button.Click += (_, _) => Clicked?.Invoke();
    }

    public PushSettingCard(string text, Symbol icon, string title, string? content = null) : this()
    {
        ButtonText = text;
        Symbol = icon;
        Title = title;
        SetContent(content);
    }

    public string? ButtonText
    {
        get => Button.Content as string;
        set => Button.Content = value;
    }

    /// <summary>PrimaryPushSettingCard: accent button.</summary>
    public bool IsPrimary
    {
        get => Button.Classes.Contains("accent");
        set => Button.Classes.Set("accent", value);
    }
}

/// <summary>qfluentwidgets PrimaryPushSettingCard.</summary>
public class PrimaryPushSettingCard : PushSettingCard
{
    public PrimaryPushSettingCard() => IsPrimary = true;

    public PrimaryPushSettingCard(string text, Symbol icon, string title, string? content = null)
        : base(text, icon, title, content) => IsPrimary = true;
}

/// <summary>qfluentwidgets HyperlinkCard(url, text, icon, title, content): opens url; linkButton.clicked for url "".</summary>
public class HyperlinkCard : SettingCard
{
    public HyperlinkButton LinkButton { get; } = new() { VerticalAlignment = VerticalAlignment.Center };

    /// <summary>linkButton.clicked</summary>
    public event Action? LinkClicked;

    public HyperlinkCard()
    {
        Footer = LinkButton;
        LinkButton.Click += (_, _) =>
        {
            if (!string.IsNullOrEmpty(Url)) Dialogs.OpenUrl(Url);
            LinkClicked?.Invoke();
        };
    }

    public HyperlinkCard(string url, string text, Symbol icon, string title, string? content = null) : this()
    {
        Url = url;
        LinkText = text;
        Symbol = icon;
        Title = title;
        SetContent(content);
    }

    public string? Url { get; set; }

    public string? LinkText
    {
        get => LinkButton.Content as string;
        set => LinkButton.Content = value;
    }
}

/// <summary>qfluentwidgets OptionsSettingCard: expandable card with one radio button per option.</summary>
public class OptionsSettingCard : SettingCard
{
    private ConfigItem<string>? _item;
    private IReadOnlyList<string>? _texts;
    private readonly StackPanel _radios = new() { Spacing = 4 };
    private readonly TextBlock _choiceLabel = new() { VerticalAlignment = VerticalAlignment.Center, Classes = { "caption" } };
    private readonly string _group = Guid.NewGuid().ToString("N");

    /// <summary>optionChanged(configItem)</summary>
    public event Action<string>? OptionChanged;

    public OptionsSettingCard()
    {
        Footer = _choiceLabel;
        // radio list aligned with the header text (WinUI SettingsExpander item padding)
        Items.Add(new SettingsExpanderItem { Content = _radios, Padding = ItemPadding });
    }

    public OptionsSettingCard(ConfigItem<string> item, Symbol icon, string title, string? content,
        IReadOnlyList<string> texts) : this()
    {
        Symbol = icon;
        Title = title;
        SetContent(content);
        _texts = texts;
        ConfigItem = item;
    }

    public IReadOnlyList<string>? Texts
    {
        get => _texts;
        set { _texts = value; Rebuild(); }
    }

    public ConfigItem<string>? ConfigItem
    {
        get => _item;
        set
        {
            UnwatchAll();
            _item = value;
            Rebuild();
            Watch(value, Sync);
        }
    }

    private void Rebuild()
    {
        _radios.Children.Clear();
        if (_item?.Options is not { } options) return;
        for (var i = 0; i < options.Count; i++)
        {
            var option = options[i];
            var rb = new RadioButton
            {
                Content = _texts != null && i < _texts.Count ? _texts[i] : option,
                GroupName = _group,
                Tag = option,
            };
            rb.IsCheckedChanged += (_, _) =>
            {
                if (rb.IsChecked != true || Updating || _item == null) return;
                _item.Value = option;
                _choiceLabel.Text = rb.Content as string;
                OptionChanged?.Invoke(option);
            };
            _radios.Children.Add(rb);
        }
        Updating = true;
        try { Sync(); }
        finally { Updating = false; }
    }

    private void Sync()
    {
        if (_item == null) return;
        foreach (var rb in _radios.Children.OfType<RadioButton>())
        {
            var on = (string?)rb.Tag == _item.Value;
            rb.IsChecked = on;
            if (on) _choiceLabel.Text = rb.Content as string;
        }
    }
}

/// <summary>qfluentwidgets CustomColorSettingCard: default / custom radio + "选择颜色" button (ColorDialog).</summary>
public class CustomColorSettingCard : SettingCard
{
    private ConfigItem<string>? _item;
    private readonly string _group = Guid.NewGuid().ToString("N");
    private readonly RadioButton _defaultRadio = new() { Content = FluentTr.Tr("Default color") };
    private readonly RadioButton _customRadio = new() { Content = FluentTr.Tr("Custom color") };
    private readonly Button _chooseButton = new() { Content = FluentTr.Tr("Choose color") };

    /// <summary>colorChanged(QColor) — #AARRGGBB string.</summary>
    public event Action<string>? ColorChanged;

    public CustomColorSettingCard()
    {
        _defaultRadio.GroupName = _group;
        _customRadio.GroupName = _group;
        Items.Add(new SettingsExpanderItem
        {
            Padding = ItemPadding,
            Content = new StackPanel
            {
                Spacing = 4,
                Children = { _defaultRadio, _customRadio },
            },
        });
        Items.Add(new SettingsExpanderItem
        {
            Padding = ItemPadding,
            Content = new TextBlock { Text = FluentTr.Tr("Custom color") },
            Footer = _chooseButton,
        });

        _defaultRadio.IsCheckedChanged += (_, _) =>
        {
            if (_defaultRadio.IsChecked != true) return;
            _chooseButton.IsEnabled = false;
            if (Updating || _item == null) return;
            if (_item.Value != _item.Default)
            {
                _item.Value = _item.Default;
                ColorChanged?.Invoke(_item.Default);
            }
        };
        _customRadio.IsCheckedChanged += (_, _) =>
        {
            if (_customRadio.IsChecked != true) return;
            _chooseButton.IsEnabled = true;
            if (Updating || _item == null) return;
            if (_customColor != null && _customColor != _item.Value)
            {
                _item.Value = _customColor;
                ColorChanged?.Invoke(_customColor);
            }
        };
        _chooseButton.Click += async (_, _) =>
        {
            if (_item == null) return;
            var chosen = await ColorDialog.ShowAsync(_item.Value, FluentTr.Tr("Choose color"), false);
            if (chosen == null) return;
            _customColor = chosen;
            _item.Value = chosen;
            ColorChanged?.Invoke(chosen);
        };
    }

    private string? _customColor;

    public CustomColorSettingCard(ConfigItem<string> item, Symbol icon, string title, string? content = null) : this()
    {
        Symbol = icon;
        Title = title;
        SetContent(content);
        ConfigItem = item;
    }

    public ConfigItem<string>? ConfigItem
    {
        get => _item;
        set
        {
            UnwatchAll();
            _item = value;
            Watch(value, () =>
            {
                var isDefault = string.Equals(_item!.Value, _item.Default, StringComparison.OrdinalIgnoreCase);
                if (!isDefault) _customColor = _item.Value;
                _defaultRadio.IsChecked = isDefault;
                _customRadio.IsChecked = !isDefault;
                _chooseButton.IsEnabled = !isDefault;
            });
        }
    }
}

/// <summary>qfluentwidgets ColorDialog: a ContentDialog hosting Avalonia's ColorView.</summary>
public static class ColorDialog
{
    /// <summary>Returns the chosen color as #AARRGGBB (QColor.name(HexArgb) style), or null when cancelled.</summary>
    public static async Task<string?> ShowAsync(string current, string title, bool enableAlpha)
    {
        App.TryParseQColor(current, out var start);
        var view = new ColorView
        {
            Color = start,
            IsAlphaEnabled = enableAlpha,
            IsAlphaVisible = enableAlpha,
            IsColorPaletteVisible = false,
            Width = 320,
        };
        var dlg = new ContentDialog
        {
            Title = title,
            Content = view,
            PrimaryButtonText = FluentTr.Tr("OK"),
            CloseButtonText = FluentTr.Tr("Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return null;
        var c = view.Color;
        if (!enableAlpha) c = Color.FromArgb(255, c.R, c.G, c.B);
        return ToQColorName(c);
    }

    public static string ToQColorName(Color c) => $"#{c.A:x2}{c.R:x2}{c.G:x2}{c.B:x2}";
}
