using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using FluentAvalonia.UI.Controls;

namespace VideoCaptioner.Avalonia.Views.Components;

/// <summary>
/// The model table used by WhisperCppDownloadDialog / FasterWhisperDownloadDialog, in a Windows 11 card:
/// quiet 12px header, rows separated by dividers, a status badge (success check for downloaded models)
/// and a subtle download button per row. Columns: 模型名称 | 大小 | 状态 | 操作; 6 rows visible, then it scrolls.
/// </summary>
public class ModelDownloadTable : Border
{
    // style as a Border so the shared Border.card style (card fill, stroke, radius 8) applies
    protected override Type StyleKeyOverride => typeof(Border);

    private const double RowHeight = 48;
    private const double HeaderHeight = 36;
    private const string Columns = "*,110,110,140";
    private readonly StackPanel _rows = new();
    private readonly List<(Border Badge, SymbolIcon Icon, TextBlock Status, Button Button)> _cells = [];

    public event Action<int>? DownloadClicked;

    public ModelDownloadTable(string nameHeader, string sizeHeader, string statusHeader, string actionHeader)
    {
        Classes.Add("card");
        Padding = new Thickness(0);
        ClipToBounds = true;
        MaxHeight = HeaderHeight + RowHeight * 6 + 2;

        var header = MakeRow(HeaderHeight);
        AddCell(header, 0, HeaderText(nameHeader));
        AddCell(header, 1, HeaderText(sizeHeader));
        AddCell(header, 2, HeaderText(statusHeader));
        AddCell(header, 3, HeaderText(actionHeader), HorizontalAlignment.Right);

        var headerBorder = new Border { Child = header, BorderThickness = new Thickness(0, 0, 0, 1) };
        headerBorder.Bind(Border.BorderBrushProperty, headerBorder.GetResourceObservable("DividerStrokeColorDefaultBrush"));

        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        grid.Children.Add(headerBorder);
        var scroll = new ScrollViewer { Content = _rows };
        Grid.SetRow(scroll, 1);
        grid.Children.Add(scroll);
        Child = grid;
    }

    private static TextBlock HeaderText(string text) => new()
    {
        Text = text,
        FontSize = 12,
        FontWeight = FontWeight.SemiBold,
        Classes = { "caption" },
    };

    private static Grid MakeRow(double height) => new()
    {
        Height = height,
        Margin = new Thickness(16, 0, 12, 0),
        ColumnDefinitions = new ColumnDefinitions(Columns),
    };

    private static void AddCell(Grid row, int col, Control c, HorizontalAlignment align = HorizontalAlignment.Left)
    {
        c.HorizontalAlignment = align;
        c.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(c, col);
        row.Children.Add(c);
    }

    /// <summary>Add a row; returns its index.</summary>
    public int AddRow(string name, string size, string status, bool downloaded, string buttonText)
    {
        var index = _cells.Count;
        var row = MakeRow(RowHeight);
        AddCell(row, 0, new TextBlock { Text = name, TextTrimming = TextTrimming.CharacterEllipsis });
        AddCell(row, 1, new TextBlock { Text = size, Classes = { "caption" }, FontSize = 14 });

        var statusIcon = new SymbolIcon { Symbol = Symbol.Checkmark, FontSize = 12 };
        var statusText = new TextBlock { Text = status, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        var badge = new Border
        {
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 2),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                Children = { statusIcon, statusText },
            },
        };
        AddCell(row, 2, badge);

        var btn = new Button
        {
            Theme = Application.Current?.FindResource("TransparentButton") as global::Avalonia.Styling.ControlTheme,
            Padding = new Thickness(10, 5),
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children =
                {
                    new SymbolIcon { Symbol = Symbol.Download, FontSize = 16 },
                    new TextBlock { Text = buttonText, VerticalAlignment = VerticalAlignment.Center },
                },
            },
        };
        btn.Click += (_, _) => DownloadClicked?.Invoke(index);
        AddCell(row, 3, btn, HorizontalAlignment.Right);

        _cells.Add((badge, statusIcon, statusText, btn));
        SetStatus(index, status, downloaded);

        var rowBorder = new Border { Child = row, BorderThickness = new Thickness(0, 0, 0, 1) };
        rowBorder.Bind(Border.BorderBrushProperty, rowBorder.GetResourceObservable("DividerStrokeColorDefaultBrush"));
        if (_rows.Children.Count > 0 && _rows.Children[^1] is Border prev) prev.BorderThickness = new Thickness(0, 0, 0, 1);
        rowBorder.BorderThickness = new Thickness(0);
        _rows.Children.Add(rowBorder);
        return index;
    }

    public void SetStatus(int row, string status, bool downloaded)
    {
        var (badge, icon, text, _) = _cells[row];
        text.Text = status;
        icon.IsVisible = downloaded;
        if (downloaded)
        {
            badge.Bind(BackgroundProperty, badge.GetResourceObservable("SystemFillColorSuccessBackgroundBrush"));
            text.Bind(TextBlock.ForegroundProperty, text.GetResourceObservable("SystemFillColorSuccessBrush"));
            icon.Bind(SymbolIcon.ForegroundProperty, icon.GetResourceObservable("SystemFillColorSuccessBrush"));
        }
        else
        {
            badge.Bind(BackgroundProperty, badge.GetResourceObservable("SubtleFillColorSecondaryBrush"));
            text.Bind(TextBlock.ForegroundProperty, text.GetResourceObservable("TextFillColorSecondaryBrush"));
        }
    }

    public void SetButtonText(int row, string text)
    {
        if (_cells[row].Button.Content is StackPanel sp && sp.Children[1] is TextBlock tb) tb.Text = text;
    }

    public void SetButtonEnabled(int row, bool enabled) => _cells[row].Button.IsEnabled = enabled;

    public void SetAllButtonsEnabled(bool enabled)
    {
        foreach (var c in _cells) c.Button.IsEnabled = enabled;
    }

    public int RowCount => _cells.Count;
}
