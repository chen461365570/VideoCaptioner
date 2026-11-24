using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;
using VideoCaptioner.Avalonia.Controls;
using VideoCaptioner.Avalonia.Services;

namespace VideoCaptioner.Avalonia.Views;

/// <summary>Port of videocaptioner/ui/view/setting_interface.py (SettingInterface).</summary>
public partial class SettingInterface : UserControl, IWindowTitleProvider
{
    private const string Ctx = "SettingInterface";
    private static string Tr(string s) => I18n.Tr(s, Ctx);

    // LLMServiceEnum values (videocaptioner.core.entities)
    private const string Openai = "OpenAI 兼容";
    private const string Ollama = "Ollama";
    private const string LmStudio = "LM Studio";

    private sealed record LlmServiceCards(LineEditSettingCard ApiKey, LineEditSettingCard ApiBase, EditComboBoxSettingCard Model)
    {
        public IEnumerable<SettingCard> Cards => [ApiKey, ApiBase, Model];
    }

    private readonly Dictionary<string, LlmServiceCards> _llmServiceConfigs = new();

    // expander -> all of its child rows (rows whose card is hidden are taken out so an expander
    // without visible options shows no chevron, like Win11 Settings)
    private readonly Dictionary<SettingCard, List<SettingCard>> _childCards = new();

    private string _helpUrl = "https://github.com/WEIFENG2333/VideoCaptioner";
    private string _feedbackUrl = "https://github.com/WEIFENG2333/VideoCaptioner/issues";
    private string _releaseUrl = "https://github.com/WEIFENG2333/VideoCaptioner/releases/latest";

    public string? WindowTitle => Tr("设置");

    public SettingInterface()
    {
        InitializeComponent();
        CreateAsrServiceCards();
        CreateLlmServiceCards();
        CreateTranslateServiceCards();
        InitCards();
        OnTranscribeModelChanged(Cfg.TranscribeModel.Value);
        OnTranslatorServiceChanged(Cfg.TranslatorService.Value);
        ConnectSignalToSlot();
        SetupSectionNav();
        _ = LoadAboutAsync();
    }

    // ------------------------------------------------------------------ section index (left column)
    private bool _navSyncing;
    private DateTime _navClickedAt;

    /// <summary>
    /// The left column lists the section headers of the page. Clicking one scrolls to it; scrolling the
    /// page selects the section at the top of the viewport.
    /// </summary>
    private void SetupSectionNav()
    {
        var headers = ExpandLayout.Children.OfType<TextBlock>().Where(t => t.Classes.Contains("section")).ToList();
        SectionNav.ItemsSource = headers.Select(h => h.Text).ToList();
        SectionNav.SelectedIndex = 0;

        SectionNav.SelectionChanged += (_, _) =>
        {
            var index = SectionNav.SelectedIndex;
            if (_navSyncing || index < 0 || index >= headers.Count) return;
            if (headers[index].TranslatePoint(default, ExpandLayout) is not { } pos) return;
            _navClickedAt = DateTime.UtcNow;
            Scroll.Offset = new global::Avalonia.Vector(0, Math.Max(0, pos.Y - 4));
        };
        Scroll.ScrollChanged += (_, _) =>
        {
            // keep the clicked section selected even when the page cannot scroll far enough to reach it
            if ((DateTime.UtcNow - _navClickedAt).TotalMilliseconds < 400) return;
            var top = Scroll.Offset.Y + 48;
            var current = 0;
            for (var i = 0; i < headers.Count; i++)
                if (headers[i].TranslatePoint(default, ExpandLayout) is { } p && p.Y <= top) current = i;
            if (Scroll.Offset.Y > 0 && Scroll.Offset.Y >= Scroll.Extent.Height - Scroll.Viewport.Height - 1)
                current = headers.Count - 1;
            if (SectionNav.SelectedIndex == current) return;
            _navSyncing = true;
            SectionNav.SelectedIndex = current;
            _navSyncing = false;
        };
    }

    // ------------------------------------------------------------------ cards
    private void CreateAsrServiceCards()
    {
        WhisperApiModelCard.SetItems(["whisper-1", "whisper-large-v3-turbo"]);
        WhisperApiModelCard.ConfigItem = Cfg.WhisperApiModel;
    }

    private void CreateLlmServiceCards()
    {
        // service -> (api key, base url, model, default base, default models)
        var serviceConfigs = new (string Service, ConfigItem<string> Key, ConfigItem<string> Base, ConfigItem<string> Model,
            string DefaultBase, string[] DefaultModels)[]
        {
            (Openai, Cfg.OpenAiApiKey, Cfg.OpenAiApiBase, Cfg.OpenAiModel, "https://api.openai.com/v1",
                ["gemini-2.5-pro", "gpt-5", "claude-sonnet-4-5-20250929", "gemini-2.5-flash", "claude-haiku-4-5-20251001"]),
            ("SiliconCloud", Cfg.SiliconCloudApiKey, Cfg.SiliconCloudApiBase, Cfg.SiliconCloudModel, "https://api.siliconflow.cn/v1",
                ["moonshotai/Kimi-K2-Instruct-0905", "deepseek-ai/DeepSeek-V3"]),
            ("DeepSeek", Cfg.DeepSeekApiKey, Cfg.DeepSeekApiBase, Cfg.DeepSeekModel, "https://api.deepseek.com/v1",
                ["deepseek-chat", "deepseek-reasoner"]),
            (Ollama, Cfg.OllamaApiKey, Cfg.OllamaApiBase, Cfg.OllamaModel, "http://localhost:11434/v1", ["qwen3:8b"]),
            (LmStudio, Cfg.LmStudioApiKey, Cfg.LmStudioApiBase, Cfg.LmStudioModel, "http://localhost:1234/v1", ["qwen3:8b"]),
            ("Gemini", Cfg.GeminiApiKey, Cfg.GeminiApiBase, Cfg.GeminiModel, "https://generativelanguage.googleapis.com/v1beta/openai/",
                ["gemini-2.5-pro", "gemini-2.5-flash", "gemini-2.0-flash-lite"]),
            ("ChatGLM", Cfg.ChatGlmApiKey, Cfg.ChatGlmApiBase, Cfg.ChatGlmModel, "https://open.bigmodel.cn/api/paas/v4",
                ["glm-4-plus", "glm-4-air-250414", "glm-4-flash"]),
        };

        // Rows go between the OpenAI official API row and the check-connection row of the provider expander.
        var insertAt = LlmServiceCard.Items.IndexOf(CheckLlmConnectionCard);
        foreach (var (service, key, apiBase, model, defaultBase, defaultModels) in serviceConfigs)
        {
            var apiKeyCard = new LineEditSettingCard(key, Symbol.Permissions, Tr("API Key"),
                Tr($"输入您的 {service} API Key"), service != Ollama ? "sk-" : "") { IsPassword = true };
            var apiBaseCard = new LineEditSettingCard(apiBase, Symbol.Link, Tr("Base URL"),
                Tr($"输入 {service} Base URL"), defaultBase);
            // 只有 OpenAI、Ollama、LM Studio 可以编辑 Base URL
            if (service is not (Openai or Ollama or LmStudio)) apiBaseCard.IsReadOnly = true;
            var modelCard = new EditComboBoxSettingCard(model, Symbol.Code, Tr("模型"),
                Tr($"选择 {service} 模型"), defaultModels);

            var cards = new LlmServiceCards(apiKeyCard, apiBaseCard, modelCard);
            _llmServiceConfigs[service] = cards;
            foreach (var card in cards.Cards) LlmServiceCard.Items.Insert(insertAt++, card);
        }

        OnLlmServiceChanged(Cfg.LlmService.Value);
    }

    private void CreateTranslateServiceCards()
    {
        OnTranslatorServiceChanged(Cfg.TranslatorService.Value);
    }

    private void InitCards()
    {
        SavePathCard.SetContent(Cfg.WorkDir.Value);

        ThemeCard.Texts = [Tr("浅色"), Tr("深色"), Tr("使用系统设置")];
        ThemeCard.ConfigItem = Cfg.ThemeMode;
        ZoomCard.Texts = ["100%", "125%", "150%", "175%", "200%", Tr("使用系统设置")];
        ZoomCard.ConfigItem = Cfg.DpiScale;
        LanguageCard.Texts = ["简体中文", "繁體中文", "English", Tr("使用系统设置")];
        LanguageCard.ConfigItem = Cfg.Language;

        SetAboutContent(null, null, null);
    }

    private void SetAboutContent(string? year, string? author, string? version)
    {
        // Win11 "About": copyright under the app name, version on the right
        AboutExpander.SetContent("© " + Tr("版权所有") + $" {year ?? "2026"}, {author ?? "Weifeng"}");
        VersionText.Text = Tr("版本") + " " + (version ?? "");
    }

    private async Task LoadAboutAsync()
    {
        try
        {
            var p = await Bridge.Instance.RequestAsync("paths");
            string? Get(string k) => p?[k]?.GetValue<string>();
            SetAboutContent(Get("YEAR"), Get("AUTHOR"), Get("VERSION"));
            _helpUrl = Get("HELP_URL") ?? _helpUrl;
            _feedbackUrl = Get("FEEDBACK_URL") ?? _feedbackUrl;
            _releaseUrl = Get("RELEASE_URL") ?? _releaseUrl;
            HelpCard.Url = _helpUrl;
        }
        catch
        {
            // backend unavailable: keep the defaults
        }
    }

    // ------------------------------------------------------------------ signals
    private void ConnectSignalToSlot()
    {
        // cfg.appRestartSig
        Cfg.Changed += item =>
        {
            if (item.Restart) ShowRestartTooltip();
        };

        LlmServiceCard.CurrentTextChanged += OnLlmServiceChanged;
        TranslatorServiceCard.CurrentTextChanged += OnTranslatorServiceChanged;
        TranscribeModelCard.CurrentTextChanged += OnTranscribeModelChanged;

        CheckLlmConnectionCard.Clicked += CheckLlmConnection;
        CheckWhisperConnectionCard.Clicked += CheckWhisperConnection;
        SavePathCard.Clicked += OnSavePathCardClicked;

        SubtitleStyleCard.LinkClicked += () => MainWindowOf()?.SwitchTo("style");
        SubtitleLayoutCard.LinkClicked += () => MainWindowOf()?.SwitchTo("style");

        CacheEnabledCard.CheckedChanged += OnCacheEnabledChanged;
        // themeCard.optionChanged -> setTheme / themeColorCard.colorChanged -> setThemeColor:
        // App.ApplyTheme already follows Cfg.ThemeMode / Cfg.ThemeColor.

        FeedbackCard.Clicked += () => Dialogs.OpenUrl(_feedbackUrl);
        AboutCard.Clicked += () => Dialogs.OpenUrl(_releaseUrl);

        // 全局 signalBus (the combo/switch signals fire on any config change, like qfluentwidgets cards)
        Cfg.TranscribeModel.ValueChanged += SignalBus.EmitTranscriptionModelChanged;
        Cfg.NeedOptimize.ValueChanged += SignalBus.EmitSubtitleOptimizationChanged;
        Cfg.NeedTranslate.ValueChanged += SignalBus.EmitSubtitleTranslationChanged;
        Cfg.TargetLanguage.ValueChanged += SignalBus.EmitTargetLanguageChanged;
        Cfg.SoftSubtitle.ValueChanged += SignalBus.EmitSoftSubtitleChanged;
        Cfg.NeedVideo.ValueChanged += SignalBus.EmitNeedVideoChanged;
        Cfg.VideoQuality.ValueChanged += SignalBus.EmitVideoQualityChanged;
    }

    private MainWindow? MainWindowOf() => this.FindAncestorOfType<MainWindow>() ?? Dialogs.MainWindow as MainWindow;

    private void ShowRestartTooltip()
    {
        Toast.Success(Tr("更新成功"), Tr("配置将在重启后生效"), 2000);
        RestartInfoBar.IsOpen = true;
    }

    private async void OnSavePathCardClicked()
    {
        var folder = await Dialogs.OpenFolderAsync(Tr("选择文件夹"), "./");
        if (string.IsNullOrEmpty(folder)) return;
        folder = folder.Replace('\\', '/').TrimEnd('/');
        if (Cfg.WorkDir.Value == folder) return;
        Cfg.WorkDir.Value = folder;
        SavePathCard.SetContent(folder);
    }

    private async void OnCacheEnabledChanged(bool isEnabled)
    {
        try
        {
            await Bridge.Instance.RequestAsync("settings_set_cache", new { enabled = isEnabled });
        }
        catch
        {
            // the bridge re-applies the cache flag on its next start
        }
        if (isEnabled)
            Toast.Success(Tr("缓存已启用"), Tr("ASR、翻译等操作将优先使用缓存"), 2000);
        else
            Toast.Warning(Tr("缓存已禁用"), Tr("所有操作将重新生成，不使用缓存（建议开启缓存）"), 5000);
    }

    // ------------------------------------------------------------------ LLM
    private void CheckLlmConnection()
    {
        var currentService = Cfg.LlmService.Value;
        if (!_llmServiceConfigs.TryGetValue(currentService, out var cfg)) return;

        var apiBase = cfg.ApiBase.Text;
        var apiKey = cfg.ApiKey.Text;
        var model = cfg.Model.Text;

        var offset = Scroll.Offset;
        CheckLlmConnectionCard.Button.IsEnabled = false;
        CheckLlmConnectionCard.ButtonText = Tr("正在检查...");
        Scroll.Offset = offset;

        var job = Bridge.Instance.Run("settings_check_llm", new { api_base = apiBase, api_key = apiKey, model });
        job.Error += OnConnectionCheckError;
        job.Finished += data =>
        {
            var success = data?["success"]?.GetValue<bool>() ?? false;
            var message = data?["message"]?.GetValue<string>() ?? "";
            var models = data?["models"]?.AsArray().Select(m => m?.GetValue<string>() ?? "").ToList() ?? [];
            OnConnectionCheckFinished(success, message, models);
        };
    }

    private void OnConnectionCheckError(string message)
    {
        CheckLlmConnectionCard.Button.IsEnabled = true;
        CheckLlmConnectionCard.ButtonText = Tr("检查连接");
        Toast.Error(Tr("LLM 连接测试错误"), message, 10000);
    }

    private void OnConnectionCheckFinished(bool isSuccess, string message, List<string> models)
    {
        CheckLlmConnectionCard.Button.IsEnabled = true;
        CheckLlmConnectionCard.ButtonText = Tr("检查连接");

        var currentService = Cfg.LlmService.Value;
        if (models.Count > 0)
        {
            if (_llmServiceConfigs.TryGetValue(currentService, out var cfg))
            {
                var temp = cfg.Model.Text;
                cfg.Model.SetItems(models);
                cfg.Model.Text = temp;
            }
            Toast.Success(Tr("获取模型列表成功:"), Tr("一共") + models.Count + Tr("个模型"), 2000);
        }
        if (!isSuccess)
            Toast.Error(Tr("LLM 连接测试错误"), message, 10000);
        else
            Toast.Success(Tr("LLM 连接测试成功"), message, 2000);
    }

    private void OnLlmServiceChanged(string service)
    {
        foreach (var cfg in _llmServiceConfigs.Values)
            foreach (var card in cfg.Cards)
                card.IsVisible = false;
        OpenaiOfficialApiCard.IsVisible = false;

        if (_llmServiceConfigs.TryGetValue(service, out var current))
        {
            foreach (var card in current.Cards) card.IsVisible = true;

            // 为 OLLAMA 和 LM_STUDIO 设置默认 API Key
            if (service == Ollama && string.IsNullOrEmpty(current.ApiKey.Text))
                current.ApiKey.Text = "ollama";
            if (service == LmStudio && string.IsNullOrEmpty(current.ApiKey.Text))
                current.ApiKey.Text = "lm-studio";

            if (service == Openai) OpenaiOfficialApiCard.IsVisible = true;
        }
    }

    // ------------------------------------------------------------------ translate / transcribe
    private void OnTranslatorServiceChanged(string service)
    {
        SettingCard[] openaiCards = [NeedReflectTranslateCard, BatchSizeCard];
        SettingCard[] deeplxCards = [DeeplxEndpointCard];
        foreach (var card in openaiCards.Concat(deeplxCards)) card.IsVisible = false;

        if (service == "DeepLx 翻译")
            foreach (var card in deeplxCards) card.IsVisible = true;
        else if (service == "LLM 大模型翻译")
            foreach (var card in openaiCards) card.IsVisible = true;
        SyncChildren(TranslatorServiceCard);
    }

    private void OnTranscribeModelChanged(string modelName)
    {
        var isWhisperApi = modelName == "Whisper [API] ✨";
        SettingCard[] whisperApiCards = [WhisperApiBaseCard, WhisperApiKeyCard, WhisperApiModelCard, CheckWhisperConnectionCard];
        foreach (var card in whisperApiCards) card.IsVisible = isWhisperApi;
        SyncChildren(TranscribeModelCard);
    }

    /// <summary>Show only the visible child rows of <paramref name="parent"/>; expand it when rows appear.</summary>
    private void SyncChildren(SettingCard parent)
    {
        if (!_childCards.TryGetValue(parent, out var all))
            _childCards[parent] = all = parent.Items.OfType<SettingCard>().ToList();
        var visible = all.Where(c => c.IsVisible).ToList();
        var current = parent.Items.OfType<SettingCard>().ToList();
        if (current.SequenceEqual(visible)) return;
        parent.Items.Clear();
        foreach (var card in visible) parent.Items.Add(card);
        if (current.Count == 0 && visible.Count > 0 && parent.IsLoaded) parent.IsExpanded = true;
    }

    private void CheckWhisperConnection()
    {
        var baseUrl = WhisperApiBaseCard.Text.Trim();
        var apiKey = WhisperApiKeyCard.Text.Trim();
        var model = WhisperApiModelCard.Text.Trim();

        if (string.IsNullOrEmpty(baseUrl))
        {
            Toast.Warning(Tr("配置不完整"), Tr("请输入 Whisper API Base URL"), 10000);
            return;
        }
        if (string.IsNullOrEmpty(apiKey))
        {
            Toast.Warning(Tr("配置不完整"), Tr("请输入 Whisper API Key"), 10000);
            return;
        }
        if (string.IsNullOrEmpty(model))
        {
            Toast.Warning(Tr("配置不完整"), Tr("请输入 Whisper 模型名称"), 10000);
            return;
        }

        var offset = Scroll.Offset;
        CheckWhisperConnectionCard.Button.IsEnabled = false;
        CheckWhisperConnectionCard.ButtonText = Tr("正在测试...");
        Scroll.Offset = offset;

        var job = Bridge.Instance.Run("settings_check_whisper", new { base_url = baseUrl, api_key = apiKey, model });
        job.Finished += data =>
        {
            CheckWhisperConnectionCard.Button.IsEnabled = true;
            CheckWhisperConnectionCard.ButtonText = Tr("测试 Whisper 连接");
            var success = data?["success"]?.GetValue<bool>() ?? false;
            var result = data?["result"]?.GetValue<string>() ?? "";
            if (success)
                Toast.Success(Tr("连接成功"), Tr("Whisper API 连接成功！\n转录结果:") + result, 2000);
            else
                Toast.Error(Tr("连接失败"), Tr($"Whisper API 连接失败！\n{result}"), 10000);
        };
        job.Error += message =>
        {
            CheckWhisperConnectionCard.Button.IsEnabled = true;
            CheckWhisperConnectionCard.ButtonText = Tr("测试 Whisper 连接");
            Toast.Error(Tr("测试错误"), message, 10000);
        };
    }
}
