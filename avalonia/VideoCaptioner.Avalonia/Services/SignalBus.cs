namespace VideoCaptioner.Avalonia.Services;

/// <summary>Port of videocaptioner/ui/common/signal_bus.py. Raise on the UI thread.</summary>
public static class SignalBus
{
    // 字幕排布信号
    public static event Action<string>? SubtitleLayoutChanged;
    // 字幕优化信号
    public static event Action<bool>? SubtitleOptimizationChanged;
    // 字幕翻译信号
    public static event Action<bool>? SubtitleTranslationChanged;
    // 翻译语言
    public static event Action<string>? TargetLanguageChanged;
    // 转录模型
    public static event Action<string>? TranscriptionModelChanged;
    // 软字幕信号
    public static event Action<bool>? SoftSubtitleChanged;
    // 视频合成信号
    public static event Action<bool>? NeedVideoChanged;
    // 视频质量信号
    public static event Action<string>? VideoQualityChanged;
    // 使用样式信号
    public static event Action<bool>? UseSubtitleStyleChanged;
    // 渲染模式变更信号
    public static event Action<string>? SubtitleRenderModeChanged;

    // 视频控制
    public static event Action? VideoPlay;
    public static event Action? VideoPause;
    public static event Action? VideoStop;
    /// <summary>Local file path of the new video source.</summary>
    public static event Action<string>? VideoSourceChanged;
    /// <summary>(start ms, end ms)</summary>
    public static event Action<int, int>? VideoSegmentPlay;
    /// <summary>Subtitle file path added to the player.</summary>
    public static event Action<string>? VideoSubtitleAdded;

    public static void EmitSubtitleLayoutChanged(string v) => SubtitleLayoutChanged?.Invoke(v);
    public static void EmitSubtitleOptimizationChanged(bool v) => SubtitleOptimizationChanged?.Invoke(v);
    public static void EmitSubtitleTranslationChanged(bool v) => SubtitleTranslationChanged?.Invoke(v);
    public static void EmitTargetLanguageChanged(string v) => TargetLanguageChanged?.Invoke(v);
    public static void EmitTranscriptionModelChanged(string v) => TranscriptionModelChanged?.Invoke(v);
    public static void EmitSoftSubtitleChanged(bool v) => SoftSubtitleChanged?.Invoke(v);
    public static void EmitNeedVideoChanged(bool v) => NeedVideoChanged?.Invoke(v);
    public static void EmitVideoQualityChanged(string v) => VideoQualityChanged?.Invoke(v);
    public static void EmitUseSubtitleStyleChanged(bool v) => UseSubtitleStyleChanged?.Invoke(v);
    public static void EmitSubtitleRenderModeChanged(string v) => SubtitleRenderModeChanged?.Invoke(v);

    public static void PlayVideo() => VideoPlay?.Invoke();
    public static void PauseVideo() => VideoPause?.Invoke();
    public static void StopVideo() => VideoStop?.Invoke();
    public static void SetVideoSource(string path) => VideoSourceChanged?.Invoke(path);
    public static void PlayVideoSegment(int startMs, int endMs) => VideoSegmentPlay?.Invoke(startMs, endMs);
    public static void AddSubtitle(string subtitleFile) => VideoSubtitleAdded?.Invoke(subtitleFile);
}
