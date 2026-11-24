using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Controls.Templates;
using Avalonia.Layout;

namespace VideoCaptioner.Avalonia.Controls;

/// <summary>
/// Workflow stepper for the home page: numbered steps joined by connector lines
/// (① 任务创建 ─ ② 语音转录 ─ ③ 字幕优化与翻译 ─ ④ 字幕视频合成).
/// Same API as SegmentedWidget: AddItem(routeKey, text, onClick); SetCurrentItem(routeKey).
/// Every step stays clickable. Styled in Styles/Controls.axaml (ToggleButton.step, Border.stepDot, Rectangle.stepLine).
/// </summary>
public class StepBar : TemplatedControl
{
    private readonly StackPanel _panel = new() { Orientation = Orientation.Horizontal, Spacing = 0 };
    private readonly List<(string Key, ToggleButton Button, Rectangle? Line)> _items = [];

    public event Action<string>? CurrentItemChanged;
    public string? CurrentRouteKey { get; private set; }

    public StepBar()
    {
        Template = new FuncControlTemplate<StepBar>((_, _) => _panel);
        HorizontalAlignment = HorizontalAlignment.Left;
    }

    public void AddItem(string routeKey, string text, Action? onClick = null)
    {
        // connector between the previous step and this one
        Rectangle? line = null;
        if (_items.Count > 0)
        {
            line = new Rectangle { Classes = { "stepLine" } };
            _panel.Children.Add(line);
        }

        var btn = new ToggleButton
        {
            Classes = { "step" },
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                Children =
                {
                    new Border
                    {
                        Classes = { "stepDot" },
                        Child = new TextBlock { Text = (_items.Count + 1).ToString() },
                    },
                    new TextBlock { Classes = { "stepLabel" }, Text = text, VerticalAlignment = VerticalAlignment.Center },
                },
            },
        };
        btn.Click += (_, _) =>
        {
            SetCurrentItem(routeKey);
            onClick?.Invoke();
        };
        _items.Add((routeKey, btn, line));
        _panel.Children.Add(btn);
        if (CurrentRouteKey == null) SetCurrentItem(routeKey);
        else Refresh();
    }

    public void SetCurrentItem(string routeKey)
    {
        if (_items.FindIndex(i => i.Key == routeKey) < 0) return;
        var changed = CurrentRouteKey != routeKey;
        CurrentRouteKey = routeKey;
        Refresh();
        if (changed) CurrentItemChanged?.Invoke(routeKey);
    }

    /// <summary>Steps before the current one are shown as passed (accent outline and connector).</summary>
    private void Refresh()
    {
        var current = _items.FindIndex(i => i.Key == CurrentRouteKey);
        for (var i = 0; i < _items.Count; i++)
        {
            var (_, btn, line) = _items[i];
            btn.IsChecked = i == current;
            btn.Classes.Set("passed", i < current);
            line?.Classes.Set("passed", i <= current);
        }
    }
}
