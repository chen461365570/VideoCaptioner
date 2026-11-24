using System.Globalization;
using System.Xml.Linq;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;

namespace VideoCaptioner.Avalonia.Services;

/// <summary>
/// Qt-style translation: source strings are the original Chinese texts passed to self.tr(),
/// translations come from the same Qt Linguist .ts files the Python app ships.
/// Language changes need a restart, exactly like cfg.language (restart=True).
/// </summary>
public static class I18n
{
    private static readonly Dictionary<(string Ctx, string Src), string> ByContext = new();
    private static readonly Dictionary<string, string> BySource = new();

    /// <summary>QLocale name actually in use: zh_CN / zh_HK / en_US.</summary>
    public static string Locale { get; private set; } = "zh_CN";

    public static void Init(string configured)
    {
        Locale = configured == "Auto" ? SystemLocale() : configured;
        ByContext.Clear();
        BySource.Clear();
        var uri = new Uri($"avares://VideoCaptioner/Assets/i18n/VideoCaptioner_{Locale}.ts");
        if (!AssetLoader.Exists(uri)) return;
        using var stream = AssetLoader.Open(uri);
        var doc = XDocument.Load(stream);
        foreach (var ctx in doc.Root!.Elements("context"))
        {
            var name = ctx.Element("name")?.Value ?? "";
            foreach (var m in ctx.Elements("message"))
            {
                var src = m.Element("source")?.Value;
                var tr = m.Element("translation");
                if (src is null || tr is null || tr.Attribute("type")?.Value is "unfinished" or "obsolete" || tr.Value.Length == 0)
                    continue;
                ByContext[(name, src)] = tr.Value;
                BySource.TryAdd(src, tr.Value);
            }
        }
    }

    private static string SystemLocale()
    {
        var c = CultureInfo.CurrentUICulture;
        if (c.TwoLetterISOLanguageName == "zh")
            return c.Name is "zh-HK" or "zh-TW" or "zh-MO" or "zh-Hant" ? "zh_HK" : "zh_CN";
        return "en_US";
    }

    /// <summary>Translate like QObject.tr(). <paramref name="context"/> is the original Python class name.</summary>
    public static string Tr(string source, string? context = null)
    {
        if (context != null && ByContext.TryGetValue((context, source), out var t)) return t;
        return BySource.TryGetValue(source, out var s) ? s : source;
    }
}

/// <summary>XAML: Text="{svc:Tr 主页}" or {svc:Tr 主页, Context=MainWindow}.</summary>
public sealed class TrExtension : MarkupExtension
{
    public TrExtension() { }
    public TrExtension(string text) => Text = text;

    public string Text { get; set; } = "";
    public string? Context { get; set; }

    public override object ProvideValue(IServiceProvider serviceProvider) => I18n.Tr(Text, Context);
}
