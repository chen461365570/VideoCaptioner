using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using FluentAvalonia.UI.Controls;
using VideoCaptioner.Avalonia.Services;

namespace VideoCaptioner.Avalonia.Views.Style;

/// <summary>
/// Port of MySettingCard.ColorPickerButton, drawn as a Windows 11 colour button: a standard button holding
/// a rounded swatch (checkerboard behind it when alpha is enabled) and the hex value. Clicking opens a
/// colour dialog ("Choose " + title) and raises <see cref="ColorChanged"/> when accepted.
/// </summary>
public class StyleColorButton : Button
{
    protected override Type StyleKeyOverride => typeof(Button);

    private readonly Border _swatch;
    private readonly Border _checker;
    private readonly TextBlock _value;
    private Color _color;

    public StyleColorButton()
    {
        MinWidth = 120;
        Padding = new Thickness(6, 5, 12, 5);
        HorizontalContentAlignment = global::Avalonia.Layout.HorizontalAlignment.Left;
        VerticalContentAlignment = global::Avalonia.Layout.VerticalAlignment.Center;

        _checker = new Border { CornerRadius = new CornerRadius(4), Background = CheckerBrush(), IsVisible = false };
        _swatch = new Border { CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1) };
        _swatch[!Border.BorderBrushProperty] = new DynamicResourceExtension("ControlStrongStrokeColorDefaultBrush");
        _value = new TextBlock
        {
            FontSize = 12,
            FontFamily = new FontFamily("Cascadia Mono, Consolas, Segoe UI Variable"),
            VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center,
        };
        var swatchHost = new Panel { Width = 32, Height = 20, Children = { _checker, _swatch } };
        Content = new StackPanel
        {
            Orientation = global::Avalonia.Layout.Orientation.Horizontal,
            Spacing = 10,
            Children = { swatchHost, _value },
        };
        UpdateSwatch();
        Click += async (_, _) => await ShowColorDialogAsync();
    }

    /// <summary>8px light/grey checkerboard, shown behind translucent colours.</summary>
    private static IBrush CheckerBrush()
    {
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing { Brush = Brushes.White, Geometry = new RectangleGeometry(new Rect(0, 0, 8, 8)) });
        group.Children.Add(new GeometryDrawing { Brush = new SolidColorBrush(Color.FromRgb(204, 204, 204)), Geometry = new RectangleGeometry(new Rect(0, 0, 4, 4)) });
        group.Children.Add(new GeometryDrawing { Brush = new SolidColorBrush(Color.FromRgb(204, 204, 204)), Geometry = new RectangleGeometry(new Rect(4, 4, 4, 4)) });
        return new DrawingBrush(group)
        {
            TileMode = TileMode.Tile,
            Stretch = Stretch.None,
            SourceRect = new RelativeRect(0, 0, 8, 8, RelativeUnit.Absolute),
            DestinationRect = new RelativeRect(0, 0, 8, 8, RelativeUnit.Absolute),
        };
    }

    /// <summary>Card title, used for the dialog title ("Choose " + title).</summary>
    public string Title
    {
        get => _title;
        set
        {
            _title = value;
            global::Avalonia.Automation.AutomationProperties.SetName(this, value);
        }
    }

    private string _title = "";

    /// <summary>ColorSettingCard(enableAlpha=...).</summary>
    public bool EnableAlpha
    {
        get => _enableAlpha;
        set
        {
            _enableAlpha = value;
            UpdateSwatch();
        }
    }

    private bool _enableAlpha;

    public Color Color
    {
        get => _color;
        set
        {
            _color = value;
            UpdateSwatch();
        }
    }

    /// <summary>Raised when the user accepts a colour in the dialog.</summary>
    public event Action<Color>? ColorChanged;

    private void UpdateSwatch()
    {
        var c = EnableAlpha ? _color : Color.FromArgb(255, _color.R, _color.G, _color.B);
        _swatch.Background = new SolidColorBrush(c);
        _checker.IsVisible = c.A < 255;
        var hex = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        _value.Text = EnableAlpha ? $"{hex}  {Math.Round(c.A * 100 / 255.0)}%" : hex;
    }

    private async Task ShowColorDialogAsync()
    {
        var picker = new ColorPickerPanel(EnableAlpha ? _color : Color.FromArgb(255, _color.R, _color.G, _color.B), EnableAlpha);
        var dlg = new ContentDialog
        {
            Title = I18n.Tr("Choose ", "ColorPickerButton") + Title,
            Content = picker,
            PrimaryButtonText = I18n.Tr("确定"),
            CloseButtonText = I18n.Tr("取消"),
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        Color = picker.Color;
        ColorChanged?.Invoke(_color);
    }

    /// <summary>
    /// WinUI 3 ColorPicker layout: hue/saturation spectrum, value slider, optional opacity slider,
    /// then a preview swatch next to a hex input.
    /// </summary>
    private sealed class ColorPickerPanel : StackPanel
    {
        private readonly ColorSpectrum _spectrum;
        private readonly ColorSlider _valueSlider;
        private readonly ColorSlider? _alphaSlider;
        private readonly Border _preview;
        private readonly TextBox _hex;
        private readonly bool _alpha;
        private bool _syncing;

        public Color Color { get; private set; }

        public ColorPickerPanel(Color initial, bool alpha)
        {
            _alpha = alpha;
            Color = initial;
            Spacing = 12;
            Width = 296;

            var hsv = initial.ToHsv();
            _spectrum = new ColorSpectrum
            {
                Height = 296,
                Shape = ColorSpectrumShape.Box,
                Components = ColorSpectrumComponents.HueSaturation,
                CornerRadius = new CornerRadius(4),
                HsvColor = hsv,
            };
            _valueSlider = NewSlider(ColorComponent.Component3, hsv);
            Children.Add(_spectrum);
            Children.Add(_valueSlider);
            if (alpha)
            {
                _alphaSlider = NewSlider(ColorComponent.Alpha, hsv);
                Children.Add(_alphaSlider);
            }

            _preview = new Border { CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1) };
            _preview[!Border.BorderBrushProperty] = new DynamicResourceExtension("ControlStrongStrokeColorDefaultBrush");
            var previewHost = new Panel
            {
                Width = 56, Height = 32,
                Children = { new Border { CornerRadius = new CornerRadius(4), Background = CheckerBrush() }, _preview },
            };
            _hex = new TextBox { Watermark = alpha ? "#RRGGBBAA" : "#RRGGBB", VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center };
            _hex.LostFocus += (_, _) => ApplyHex();
            _hex.KeyDown += (_, e) =>
            {
                if (e.Key != Key.Enter) return;
                ApplyHex();
                e.Handled = true;
            };
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,12,*"), Margin = new Thickness(0, 4, 0, 0) };
            row.Children.Add(previewHost);
            Grid.SetColumn(_hex, 2);
            row.Children.Add(_hex);
            Children.Add(row);

            _spectrum.ColorChanged += (_, _) => Sync(_spectrum.HsvColor);
            _valueSlider.ColorChanged += (_, _) => Sync(_valueSlider.HsvColor);
            if (_alphaSlider != null) _alphaSlider.ColorChanged += (_, _) => Sync(_alphaSlider.HsvColor);
            UpdateOutputs(initial);
        }

        private ColorSlider NewSlider(ColorComponent component, HsvColor hsv) => new()
        {
            Orientation = global::Avalonia.Layout.Orientation.Horizontal,
            ColorModel = ColorModel.Hsva,
            ColorComponent = component,
            IsAlphaVisible = component == ColorComponent.Alpha,
            HsvColor = hsv,
        };

        private void Sync(HsvColor hsv)
        {
            if (_syncing) return;
            _syncing = true;
            try
            {
                if (!_alpha) hsv = new HsvColor(1, hsv.H, hsv.S, hsv.V);
                _spectrum.HsvColor = hsv;
                _valueSlider.HsvColor = hsv;
                if (_alphaSlider != null) _alphaSlider.HsvColor = hsv;
                UpdateOutputs(hsv.ToRgb());
            }
            finally
            {
                _syncing = false;
            }
        }

        private void UpdateOutputs(Color c)
        {
            Color = c;
            _preview.Background = new SolidColorBrush(c);
            _hex.Text = _alpha ? RgbaHex(c).ToUpperInvariant() : ColorName(c).ToUpperInvariant();
        }

        private void ApplyHex()
        {
            var text = (_hex.Text ?? "").Trim();
            if (!text.StartsWith('#')) text = "#" + text;
            Color c;
            if (_alpha && text.Length == 9) c = ParseRgbaHex(text);
            else if (text.Length is 7 or 4 && global::Avalonia.Media.Color.TryParse(text, out var parsed)) c = parsed;
            else
            {
                UpdateOutputs(Color); // invalid input: show the current value again
                return;
            }
            if (!_alpha) c = global::Avalonia.Media.Color.FromArgb(255, c.R, c.G, c.B);
            Sync(c.ToHsv());
        }
    }

    // ---- helpers shared with the page (QColor semantics) ----

    /// <summary>QColor.name(): "#rrggbb" (lower case).</summary>
    public static string ColorName(Color c) => $"#{c.R:x2}{c.G:x2}{c.B:x2}";

    /// <summary>"#rrggbbaa" as written by the rounded background cards.</summary>
    public static string RgbaHex(Color c) => $"#{c.R:x2}{c.G:x2}{c.B:x2}{c.A:x2}";

    /// <summary>QColor(str): #rgb / #rrggbb / #aarrggbb / named colours. Invalid → black like an invalid QColor painted.</summary>
    public static Color ParseQColor(string? s)
    {
        if (!string.IsNullOrWhiteSpace(s) && Color.TryParse(s.Trim(), out var c)) return c;
        return Colors.Black;
    }

    /// <summary>SubtitleStyleInterface._parseRgbaHex: #RRGGBBAA / #RRGGBB, else (25, 25, 25, 200).</summary>
    public static Color ParseRgbaHex(string? hex)
    {
        var h = (hex ?? "").TrimStart('#');
        try
        {
            if (h.Length == 8)
                return Color.FromArgb(Convert.ToByte(h[6..8], 16), Convert.ToByte(h[0..2], 16),
                    Convert.ToByte(h[2..4], 16), Convert.ToByte(h[4..6], 16));
            if (h.Length == 6) return ParseQColor("#" + h);
        }
        catch (FormatException) { }
        return Color.FromArgb(200, 25, 25, 25);
    }
}
