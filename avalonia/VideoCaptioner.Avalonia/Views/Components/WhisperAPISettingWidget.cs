using Avalonia;
using Avalonia.Controls;
using FluentAvalonia.UI.Controls;
using VideoCaptioner.Avalonia.Controls;
using VideoCaptioner.Avalonia.Services;

namespace VideoCaptioner.Avalonia.Views.Components;

/// <summary>Port of videocaptioner/ui/components/WhisperAPISettingWidget.py.</summary>
public class WhisperAPISettingWidget : UserControl
{
    private const string Ctx = "WhisperAPISettingWidget";
    private static string Tr(string s) => I18n.Tr(s, Ctx);

    public LineEditSettingCard BaseUrlCard { get; }
    public LineEditSettingCard ApiKeyCard { get; }
    public EditComboBoxSettingCard ModelCard { get; }
    public ComboBoxSettingCard LanguageCard { get; }
    public LineEditSettingCard PromptCard { get; }
    public PushSettingCard CheckConnectionCard { get; }

    public WhisperAPISettingWidget()
    {
        var settingGroup = new SettingCardGroup { Title = Tr("Whisper API 设置") };

        BaseUrlCard = new LineEditSettingCard(Cfg.WhisperApiBase, Symbol.Link, Tr("API Base URL"),
            Tr("输入 Whisper API Base URL"), "https://api.openai.com/v1");
        ApiKeyCard = new LineEditSettingCard(Cfg.WhisperApiKey, Symbol.Permissions, Tr("API Key"),
            Tr("输入 Whisper API Key"), "sk-") { IsPassword = true };
        ModelCard = new EditComboBoxSettingCard(Cfg.WhisperApiModel, Symbol.Code, Tr("Whisper 模型"),
            Tr("选择 Whisper 模型"), ["whisper-large-v3", "whisper-large-v3-turbo", "whisper-1"]);
        LanguageCard = new ComboBoxSettingCard(Cfg.TranscribeLanguage, Symbol.Character, Tr("源语言"),
            Tr("音视频中说话的语言，默认根据前30秒自动识别"), Models.Options.TranscribeLanguage);
        PromptCard = new LineEditSettingCard(Cfg.WhisperApiPrompt, Symbol.Message, Tr("提示词"),
            Tr("可选的提示词,默认空"), "");
        CheckConnectionCard = new PushSettingCard(Tr("测试连接"), Symbol.Sync, Tr("测试 Whisper API 连接"),
            Tr("点击测试 API 连接是否正常"));

        // Win11 layout: the connection (base url / key / model / test) is one expander, like the LLM provider
        // in Settings; language and prompt are separate cards. Widths: WinUI SettingsCard standard widths.
        var connectionCard = new SettingCard(Symbol.Globe, "Whisper API", Tr("配置 API 地址、密钥和模型"));
        connectionCard.Items.Add(BaseUrlCard);
        connectionCard.Items.Add(ApiKeyCard);
        connectionCard.Items.Add(ModelCard);
        connectionCard.Items.Add(CheckConnectionCard);
        connectionCard.IsExpanded = true;

        settingGroup.Items.Add(connectionCard);
        settingGroup.Items.Add(LanguageCard);
        settingGroup.Items.Add(PromptCard);

        CheckConnectionCard.Clicked += OnCheckConnection;

        Content = new ScrollViewer
        {
            HorizontalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = new StackPanel { Children = { settingGroup } },
        };
    }

    private void OnCheckConnection()
    {
        var baseUrl = BaseUrlCard.Text.Trim();
        var apiKey = ApiKeyCard.Text.Trim();
        var model = ModelCard.Text.Trim();

        if (baseUrl.Length == 0 || apiKey.Length == 0 || model.Length == 0)
        {
            Toast.Warning(Tr("配置不完整"), Tr("请输入 API Base URL、API Key 和 model"), 10000, ToastPosition.Top);
            return;
        }

        CheckConnectionCard.Button.IsEnabled = false;
        CheckConnectionCard.ButtonText = Tr("正在测试...");

        var job = Bridge.Instance.Run("settings_check_whisper", new { base_url = baseUrl, api_key = apiKey, model });
        job.Finished += data =>
        {
            CheckConnectionCard.Button.IsEnabled = true;
            CheckConnectionCard.ButtonText = Tr("测试连接");
            var success = data?["success"]?.GetValue<bool>() ?? false;
            var result = data?["result"]?.GetValue<string>() ?? "";
            if (success)
                Toast.Success(Tr("连接成功"), Tr("Whisper API 连接成功！") + "\n" + result, 2000, ToastPosition.Bottom);
            else
                Toast.Error(Tr("连接失败"), Tr($"Whisper API 连接失败！\n{result}"), 10000, ToastPosition.Bottom);
        };
        job.Error += message =>
        {
            CheckConnectionCard.Button.IsEnabled = true;
            CheckConnectionCard.ButtonText = Tr("测试连接");
            Toast.Error(Tr("测试错误"), message, 10000, ToastPosition.Bottom);
        };
    }
}
