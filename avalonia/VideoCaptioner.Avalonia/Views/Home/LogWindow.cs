using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;
using VideoCaptioner.Avalonia.Services;

namespace VideoCaptioner.Avalonia.Views.Home;

/// <summary>Port of videocaptioner/ui/view/log_window.py (日志查看器): tails LOG_PATH/app.log every 500 ms.</summary>
public class LogWindow : Window
{
    private readonly TextBox _logText;
    private readonly DispatcherTimer _timer;
    private readonly string _logPath;
    private FileStream? _logFile;
    private readonly Decoder _decoder = new UTF8Encoding(false).GetDecoder();
    private long _lastPosition;
    private bool _autoScroll = true;
    private ScrollViewer? _scroll;

    public LogWindow()
    {
        Title = "日志查看器";
        Styles.Add(new global::Avalonia.Styling.Style(x => x.OfType<TextBox>().Class("logText").Template().OfType<Border>())
        {
            Setters =
            {
                new global::Avalonia.Styling.Setter(Border.BackgroundProperty, Brushes.Transparent),
                new global::Avalonia.Styling.Setter(Border.BorderThicknessProperty, new Thickness(0)),
            },
        });
        Width = 800;
        Height = 600;
        Icon = Dialogs.MainWindow?.Icon;
        // solid Windows 11 window background (the main window's Mica brush is transparent outside the main window)
        this.Bind(BackgroundProperty, this.GetResourceObservable("SolidBackgroundFillColorBaseBrush"));

        // 创建主布局 (Windows 11: header with title, log path and command; log text in a card)
        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), RowSpacing = 16, Margin = new Thickness(24, 20, 24, 24) };

        // 创建文本编辑器用于显示日志
        _logText = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalContentAlignment = VerticalAlignment.Top,
            FontFamily = new FontFamily("Cascadia Mono, Consolas, Microsoft YaHei UI"),
            FontSize = 12,
            Padding = new Thickness(12, 10),
            Classes = { "logText" },
        };
        // 获取日志文件路径并打开文件
        var logDir = Bridge.Instance.Paths.TryGetValue("LOG_PATH", out var p) && p.Length > 0
            ? p : Path.Combine(Cfg.AppDataPath, "logs");
        _logPath = Path.Combine(logDir, "app.log");
        // 顶部：标题 + 日志路径 + 打开日志文件夹按钮
        var openFolderBtn = new Button
        {
            VerticalAlignment = VerticalAlignment.Center,
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children =
                {
                    new SymbolIcon { Symbol = Symbol.OpenFolder, FontSize = 16 },
                    new TextBlock { Text = "打开日志文件夹", VerticalAlignment = VerticalAlignment.Center },
                },
            },
        };
        openFolderBtn.Click += (_, _) => OpenLogFolder();
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 16 };
        header.Children.Add(new StackPanel
        {
            Spacing = 2,
            Children =
            {
                new TextBlock { Text = "日志查看器", Classes = { "subtitle" } },
                new TextBlock { Text = _logPath, Classes = { "caption" }, TextTrimming = TextTrimming.PathSegmentEllipsis },
            },
        });
        Grid.SetColumn(openFolderBtn, 1);
        header.Children.Add(openFolderBtn);
        layout.Children.Add(header);

        var logCard = new Border { Classes = { "card" }, Padding = new Thickness(0), ClipToBounds = true, Child = _logText };
        Grid.SetRow(logCard, 1);
        layout.Children.Add(logCard);
        Content = layout;

        try
        {
            _logFile = new FileStream(_logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            LoadLastLines(20480);
            _logText.Text += $"\n{new string('=', 25)}以上是历史日志{new string('=', 25)}\n\n";
        }
        catch (Exception e)
        {
            _logFile = null;
            _logText.Text = $"打开日志文件失败: {e.Message}";
        }

        // 设置定时器用于更新日志
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += (_, _) => UpdateLog();
        _timer.Start();

        Opened += (_, _) =>
        {
            // 监听滚动条变化
            _scroll = _logText.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
            if (_scroll != null) _scroll.ScrollChanged += (_, _) => OnScrollChanged();
            ScrollToEnd();
        };
        Closed += (_, _) =>
        {
            _timer.Stop();
            _logFile?.Dispose();
        };
    }

    private string ReadToEnd()
    {
        var bytes = new byte[_logFile!.Length - _logFile.Position];
        var read = 0;
        while (read < bytes.Length)
        {
            var n = _logFile.Read(bytes, read, bytes.Length - read);
            if (n <= 0) break;
            read += n;
        }
        var chars = new char[_decoder.GetCharCount(bytes, 0, read)];
        _decoder.GetChars(bytes, 0, read, chars, 0);
        return new string(chars);
    }

    /// <summary>加载文件最后的内容 (read_size 个字符)</summary>
    private void LoadLastLines(int readSize)
    {
        try
        {
            // 从文件开头读取以确保不会破坏UTF-8编码
            _logFile!.Seek(0, SeekOrigin.Begin);
            var content = ReadToEnd();

            // 只保留最后一部分内容
            if (content.Length > readSize)
            {
                content = content[^readSize..];
                // 找到第一个完整的行
                var newlinePos = content.IndexOf('\n');
                if (newlinePos != -1) content = content[(newlinePos + 1)..];
            }

            _lastPosition = _logFile.Position;
            _logText.Text = content;
        }
        catch (Exception e)
        {
            _logText.Text = $"读取日志文件失败: {e.Message}";
        }
    }

    private void OnScrollChanged()
    {
        if (_scroll == null) return;
        var max = Math.Max(0, _scroll.Extent.Height - _scroll.Viewport.Height);
        var value = _scroll.Offset.Y;
        _autoScroll = value <= max + 0.5 && value >= max * 0.85;
    }

    private void ScrollToEnd()
    {
        _logText.CaretIndex = _logText.Text?.Length ?? 0;
        _scroll?.ScrollToEnd();
    }

    /// <summary>更新日志内容</summary>
    private void UpdateLog()
    {
        if (_logFile == null) return;
        try
        {
            // 文件被轮转/截断时从头读取
            if (_logFile.Length < _lastPosition) _lastPosition = 0;
            _logFile.Seek(_lastPosition, SeekOrigin.Begin);
            var newContent = ReadToEnd();
            if (newContent.Length > 0)
            {
                var keepCaret = _autoScroll ? -1 : _logText.CaretIndex;
                _logText.Text += newContent;
                _lastPosition = _logFile.Position;
                if (_autoScroll) ScrollToEnd();
                else _logText.CaretIndex = keepCaret;
            }
        }
        catch (Exception e)
        {
            _logText.Text = $"读取日志文件出错: {e.Message}";
        }
    }

    /// <summary>打开日志文件所在文件夹 (reveal_in_explorer)</summary>
    private void OpenLogFolder()
    {
        try
        {
            if (OperatingSystem.IsWindows())
                System.Diagnostics.Process.Start("explorer", $"/select,\"{Path.GetFullPath(_logPath)}\"");
            else
                Dialogs.OpenFolder(_logPath);
        }
        catch (Exception e)
        {
            Toast.Error(I18n.Tr("打开失败"), e.Message);
        }
    }
}
