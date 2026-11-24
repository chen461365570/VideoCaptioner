using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using FluentAvalonia.UI.Controls;

namespace VideoCaptioner.Avalonia.Services;

/// <summary>Same positions as qfluentwidgets InfoBarPosition.</summary>
public enum ToastPosition { Top, TopLeft, TopRight, Bottom, BottomLeft, BottomRight }

/// <summary>
/// qfluentwidgets InfoBar replacement: InfoBar.success/warning/error/info(title, content, duration, position).
/// duration is in ms; -1 (INFOBAR_DURATION_FOREVER) keeps it until closed.
/// The host panels live in MainWindow (see <see cref="AttachHost"/>).
/// </summary>
public static class Toast
{
    public const int Forever = -1;
    private static readonly Dictionary<ToastPosition, StackPanel> Hosts = new();

    public static void AttachHost(Panel overlay)
    {
        foreach (ToastPosition pos in Enum.GetValues<ToastPosition>())
        {
            var sp = new StackPanel
            {
                Spacing = 8,
                Margin = new Thickness(24, 24, 24, 24),
                MaxWidth = 520,
                HorizontalAlignment = pos switch
                {
                    ToastPosition.TopLeft or ToastPosition.BottomLeft => HorizontalAlignment.Left,
                    ToastPosition.TopRight or ToastPosition.BottomRight => HorizontalAlignment.Right,
                    _ => HorizontalAlignment.Center,
                },
                VerticalAlignment = pos is ToastPosition.Bottom or ToastPosition.BottomLeft or ToastPosition.BottomRight
                    ? VerticalAlignment.Bottom : VerticalAlignment.Top,
            };
            overlay.Children.Add(sp);
            Hosts[pos] = sp;
        }
    }

    public static InfoBar? Success(string title, string content = "", int duration = 2000, ToastPosition position = ToastPosition.TopRight, bool closable = true)
        => Show(InfoBarSeverity.Success, title, content, duration, position, closable);

    public static InfoBar? Warning(string title, string content = "", int duration = 2000, ToastPosition position = ToastPosition.TopRight, bool closable = true)
        => Show(InfoBarSeverity.Warning, title, content, duration, position, closable);

    public static InfoBar? Error(string title, string content = "", int duration = 2000, ToastPosition position = ToastPosition.TopRight, bool closable = true)
        => Show(InfoBarSeverity.Error, title, content, duration, position, closable);

    public static InfoBar? Info(string title, string content = "", int duration = 2000, ToastPosition position = ToastPosition.TopRight, bool closable = true)
        => Show(InfoBarSeverity.Informational, title, content, duration, position, closable);

    public static InfoBar? Show(InfoBarSeverity severity, string title, string content, int duration, ToastPosition position, bool closable)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => Show(severity, title, content, duration, position, closable));
            return null;
        }
        if (!Hosts.TryGetValue(position, out var host)) return null;

        var bar = new InfoBar
        {
            Severity = severity,
            Title = title,
            Message = content,
            IsClosable = closable,
            IsOpen = true,
            MinWidth = 280,
        };
        bar.Closed += (_, _) => host.Children.Remove(bar);
        if (position is ToastPosition.Bottom or ToastPosition.BottomLeft or ToastPosition.BottomRight)
            host.Children.Add(bar);
        else
            host.Children.Insert(0, bar);

        if (duration >= 0)
            DispatcherTimer.RunOnce(() => { bar.IsOpen = false; host.Children.Remove(bar); }, TimeSpan.FromMilliseconds(duration));
        return bar;
    }

    /// <summary>Close a persistent toast returned from Show/Success/...</summary>
    public static void Close(InfoBar? bar)
    {
        if (bar?.Parent is Panel p) p.Children.Remove(bar);
    }
}
