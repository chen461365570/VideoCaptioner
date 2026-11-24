using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;

namespace VideoCaptioner.Avalonia.Controls;

/// <summary>
/// qfluentwidgets SegmentedWidget: horizontal pivot of text items with a selected pill.
/// Usage: AddItem(routeKey, text, onClick); SetCurrentItem(routeKey). Styled in Styles/Controls.axaml.
/// </summary>
public class SegmentedWidget : TemplatedControl
{
    private readonly StackPanel _panel = new() { Orientation = Orientation.Horizontal, Spacing = 0 };
    private readonly Dictionary<string, ToggleButton> _items = new();
    private readonly List<string> _order = [];

    public event Action<string>? CurrentItemChanged;
    public string? CurrentRouteKey { get; private set; }

    public SegmentedWidget()
    {
        Template = new FuncControlTemplate<SegmentedWidget>((_, _) =>
            new Border
            {
                Classes = { "segmented" },
                Child = _panel,
            });
        HorizontalAlignment = HorizontalAlignment.Left;
    }

    public void AddItem(string routeKey, string text, Action? onClick = null)
    {
        var btn = new ToggleButton { Content = text, Classes = { "segment" } };
        btn.Click += (_, _) =>
        {
            SetCurrentItem(routeKey);
            onClick?.Invoke();
        };
        _items[routeKey] = btn;
        _order.Add(routeKey);
        _panel.Children.Add(btn);
        if (CurrentRouteKey == null) SetCurrentItem(routeKey);
    }

    public void SetCurrentItem(string routeKey)
    {
        if (!_items.ContainsKey(routeKey)) return;
        foreach (var (k, b) in _items) b.IsChecked = k == routeKey;
        if (CurrentRouteKey == routeKey) return;
        CurrentRouteKey = routeKey;
        CurrentItemChanged?.Invoke(routeKey);
    }
}
