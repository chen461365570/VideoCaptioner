using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VideoCaptioner.Avalonia.Models;

namespace VideoCaptioner.Avalonia.Services;

/// <summary>
/// One entry of settings.json ("Group" -> "Key"), mirroring qfluentwidgets ConfigItem.
/// Setting <see cref="Value"/> validates, persists immediately and raises <see cref="ValueChanged"/>.
/// Bind in XAML with {Binding Value, Source={x:Static svc:Cfg.NeedOptimize}}.
/// </summary>
public abstract class ConfigItemBase : INotifyPropertyChanged
{
    protected ConfigItemBase(string group, string key)
    {
        Group = group;
        Key = key;
        Cfg.Register(this);
    }

    public string Group { get; }
    public string Key { get; }
    /// <summary>True for items qfluentwidgets marks restart=True (DpiScale, Language).</summary>
    public bool Restart { get; init; }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void RaiseValueChanged() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Value"));

    internal abstract void Load(JsonNode? node);
    internal abstract JsonNode? Serialize();
    public abstract object? BoxedValue { get; }
}

public sealed class ConfigItem<T> : ConfigItemBase
{
    private T _value;
    private readonly Func<T, T> _correct;

    /// <param name="options">Allowed values (OptionsValidator); invalid values fall back to options[0].</param>
    /// <param name="min">RangeValidator lower bound (numeric T only).</param>
    public ConfigItem(string group, string key, T defaultValue, IReadOnlyList<T>? options = null,
        double? min = null, double? max = null) : base(group, key)
    {
        Default = defaultValue;
        Options = options;
        Min = min;
        Max = max;
        _correct = v => Correct(v);
        _value = defaultValue;
    }

    public T Default { get; }
    public IReadOnlyList<T>? Options { get; }
    public double? Min { get; }
    public double? Max { get; }
    public override object? BoxedValue => _value;

    /// <summary>Raised after the value changed (already persisted).</summary>
    public event Action<T>? ValueChanged;

    public T Value
    {
        get => _value;
        set
        {
            var v = _correct(value);
            if (EqualityComparer<T>.Default.Equals(v, _value)) return;
            _value = v;
            Cfg.Save();
            RaiseValueChanged();
            ValueChanged?.Invoke(v);
            Cfg.NotifyChanged(this);
        }
    }

    /// <summary>Set without persisting (used while batch-loading).</summary>
    internal void SetSilently(T v)
    {
        v = _correct(v);
        if (EqualityComparer<T>.Default.Equals(v, _value)) return;
        _value = v;
        RaiseValueChanged();
        ValueChanged?.Invoke(v);
    }

    private T Correct(T v)
    {
        if (Options is { Count: > 0 } && !Options.Contains(v)) return Options[0];
        if ((Min.HasValue || Max.HasValue) && v is IConvertible c && typeof(T) != typeof(string) && typeof(T) != typeof(bool))
        {
            var d = c.ToDouble(CultureInfo.InvariantCulture);
            if (Min.HasValue && d < Min) d = Min.Value;
            if (Max.HasValue && d > Max) d = Max.Value;
            return (T)Convert.ChangeType(d, typeof(T), CultureInfo.InvariantCulture);
        }
        return v;
    }

    internal override void Load(JsonNode? node)
    {
        if (node is null) { SetSilently(Default); return; }
        try
        {
            object? parsed = typeof(T) switch
            {
                var t when t == typeof(string) => node is JsonValue jv && jv.TryGetValue<string>(out var s) ? s : node.ToJsonString().Trim('"'),
                var t when t == typeof(bool) => node.GetValue<bool>(),
                var t when t == typeof(int) => (int)Math.Round(node.GetValue<double>()),
                var t when t == typeof(double) => node.GetValue<double>(),
                _ => node.Deserialize<T>(),
            };
            SetSilently(parsed is T tv ? tv : Default);
        }
        catch
        {
            SetSilently(Default);
        }
    }

    internal override JsonNode? Serialize()
    {
        // DpiScale is stored as a number unless it is "Auto" (qfluentwidgets OptionsValidator([1, 1.25, ..., "Auto"])).
        if (_value is string s && Group == "MainWindow" && Key == "DpiScale" &&
            double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            return d % 1 == 0 ? JsonValue.Create((int)d) : JsonValue.Create(d);
        return JsonValue.Create(_value);
    }
}

/// <summary>
/// Typed access to the same settings.json the Python app uses
/// (videocaptioner/ui/common/config.py). Unknown keys are preserved on save.
/// </summary>
public static class Cfg
{
    private static readonly List<ConfigItemBase> Items = [];
    private static JsonObject _root = new();
    private static bool _loading;

    public static string AppDataPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VideoCaptioner", "VideoCaptioner");
    public static string SettingsPath => Path.Combine(AppDataPath, "settings.json");

    internal static void Register(ConfigItemBase item) => Items.Add(item);

    /// <summary>Raised after any item was changed through its Value setter.</summary>
    public static event Action<ConfigItemBase>? Changed;

    // ------------------- LLM -------------------
    public static readonly ConfigItem<string> LlmService = new("LLM", "LLMService", "OpenAI 兼容", Models.Options.LLMService);
    public static readonly ConfigItem<string> OpenAiModel = new("LLM", "OpenAI_Model", "gpt-4o-mini");
    public static readonly ConfigItem<string> OpenAiApiKey = new("LLM", "OpenAI_API_Key", "");
    public static readonly ConfigItem<string> OpenAiApiBase = new("LLM", "OpenAI_API_Base", "https://api.openai.com/v1");
    public static readonly ConfigItem<string> SiliconCloudModel = new("LLM", "SiliconCloud_Model", "gpt-4o-mini");
    public static readonly ConfigItem<string> SiliconCloudApiKey = new("LLM", "SiliconCloud_API_Key", "");
    public static readonly ConfigItem<string> SiliconCloudApiBase = new("LLM", "SiliconCloud_API_Base", "https://api.siliconflow.cn/v1");
    public static readonly ConfigItem<string> DeepSeekModel = new("LLM", "DeepSeek_Model", "deepseek-chat");
    public static readonly ConfigItem<string> DeepSeekApiKey = new("LLM", "DeepSeek_API_Key", "");
    public static readonly ConfigItem<string> DeepSeekApiBase = new("LLM", "DeepSeek_API_Base", "https://api.deepseek.com/v1");
    public static readonly ConfigItem<string> OllamaModel = new("LLM", "Ollama_Model", "llama2");
    public static readonly ConfigItem<string> OllamaApiKey = new("LLM", "Ollama_API_Key", "ollama");
    public static readonly ConfigItem<string> OllamaApiBase = new("LLM", "Ollama_API_Base", "http://localhost:11434/v1");
    public static readonly ConfigItem<string> LmStudioModel = new("LLM", "LmStudio_Model", "qwen2.5:7b");
    public static readonly ConfigItem<string> LmStudioApiKey = new("LLM", "LmStudio_API_Key", "lmstudio");
    public static readonly ConfigItem<string> LmStudioApiBase = new("LLM", "LmStudio_API_Base", "http://localhost:1234/v1");
    public static readonly ConfigItem<string> GeminiModel = new("LLM", "Gemini_Model", "gemini-pro");
    public static readonly ConfigItem<string> GeminiApiKey = new("LLM", "Gemini_API_Key", "");
    public static readonly ConfigItem<string> GeminiApiBase = new("LLM", "Gemini_API_Base", "https://generativelanguage.googleapis.com/v1beta/openai/");
    public static readonly ConfigItem<string> ChatGlmModel = new("LLM", "ChatGLM_Model", "glm-4");
    public static readonly ConfigItem<string> ChatGlmApiKey = new("LLM", "ChatGLM_API_Key", "");
    public static readonly ConfigItem<string> ChatGlmApiBase = new("LLM", "ChatGLM_API_Base", "https://open.bigmodel.cn/api/paas/v4");

    // ------------------- 翻译配置 -------------------
    public static readonly ConfigItem<string> TranslatorService = new("Translate", "TranslatorServiceEnum", "微软翻译", Models.Options.TranslatorService);
    public static readonly ConfigItem<bool> NeedReflectTranslate = new("Translate", "NeedReflectTranslate", false);
    public static readonly ConfigItem<string> DeeplxEndpoint = new("Translate", "DeeplxEndpoint", "");
    public static readonly ConfigItem<int> BatchSize = new("Translate", "BatchSize", 10, min: 5, max: 50);
    public static readonly ConfigItem<int> ThreadNum = new("Translate", "ThreadNum", 10, min: 1, max: 50);

    // ------------------- 转录配置 -------------------
    public static readonly ConfigItem<string> TranscribeModel = new("Transcribe", "TranscribeModel", "B 接口", Models.Options.AvailableTranscribeModel);
    public static readonly ConfigItem<string> TranscribeOutputFormat = new("Transcribe", "OutputFormat", "SRT", Models.Options.TranscribeOutputFormat);
    public static readonly ConfigItem<string> TranscribeLanguage = new("Transcribe", "TranscribeLanguage", "自动检测", Models.Options.TranscribeLanguage);

    // ------------------- Whisper Cpp -------------------
    public static readonly ConfigItem<string> WhisperModel = new("Whisper", "WhisperModel", "tiny", Models.Options.WhisperModel);
    /// <summary>
    /// Avalonia-only addition: Vulkan whisper-cli settings, applied by bridge/handlers/whisper_cpp_gpu.py.
    /// Model is a ggml file name in MODEL_PATH; Device is "auto", "cpu" or a Vulkan device name.
    /// </summary>
    public static readonly ConfigItem<string> WhisperCppModel = new("WhisperCpp", "Model", "");
    public static readonly ConfigItem<string> WhisperCppDevice = new("WhisperCpp", "Device", "auto");
    public static readonly ConfigItem<bool> WhisperCppVadFilter = new("WhisperCpp", "VadFilter", true);
    public static readonly ConfigItem<double> WhisperCppVadThreshold = new("WhisperCpp", "VadThreshold", 0.4, min: 0, max: 1);

    // ------------------- Faster Whisper -------------------
    public static readonly ConfigItem<string> FasterWhisperProgram = new("FasterWhisper", "Program", "faster-whisper-xxl.exe");
    public static readonly ConfigItem<string> FasterWhisperModel = new("FasterWhisper", "Model", "tiny", Models.Options.FasterWhisperModel);
    public static readonly ConfigItem<string> FasterWhisperModelDir = new("FasterWhisper", "ModelDir", "");
    public static readonly ConfigItem<string> FasterWhisperDevice = new("FasterWhisper", "Device", "cuda", ["cuda", "cpu"]);
    /// <summary>
    /// Avalonia-only addition: CTranslate2 compute type passed as --compute_type ("auto" keeps the
    /// original behaviour: float16 on RTX 50, program default otherwise). Applied by bridge/handlers/compute_type.py.
    /// </summary>
    public static readonly ConfigItem<string> FasterWhisperComputeType = new("FasterWhisper", "ComputeType", "auto",
        ["auto", "int8", "int8_float16", "float16", "float32"]);
    public static readonly ConfigItem<bool> FasterWhisperVadFilter = new("FasterWhisper", "VadFilter", true);
    public static readonly ConfigItem<double> FasterWhisperVadThreshold = new("FasterWhisper", "VadThreshold", 0.4, min: 0, max: 1);
    public static readonly ConfigItem<string> FasterWhisperVadMethod = new("FasterWhisper", "VadMethod", "silero_v4", Models.Options.VadMethod);
    public static readonly ConfigItem<bool> FasterWhisperFfMdxKim2 = new("FasterWhisper", "FfMdxKim2", false);
    public static readonly ConfigItem<bool> FasterWhisperOneWord = new("FasterWhisper", "OneWord", true);
    public static readonly ConfigItem<string> FasterWhisperPrompt = new("FasterWhisper", "Prompt", "");

    // ------------------- Whisper API -------------------
    public static readonly ConfigItem<string> WhisperApiBase = new("WhisperAPI", "WhisperApiBase", "");
    public static readonly ConfigItem<string> WhisperApiKey = new("WhisperAPI", "WhisperApiKey", "");
    public static readonly ConfigItem<string> WhisperApiModel = new("WhisperAPI", "WhisperApiModel", "");
    public static readonly ConfigItem<string> WhisperApiPrompt = new("WhisperAPI", "WhisperApiPrompt", "");

    // ------------------- 字幕配置 -------------------
    public static readonly ConfigItem<bool> NeedOptimize = new("Subtitle", "NeedOptimize", false);
    public static readonly ConfigItem<bool> NeedTranslate = new("Subtitle", "NeedTranslate", false);
    public static readonly ConfigItem<bool> NeedSplit = new("Subtitle", "NeedSplit", false);
    public static readonly ConfigItem<string> TargetLanguage = new("Subtitle", "TargetLanguage", "简体中文", Models.Options.TargetLanguage);
    public static readonly ConfigItem<int> MaxWordCountCjk = new("Subtitle", "MaxWordCountCJK", 28, min: 8, max: 100);
    public static readonly ConfigItem<int> MaxWordCountEnglish = new("Subtitle", "MaxWordCountEnglish", 20, min: 8, max: 100);
    public static readonly ConfigItem<string> CustomPromptText = new("Subtitle", "CustomPromptText", "");

    // ------------------- 字幕合成配置 -------------------
    public static readonly ConfigItem<bool> SoftSubtitle = new("Video", "SoftSubtitle", false);
    public static readonly ConfigItem<bool> NeedVideo = new("Video", "NeedVideo", true);
    public static readonly ConfigItem<string> VideoQuality = new("Video", "VideoQuality", "中等质量", Models.Options.VideoQuality);
    public static readonly ConfigItem<bool> UseSubtitleStyle = new("Video", "UseSubtitleStyle", false);

    // ------------------- 字幕样式配置 -------------------
    public static readonly ConfigItem<string> SubtitleStyleName = new("SubtitleStyle", "StyleName", "default");
    public static readonly ConfigItem<string> SubtitleLayout = new("SubtitleStyle", "Layout", "译文在上", Models.Options.SubtitleLayout);
    public static readonly ConfigItem<string> SubtitlePreviewImage = new("SubtitleStyle", "PreviewImage", "");
    public static readonly ConfigItem<string> SubtitleRenderMode = new("SubtitleStyle", "RenderMode", "圆角背景", Models.Options.SubtitleRenderMode);

    // 圆角背景模式
    public static readonly ConfigItem<string> RoundedBgFontName = new("RoundedBgStyle", "FontName", "Noto Sans SC");
    public static readonly ConfigItem<int> RoundedBgFontSize = new("RoundedBgStyle", "FontSize", 52, min: 16, max: 120);
    public static readonly ConfigItem<string> RoundedBgColor = new("RoundedBgStyle", "BgColor", "#191919C8");
    public static readonly ConfigItem<string> RoundedBgTextColor = new("RoundedBgStyle", "TextColor", "#FFFFFF");
    public static readonly ConfigItem<int> RoundedBgCornerRadius = new("RoundedBgStyle", "CornerRadius", 12, min: 0, max: 50);
    public static readonly ConfigItem<int> RoundedBgPaddingH = new("RoundedBgStyle", "PaddingH", 28, min: 4, max: 100);
    public static readonly ConfigItem<int> RoundedBgPaddingV = new("RoundedBgStyle", "PaddingV", 14, min: 4, max: 50);
    public static readonly ConfigItem<int> RoundedBgMarginBottom = new("RoundedBgStyle", "MarginBottom", 60, min: 20, max: 300);
    public static readonly ConfigItem<int> RoundedBgLineSpacing = new("RoundedBgStyle", "LineSpacing", 10, min: 0, max: 50);
    public static readonly ConfigItem<int> RoundedBgLetterSpacing = new("RoundedBgStyle", "LetterSpacing", 0, min: 0, max: 20);

    // ------------------- 保存配置 -------------------
    public static readonly ConfigItem<string> WorkDir = new("Save", "Work_Dir",
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "VideoCaptioner").Replace('\\', '/'));

    // ------------------- 软件页面配置 -------------------
    public static readonly ConfigItem<bool> MicaEnabled = new("MainWindow", "MicaEnabled", false);
    public static readonly ConfigItem<string> DpiScale = new("MainWindow", "DpiScale", "Auto", ["1", "1.25", "1.5", "1.75", "2", "Auto"]) { Restart = true };
    /// <summary>"Auto", "zh_CN", "zh_HK", "en_US" (QLocale names).</summary>
    public static readonly ConfigItem<string> Language = new("MainWindow", "Language", "Auto", ["zh_CN", "zh_HK", "en_US", "Auto"]) { Restart = true };

    // ------------------- 更新配置 -------------------
    public static readonly ConfigItem<bool> CheckUpdateAtStartUp = new("Update", "CheckUpdateAtStartUp", true);

    // ------------------- 缓存配置 -------------------
    public static readonly ConfigItem<bool> CacheEnabled = new("Cache", "CacheEnabled", true);

    // ------------------- qfluentwidgets 主题 -------------------
    /// <summary>"Light" / "Dark" / "Auto".</summary>
    public static readonly ConfigItem<string> ThemeMode = new("QFluentWidgets", "ThemeMode", "Dark", ["Light", "Dark", "Auto"]);
    /// <summary>#AARRGGBB like QColor.name(HexArgb).</summary>
    public static readonly ConfigItem<string> ThemeColor = new("QFluentWidgets", "ThemeColor", "#ff28f08b");

    public static IReadOnlyList<ConfigItemBase> All => Items;

    /// <summary>(Re)load settings.json. Called once at startup; call again if the backend changed the file.</summary>
    public static void Load()
    {
        _loading = true;
        try
        {
            _root = File.Exists(SettingsPath)
                ? JsonNode.Parse(File.ReadAllText(SettingsPath, Encoding.UTF8)) as JsonObject ?? new JsonObject()
                : new JsonObject();
        }
        catch
        {
            _root = new JsonObject();
        }

        foreach (var item in Items)
            item.Load((_root[item.Group] as JsonObject)?[item.Key]);
        _loading = false;
    }

    internal static void Save()
    {
        if (_loading) return;
        foreach (var item in Items)
        {
            if (_root[item.Group] is not JsonObject g)
                _root[item.Group] = g = new JsonObject();
            g[item.Key] = item.Serialize();
        }
        Directory.CreateDirectory(AppDataPath);
        // Same layout qfluentwidgets writes (indent 4, keep non-ASCII).
        var json = _root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            // JsonValue.Create<T>(...) values need a resolver when written with custom options
            TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
        });
        File.WriteAllText(SettingsPath, json, new UTF8Encoding(false));
    }

    internal static void NotifyChanged(ConfigItemBase item) => Changed?.Invoke(item);

    /// <summary>LLM base/key/model for the currently selected LLM service (same mapping as TaskFactory).</summary>
    public static (ConfigItem<string> Base, ConfigItem<string> Key, ConfigItem<string> Model)? LlmItemsFor(string service) => service switch
    {
        "OpenAI 兼容" => (OpenAiApiBase, OpenAiApiKey, OpenAiModel),
        "SiliconCloud" => (SiliconCloudApiBase, SiliconCloudApiKey, SiliconCloudModel),
        "DeepSeek" => (DeepSeekApiBase, DeepSeekApiKey, DeepSeekModel),
        "Ollama" => (OllamaApiBase, OllamaApiKey, OllamaModel),
        "LM Studio" => (LmStudioApiBase, LmStudioApiKey, LmStudioModel),
        "Gemini" => (GeminiApiBase, GeminiApiKey, GeminiModel),
        "ChatGLM" => (ChatGlmApiBase, ChatGlmApiKey, ChatGlmModel),
        _ => null,
    };
}
