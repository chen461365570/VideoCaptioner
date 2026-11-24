using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using FluentAvalonia.UI.Controls;
using FluentAvalonia.UI.Windowing;
using VideoCaptioner.Avalonia.Services;
using VideoCaptioner.Avalonia.Views.Components;

namespace VideoCaptioner.Avalonia.Views;

/// <summary>Port of videocaptioner/ui/view/main_window.py (qfluentwidgets FluentWindow).</summary>
public partial class MainWindow : AppWindow
{
    private const string DefaultTitle = "卡卡字幕助手 -- VideoCaptioner";

    public HomeInterface HomeInterface { get; }
    public BatchProcessInterface BatchProcessInterface { get; }
    public SubtitleStyleInterface SubtitleStyleInterface { get; }
    public LlmLogsInterface LlmLogsInterface { get; }
    public SettingInterface SettingInterface { get; }

    private readonly Dictionary<string, Control> _pages = new();

    public MainWindow()
    {
        InitializeComponent();
        PropertyChanged += (_, e) =>
        {
            if (e.Property == TitleProperty) TitleText.Text = Title;
        };
        Title = I18n.Tr(DefaultTitle);
        TitleText.Text = Title;

        TitleBar.ExtendsContentIntoTitleBar = true;
        TitleBar.TitleBarHitTestType = TitleBarHitTestType.Complex;
        SplashScreen = new MainSplashScreen();
        ApplyBackdrop();

        Toast.AttachHost(ToastHost);

        HomeInterface = new HomeInterface();
        BatchProcessInterface = new BatchProcessInterface();
        SubtitleStyleInterface = new SubtitleStyleInterface();
        LlmLogsInterface = new LlmLogsInterface();
        SettingInterface = new SettingInterface();

        AddSubInterface("home", HomeInterface);
        AddSubInterface("batch", BatchProcessInterface);
        AddSubInterface("style", SubtitleStyleInterface);
        AddSubInterface("logs", LlmLogsInterface);
        AddSubInterface("settings", SettingInterface);

        NavView.ItemInvoked += OnItemInvoked;
        NavView.SelectionChanged += (_, e) =>
        {
            if (e.SelectedItemContainer is NavigationViewItem { Tag: string tag } && tag != "github") SwitchTo(tag);
        };
        NavView.SelectedItem = NavView.MenuItems.OfType<NavigationViewItem>().First();
        SwitchTo("home");
        // Development hook for screenshots: VC_START_PAGE=home|batch|style|logs|settings,
        // VC_HOME_TAB=TaskCreationInterface|TranscriptionInterface|SubtitleInterface|VideoSynthesisInterface
        if (Environment.GetEnvironmentVariable("VC_START_PAGE") is { Length: > 0 } startPage) SwitchTo(startPage);
        if (Environment.GetEnvironmentVariable("VC_HOME_TAB") is { Length: > 0 } homeTab) HomeInterface.ShowTab(homeTab);

        Opened += async (_, _) =>
        {
            // Fit the default size to the screen (high DPI laptops have little logical height).
            if (Screens.ScreenFromWindow(this) is { } screen)
            {
                var area = screen.WorkingArea;
                double w = Math.Min(Width, area.Width / screen.Scaling - 32), h = Math.Min(Height, area.Height / screen.Scaling - 32);
                if (w != Width || h != Height)
                {
                    Width = w; Height = h;
                    Position = new PixelPoint(area.X + (int)((area.Width - w * screen.Scaling) / 2), area.Y + (int)((area.Height - h * screen.Scaling) / 2));
                }
            }
            await CheckFfmpegAsync();
            StartVersionCheck();
        };
        Bridge.Instance.Exited += code =>
            Toast.Error(I18n.Tr("后端已退出"), $"Python backend exited (code {code})", Toast.Forever, ToastPosition.Bottom);
    }



    private void AddSubInterface(string key, Control page)
    {
        page.IsVisible = false;
        _pages[key] = page;
        PageHost.Children.Add(page);
    }

    private void OnItemInvoked(object? sender, NavigationViewItemInvokedEventArgs e)
    {
        if (e.InvokedItemContainer is not NavigationViewItem item) return;
        var tag = item.Tag as string;
        if (tag == "github")
        {
            _ = OnGithubDialogAsync();
            return;
        }
        if (tag != null) SwitchTo(tag);
    }

    /// <summary>Show a sub interface (routeKey = home / batch / style / logs / settings).</summary>
    public void SwitchTo(string key)
    {
        if (!_pages.TryGetValue(key, out var page)) return;
        foreach (var p in _pages.Values) p.IsVisible = ReferenceEquals(p, page);
        var navItem = NavView.MenuItems.Concat(NavView.FooterMenuItems)
            .OfType<NavigationViewItem>().FirstOrDefault(i => (string?)i.Tag == key);
        if (navItem != null && !ReferenceEquals(NavView.SelectedItem, navItem)) NavView.SelectedItem = navItem;
        // Interfaces may define their own window title (QWidget.windowTitle()).
        var title = (page as IWindowTitleProvider)?.WindowTitle;
        Title = string.IsNullOrEmpty(title) ? I18n.Tr(DefaultTitle) : title;
    }

    /// <summary>
    /// Windows 11 backdrop: Mica behind the title bar and navigation pane, pages sit on
    /// NavigationView's rounded content layer. Falls back to the solid theme background.
    /// </summary>
    private void ApplyBackdrop()
    {
        TitleBar.BackgroundColor = Colors.Transparent;
        TitleBar.InactiveBackgroundColor = Colors.Transparent;
        TitleBar.ButtonBackgroundColor = Colors.Transparent;
        TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Mica, WindowTransparencyLevel.None];
        Background = Brushes.Transparent;
        this[!TransparencyBackgroundFallbackProperty] = this.GetResourceObservable("VcWindowBackground").ToBinding();
    }

    private async Task OnGithubDialogAsync()
    {
        var ok = await Dialogs.MessageBoxAsync(
            I18n.Tr("GitHub信息"),
            I18n.Tr("VideoCaptioner 由本人在课余时间独立开发完成，目前托管在GitHub上，欢迎Star和Fork。项目诚然还有很多地方需要完善，遇到软件的问题或者BUG欢迎提交Issue。\n\n https://github.com/WEIFENG2333/VideoCaptioner"),
            I18n.Tr("打开 GitHub"),
            I18n.Tr("支持作者"));
        if (ok)
            Dialogs.OpenUrl("https://github.com/WEIFENG2333/VideoCaptioner");
        else
            await new DonateDialog().ShowAsync();
    }

    private void StartVersionCheck()
    {
        var job = Bridge.Instance.Run("version_check");
        job.Event += async (name, data) =>
        {
            if (name == "new_version" && data != null)
            {
                await OnNewVersionAsync(
                    data["version"]?.GetValue<string>() ?? "",
                    data["update_required"]?.GetValue<bool>() ?? false,
                    data["update_info"]?.GetValue<string>() ?? "",
                    data["download_url"]?.GetValue<string>() ?? "");
            }
            else if (name == "announcement" && data != null)
            {
                await Dialogs.MessageBoxAsync("公告", data["content"]?.GetValue<string>() ?? "", "我知道了", null);
            }
        };
    }

    private async Task OnNewVersionAsync(string version, bool updateRequired, string updateInfo, string downloadUrl)
    {
        string title, content;
        if (updateRequired)
        {
            title = "发现新版本, 需要更新";
            content = $"发现新版本 {version}\n\n更新内容：\n{updateInfo}";
        }
        else
        {
            title = "发现新版本";
            content = $"发现新版本 {version}\n\n{updateInfo}";
        }

        var ok = await Dialogs.MessageBoxAsync(title, content, "立即更新", "稍后再说");
        if (ok || updateRequired) Dialogs.OpenUrl(downloadUrl);

        if (updateRequired)
        {
            HomeInterface.IsEnabled = false;
            BatchProcessInterface.IsEnabled = false;
            Toast.Error("需要更新", I18n.Tr("当前版本部分功能已被禁用。请尽快更新。"), Toast.Forever, ToastPosition.Bottom, closable: false);
        }
    }

    private async Task CheckFfmpegAsync()
    {
        try
        {
            var ok = await Bridge.Instance.RequestAsync("check_ffmpeg");
            if (ok?.GetValue<bool>() == false)
                Toast.Warning(I18n.Tr("FFmpeg 未安装"), I18n.Tr("软件处理音视频文件时需要 FFmpeg，请先安装"),
                    Toast.Forever, ToastPosition.Bottom);
        }
        catch (Exception e)
        {
            Toast.Error(I18n.Tr("后端启动失败"), e.Message, Toast.Forever, ToastPosition.Bottom);
        }
    }
}

/// <summary>Implemented by interfaces that set their own window title.</summary>
public interface IWindowTitleProvider
{
    string? WindowTitle { get; }
}

/// <summary>qfluentwidgets SplashScreen: window icon at 106x106 while the UI initialises.</summary>
internal sealed class MainSplashScreen : IApplicationSplashScreen
{
    public string AppName => "VideoCaptioner";
    public IImage AppIcon { get; } = new Bitmap(AssetLoader.Open(new Uri("avares://VideoCaptioner/Assets/logo.png")));
    public object? SplashScreenContent => new Image { Source = AppIcon, Width = 106, Height = 106 };
    public int MinimumShowTime => 600;

    public Task RunTasks(CancellationToken cancellationToken) =>
        Task.WhenAny(Bridge.Instance.Ready, Task.Delay(4000, cancellationToken));
}
