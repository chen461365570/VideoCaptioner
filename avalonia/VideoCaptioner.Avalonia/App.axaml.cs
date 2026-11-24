using System.Globalization;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;
using FluentAvalonia.Styling;
using VideoCaptioner.Avalonia.Services;
using VideoCaptioner.Avalonia.Views;

namespace VideoCaptioner.Avalonia;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        Cfg.Load();
        I18n.Init(Cfg.Language.Value);
        ApplyTheme();
        Cfg.ThemeMode.ValueChanged += _ => ApplyTheme();
        Cfg.ThemeColor.ValueChanged += _ => ApplyTheme();

        Bridge.Instance.Start();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
            desktop.ShutdownRequested += (_, _) => Bridge.Instance.Dispose();
            desktop.Exit += (_, _) => Bridge.Instance.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>qfluentwidgets default theme colour written by the Python app.</summary>
    public const string DefaultThemeColor = "#ff28f08b";

    /// <summary>Apply qfluentwidgets ThemeMode / ThemeColor (#AARRGGBB).</summary>
    public static void ApplyTheme()
    {
        var app = Current!;
        app.RequestedThemeVariant = Cfg.ThemeMode.Value switch
        {
            "Light" => ThemeVariant.Light,
            "Dark" => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
        var fa = app.Styles.OfType<FluentAvaloniaTheme>().FirstOrDefault();
        if (fa != null)
        {
            fa.PreferSystemTheme = Cfg.ThemeMode.Value == "Auto";
            // Windows 11 look: use the Windows accent colour unless the user picked a custom
            // theme colour (#ff28f08b is the value the Python app always forces as default).
            var custom = !string.Equals(Cfg.ThemeColor.Value, DefaultThemeColor, StringComparison.OrdinalIgnoreCase)
                         && TryParseQColor(Cfg.ThemeColor.Value, out _);
            fa.PreferUserAccentColor = !custom;
            if (custom && TryParseQColor(Cfg.ThemeColor.Value, out var c)) fa.CustomAccentColor = c;
            else fa.CustomAccentColor = null;
        }
    }

    /// <summary>Parse QColor names: #RRGGBB or #AARRGGBB.</summary>
    public static bool TryParseQColor(string? s, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(s)) return false;
        var hex = s.TrimStart('#');
        if (hex.Length == 8 && uint.TryParse(hex, NumberStyles.HexNumber, null, out var argb))
        {
            color = Color.FromUInt32(argb);
            return true;
        }
        return Color.TryParse(s, out color);
    }
}
