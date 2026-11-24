using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using FluentAvalonia.UI.Controls;
using VideoCaptioner.Avalonia.Services;

// Port of ui/components/MySettingCard.py: setting cards that are NOT bound to a ConfigItem
// (the caller sets values and listens to the change events). Separate namespace because the
// class names clash with the config-bound qfluentwidgets / SpinBoxSettingCard.py equivalents.
namespace VideoCaptioner.Avalonia.Controls.MySettingCard;

/// <summary>MySettingCard.SettingCard (icon + title + content; right-hand control in Footer).</summary>
public class SettingCard : Controls.SettingCard
{
    public SettingCard() { }

    public SettingCard(Symbol icon, string title, string? content = null) : base(icon, title, content) { }

    /// <summary>setValue(value): no-op in the base class.</summary>
    public virtual void SetValue(object? value) { }
}

/// <summary>MySettingCard.DoubleSpinBoxSettingCard (CompactDoubleSpinBox, step 0.2).</summary>
public class DoubleSpinBoxSettingCard : SettingCard
{
    private bool _setting;

    public NumberBox SpinBox { get; } = new()
    {
        SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
        MinWidth = NumberWidth,
        SmallChange = 0.2,
        SimpleNumberFormat = "F1",
        VerticalAlignment = VerticalAlignment.Center,
    };

    public event Action<double>? ValueChanged;

    public DoubleSpinBoxSettingCard()
    {
        Footer = SpinBox;
        SpinBox.ValueChanged += (_, e) =>
        {
            if (double.IsNaN(e.NewValue)) { SpinBox.Value = SpinBox.Minimum; return; }
            if (!_setting) ValueChanged?.Invoke(Math.Round(e.NewValue, Decimals));
        };
    }

    public DoubleSpinBoxSettingCard(Symbol icon, string title, string? content = null,
        double minimum = 0.0, double maximum = 100.0, int decimals = 1) : this()
    {
        Symbol = icon;
        Title = title;
        SetContent(content);
        SpinBox.Minimum = minimum;
        SpinBox.Maximum = maximum;
        Decimals = decimals;
    }

    private int _decimals = 1;

    public int Decimals
    {
        get => _decimals;
        set { _decimals = value; SpinBox.SimpleNumberFormat = "F" + value; }
    }

    public double Value => SpinBox.Value;

    /// <summary>setValue (does not raise ValueChanged, like a programmatic set with no change).</summary>
    public void SetValue(double value)
    {
        _setting = true;
        try { SpinBox.Value = value; }
        finally { _setting = false; }
    }

    public override void SetValue(object? value) => SetValue(Convert.ToDouble(value));
}

/// <summary>MySettingCard.SpinBoxSettingCard (CompactSpinBox, default step 2).</summary>
public class SpinBoxSettingCard : SettingCard
{
    private bool _setting;

    public NumberBox SpinBox { get; } = new()
    {
        SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
        MinWidth = NumberWidth,
        SmallChange = 2,
        SimpleNumberFormat = "F0",
        VerticalAlignment = VerticalAlignment.Center,
    };

    public event Action<int>? ValueChanged;

    public SpinBoxSettingCard()
    {
        Footer = SpinBox;
        SpinBox.ValueChanged += (_, e) =>
        {
            if (double.IsNaN(e.NewValue)) { SpinBox.Value = SpinBox.Minimum; return; }
            if (!_setting) ValueChanged?.Invoke((int)Math.Round(e.NewValue));
        };
    }

    public SpinBoxSettingCard(Symbol icon, string title, string? content = null,
        int minimum = 0, int maximum = 100, int step = 2) : this()
    {
        Symbol = icon;
        Title = title;
        SetContent(content);
        SpinBox.Minimum = minimum;
        SpinBox.Maximum = maximum;
        SpinBox.SmallChange = step;
    }

    public int Value => (int)Math.Round(SpinBox.Value);

    public void SetValue(int value)
    {
        _setting = true;
        try { SpinBox.Value = value; }
        finally { _setting = false; }
    }

    public override void SetValue(object? value) => SetValue(Convert.ToInt32(value));
}

/// <summary>MySettingCard.ComboBoxSettingCard (plain ComboBox, no config item).</summary>
public class ComboBoxSettingCard : SettingCard
{
    private readonly List<string> _texts = [];

    public ComboBox ComboBox { get; } = new() { MinWidth = ComboWidth, MaxDropDownHeight = 360, VerticalAlignment = VerticalAlignment.Center };

    public event Action<string>? CurrentTextChanged;
    public event Action<int>? CurrentIndexChanged;

    public ComboBoxSettingCard()
    {
        Footer = ComboBox;
        ComboBox.SelectionChanged += (_, _) =>
        {
            var i = ComboBox.SelectedIndex;
            CurrentTextChanged?.Invoke(i >= 0 && i < _texts.Count ? _texts[i] : "");
            CurrentIndexChanged?.Invoke(i);
        };
    }

    public ComboBoxSettingCard(Symbol icon, string title, string? content = null, IEnumerable<string>? texts = null) : this()
    {
        Symbol = icon;
        Title = title;
        SetContent(content);
        if (texts != null) AddItems(texts);
    }

    public string CurrentText => ComboBox.SelectedIndex >= 0 && ComboBox.SelectedIndex < _texts.Count ? _texts[ComboBox.SelectedIndex] : "";
    public int CurrentIndex => ComboBox.SelectedIndex;

    public void SetCurrentText(string text)
    {
        var i = _texts.IndexOf(text);
        if (i >= 0) ComboBox.SelectedIndex = i;
    }

    public void SetCurrentIndex(int index) => ComboBox.SelectedIndex = index;

    public void AddItem(string text) => AddItems([text]);

    public void AddItems(IEnumerable<string> texts)
    {
        var sel = ComboBox.SelectedIndex;
        _texts.AddRange(texts);
        ComboBox.ItemsSource = _texts.ToList();
        // qfluentwidgets ComboBox selects the first item when items are added to an empty box.
        ComboBox.SelectedIndex = sel >= 0 ? sel : (_texts.Count > 0 ? 0 : -1);
    }

    public void Clear()
    {
        _texts.Clear();
        ComboBox.ItemsSource = new List<string>();
    }
}

/// <summary>MySettingCard.ColorSettingCard (ColorPickerButton on the right).</summary>
public class ColorSettingCard : SettingCard
{
    public ColorPickerButton ColorPicker { get; }

    /// <summary>colorChanged(QColor)</summary>
    public event Action<Color>? ColorChanged;

    public ColorSettingCard(Color color, Symbol icon, string title, string? content = null, bool enableAlpha = false)
        : base(icon, title, content)
    {
        ColorPicker = new ColorPickerButton(color, title, enableAlpha) { Width = 60 };
        Footer = ColorPicker;
        ColorPicker.ColorChanged += c => ColorChanged?.Invoke(c);
    }

    public void SetColor(Color color) => ColorPicker.SetColor(color);
}

/// <summary>MySettingCard.ColorPickerButton: a rounded swatch that opens a ColorDialog.</summary>
public class ColorPickerButton : Border
{
    private readonly string _title;
    private readonly bool _enableAlpha;

    public Color Color { get; private set; }

    public event Action<Color>? ColorChanged;

    public ColorPickerButton(Color color, string title, bool enableAlpha = false)
    {
        _title = title;
        _enableAlpha = enableAlpha;
        Width = 96;
        Height = 32;
        CornerRadius = new CornerRadius(4);
        BorderThickness = new Thickness(1);
        Cursor = new Cursor(StandardCursorType.Hand);
        VerticalAlignment = VerticalAlignment.Center;
        ActualThemeVariantChanged += (_, _) => UpdateBorder();
        UpdateBorder();
        SetColor(color);
        PointerReleased += async (_, e) =>
        {
            if (e.InitialPressMouseButton != MouseButton.Left) return;
            var chosen = await ColorDialog.ShowAsync(ColorDialog.ToQColorName(Color),
                I18n.Tr("Choose ", "ColorPickerButton") + _title, _enableAlpha);
            if (chosen == null || !App.TryParseQColor(chosen, out var c)) return;
            SetColor(c);
            ColorChanged?.Invoke(c);
        };
    }

    private void UpdateBorder()
    {
        BorderBrush = new SolidColorBrush(ActualThemeVariant == global::Avalonia.Styling.ThemeVariant.Dark
            ? Color.FromArgb(10, 255, 255, 255)
            : Color.FromRgb(234, 234, 234));
    }

    public void SetColor(Color color)
    {
        Color = color;
        Background = new SolidColorBrush(_enableAlpha ? color : Color.FromArgb(255, color.R, color.G, color.B));
    }
}
