using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using FluentAvalonia.UI.Controls;
using LibVLCSharp.Shared;
using VideoCaptioner.Avalonia.Services;

namespace VideoCaptioner.Avalonia.Views.Components;

/// <summary>
/// Lazily created LibVLC instance (MediaPlayer.__init__: vlc.Instance(["--no-xlib", "--quiet"])).
/// Core.Initialize() loads the native libvlc only when the first player is created,
/// so app startup is not affected.
/// </summary>
public static class VlcCore
{
    private static readonly Lazy<LibVLC> Instance = new(() =>
    {
        Core.Initialize();
        try
        {
            return new LibVLC("--no-xlib", "--quiet");
        }
        catch
        {
            return new LibVLC("--quiet");
        }
    }, LazyThreadSafetyMode.ExecutionAndPublication);

    public static LibVLC LibVlc => Instance.Value;

    /// <summary>Optional warm-up (loads libvlc and the plugin cache on a worker thread).</summary>
    public static Task WarmUpAsync() => Task.Run(() => _ = Instance.Value);
}

/// <summary>
/// Port of videocaptioner/ui/components/MyVideoWidget.py: a top-level video window
/// (VLC player + qfluentwidgets StandardMediaPlayBar) driven by SignalBus video events.
/// Usage like Python: <c>var p = new MyVideoWidget(); p.Show(); p.Play();</c>
/// </summary>
public partial class MyVideoWidget : Window
{
    private static readonly string[] VideoExts = [".mp4", ".avi", ".mkv", ".mov", ".wmv", ".flv"];
    private static readonly string[] SubtitleExts = [".srt", ".ass"];

    private readonly LibVLC _libVlc;
    private readonly MediaPlayer _player;
    private readonly DispatcherTimer _updateTimer;
    private Media? _media;
    private string? _subtitleFile; // 设置字幕文件

    // 保存上一次的状态，用于检测变化
    private long _lastPosition;
    private long _lastDuration;
    private int _lastVolume = -1;
    private int _volume = 30;
    private bool _muted;
    private bool _updatingSlider;
    private bool _sliderPressed;
    private EventHandler<MediaPlayerTimeChangedEventArgs>? _segmentHandler;
    private long? _pendingSeek; // seek requested before the input was running

    public MyVideoWidget()
    {
        InitializeComponent();
        TipLabel.Text = I18n.Tr("请拖入视频文件", "MyVideoWidget");

        // 创建播放器（首次使用时才初始化 LibVLC）
        _libVlc = VlcCore.LibVlc;
        _player = new MediaPlayer(_libVlc)
        {
            // let clicks / keys / drops reach the Avalonia window instead of the VLC video window
            EnableMouseInput = false,
            EnableKeyInput = false,
        };
        VideoView.MediaPlayer = _player;

        _player.Playing += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            ApplyVolume();
            if (_pendingSeek is { } seek)
            {
                _pendingSeek = null;
                SetPosition(seek);
            }
            UpdatePlayButton();
        });
        _player.Paused += (_, _) => Dispatcher.UIThread.Post(UpdatePlayButton);
        _player.Stopped += (_, _) => Dispatcher.UIThread.Post(UpdatePlayButton);
        _player.EndReached += (_, _) => Dispatcher.UIThread.Post(UpdatePlayButton);

        // 创建定时器用于更新状态（100ms）
        _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _updateTimer.Tick += (_, _) => OnTimerUpdate();
        _updateTimer.Start();

        SetupPlayBar();

        // playBar.setVolume(80)
        SetVolume(80);

        // 连接 SignalBus 信号
        ConnectSignals();

        AddHandler(KeyDownEvent, OnKeyDownTunnel, RoutingStrategies.Tunnel);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        PointerWheelChanged += (_, e) => e.Handled = true; // wheelEvent: ignore

        Opened += (_, _) => Focus();
        Closing += (_, _) => Stop(); // hideEvent -> stop
        Closed += (_, _) => Release();
    }

    // ------------------------------------------------------------------ play bar

    private void SetupPlayBar()
    {
        PlayButton.Click += (_, _) => TogglePlayState();
        SkipBackButton.Click += (_, _) => SkipBack(5000);
        SkipForwardButton.Click += (_, _) => SkipForward(5000);

        // don't use valueChanged for playback, only user interaction (sliderMoved / clicked)
        ProgressSlider.AddHandler(PointerPressedEvent, (_, _) => _sliderPressed = true, RoutingStrategies.Tunnel, true);
        ProgressSlider.AddHandler(PointerReleasedEvent, (_, _) =>
        {
            _sliderPressed = false;
            SetPosition((long)ProgressSlider.Value);
        }, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, true);
        ProgressSlider.ValueChanged += (_, e) =>
        {
            if (_updatingSlider) return;
            if (_sliderPressed) SetPosition((long)e.NewValue);
        };

        VolumeSlider.ValueChanged += (_, e) => SetVolume((int)e.NewValue);
        MuteButton.Click += (_, _) => SetMuted(!_muted);
        SetMutedUi(false);
        UpdatePlayButton();
    }

    private void UpdatePlayButton()
    {
        var playing = IsPlaying;
        PlayIcon.Symbol = playing ? Symbol.PauseFilled : Symbol.PlayFilled;
        ToolTip.SetTip(PlayButton, playing ? I18n.Tr("Pause", "PlayButton") : I18n.Tr("Play", "PlayButton"));
    }

    private void SetMutedUi(bool isMute)
    {
        VolumeIcon.Symbol = isMute ? Symbol.Mute : Symbol.Volume;
        MuteIcon.Symbol = isMute ? Symbol.Mute : Symbol.Volume;
        ToolTip.SetTip(MuteButton, isMute ? I18n.Tr("Unmute", "VolumeView") : I18n.Tr("Mute", "VolumeView"));
    }

    private void SetVolumeUi(int volume)
    {
        _updatingSlider = true;
        VolumeSlider.Value = volume;
        _updatingSlider = false;
        VolumeLabel.Text = volume.ToString();
    }

    /// <summary>MediaPlayer._on_timer_update: poll position / duration / volume.</summary>
    private void OnTimerUpdate()
    {
        var position = _player.Time;
        if (position != _lastPosition)
        {
            _lastPosition = position;
            OnPositionChanged(position);
        }

        var duration = _player.Length;
        if (duration != _lastDuration)
        {
            _lastDuration = duration;
            _updatingSlider = true;
            ProgressSlider.Maximum = Math.Max(0, duration);
            _updatingSlider = false;
        }

        var volume = _player.Volume;
        if (volume >= 0 && volume != _lastVolume)
        {
            _lastVolume = volume;
            SetVolumeUi(volume);
        }
    }

    private void OnPositionChanged(long position)
    {
        if (!_sliderPressed)
        {
            _updatingSlider = true;
            ProgressSlider.Value = Math.Clamp(position, 0, ProgressSlider.Maximum);
            _updatingSlider = false;
        }
        CurrentTimeLabel.Text = FormatTime(position);
        RemainTimeLabel.Text = FormatTime(Duration - position);
    }

    private static string FormatTime(long time)
    {
        var t = (int)(time / 1000);
        var s = t % 60;
        var m = t / 60 % 60;
        var h = t / 3600;
        return $"{h}:{m:00}:{s:00}";
    }

    // ------------------------------------------------------------------ player (MediaPlayer port)

    public bool IsPlaying => _player.IsPlaying;
    public long Duration => _player.Length;
    public long PlaybackPosition => _player.Time;
    public bool HasMedia => _media != null;

    public void SetPosition(long position)
    {
        _player.Time = Math.Max(0, position);
        OnPositionChanged(position);
    }

    public void SkipBack(int ms) => SetPosition(PlaybackPosition - ms);
    public void SkipForward(int ms) => SetPosition(PlaybackPosition + ms);

    public void SetVolume(int volume)
    {
        _volume = Math.Clamp(volume, 0, 100);
        ApplyVolume();
        SetVolumeUi(_volume);
    }

    /// <summary>libvlc ignores the volume while no audio output exists, so re-apply it on playback start.</summary>
    private void ApplyVolume() => _player.Volume = _volume;

    public void SetMuted(bool isMuted)
    {
        _muted = isMuted;
        _player.Mute = isMuted;
        SetMutedUi(isMuted);
    }

    /// <summary>添加字幕文件的内部方法</summary>
    public void AddSubtitle(string subtitleFile)
    {
        _subtitleFile = subtitleFile;
        AddSubtitleToPlayer(subtitleFile);
    }

    private bool AddSubtitleToPlayer(string subtitleFile)
    {
        if (!HasMedia) return false;
        try
        {
            var uri = new Uri(Path.GetFullPath(subtitleFile)).AbsoluteUri;
            return _player.AddSlave(MediaSlaveType.Subtitle, uri, true);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>设置视频源 (local file path).</summary>
    public void SetVideo(string path)
    {
        Title = Path.GetFileName(path);
        _segmentHandlerDetach();
        var old = _media;
        _media = new Media(_libVlc, path, FromType.FromPath);
        _player.Media = _media;
        old?.Dispose();
        if (_subtitleFile != null)
            AddSubtitleToPlayer(_subtitleFile);
        // 隐藏提示标签
        TipLabel.IsVisible = false;
        VideoView.IsVisible = true;
        UpdateLayout(); // create the native video window now
        AttachVideoView();
        UpdatePlayButton();
    }

    /// <summary>播放视频</summary>
    public void Play() => Play(0);

    /// <summary>
    /// VideoView only hands its HWND to the player when MediaPlayer is assigned (or on Initialized),
    /// but the native window is created later (when the view first becomes visible). Re-assign to attach.
    /// </summary>
    private void AttachVideoView()
    {
        if (_player.Hwnd != IntPtr.Zero || !VideoView.IsVisible) return;
        VideoView.MediaPlayer = null;
        VideoView.MediaPlayer = _player;
    }

    private void Play(int attempt)
    {
        if (!HasMedia) return;
        // The VideoView's native window is created on the next layout pass after it became visible.
        // Starting playback before libvlc knows the HWND would make VLC open its own window.
        if (_player.Hwnd == IntPtr.Zero && attempt < 20)
        {
            AttachVideoView();
            Dispatcher.UIThread.Post(() => Play(attempt + 1), DispatcherPriority.Background);
            return;
        }
        if (_player.State == VLCState.Ended) _player.Stop(); // libvlc 3 needs a stop before replaying
        _player.Play();
    }

    /// <summary>暂停视频</summary>
    public void Pause() => _player.SetPause(true);

    /// <summary>停止视频</summary>
    public void Stop()
    {
        _segmentHandlerDetach();
        _player.Stop();
        UpdatePlayButton();
    }

    /// <summary>播放指定时间段的视频片段 (ms)</summary>
    public void PlaySegment(int startTime, int endTime)
    {
        if (!HasMedia) return;
        // 确保时间范围有效
        if (startTime < 0 || endTime > Duration || startTime >= endTime) return;

        _segmentHandlerDetach();
        EventHandler<MediaPlayerTimeChangedEventArgs>? handler = null;
        handler = (_, e) =>
        {
            // 当播放位置到达结束时间时停止播放 (libvlc must not be called from its own event thread)
            if (e.Time < endTime) return;
            Dispatcher.UIThread.Post(() =>
            {
                if (_segmentHandler != handler) return;
                Pause();
                _segmentHandlerDetach();
            });
        };
        _segmentHandler = handler;
        _player.TimeChanged += handler;

        // 设置开始位置并播放
        if (_player.State is VLCState.Playing or VLCState.Paused)
            SetPosition(startTime);
        else
            _pendingSeek = startTime;
        Play();
    }

    private void _segmentHandlerDetach()
    {
        if (_segmentHandler == null) return;
        _player.TimeChanged -= _segmentHandler;
        _segmentHandler = null;
    }

    /// <summary>toggle play state</summary>
    public void TogglePlayState()
    {
        if (IsPlaying) Pause();
        else Play();
    }

    // ------------------------------------------------------------------ SignalBus

    private void ConnectSignals()
    {
        SignalBus.VideoPlay += Play;
        SignalBus.VideoPause += Pause;
        SignalBus.VideoStop += Stop;
        SignalBus.VideoSourceChanged += SetVideo;
        SignalBus.VideoSegmentPlay += PlaySegment;
        SignalBus.VideoSubtitleAdded += AddSubtitle;
    }

    private void DisconnectSignals()
    {
        SignalBus.VideoPlay -= Play;
        SignalBus.VideoPause -= Pause;
        SignalBus.VideoStop -= Stop;
        SignalBus.VideoSourceChanged -= SetVideo;
        SignalBus.VideoSegmentPlay -= PlaySegment;
        SignalBus.VideoSubtitleAdded -= AddSubtitle;
    }

    private void Release()
    {
        DisconnectSignals();
        _updateTimer.Stop();
        VideoView.MediaPlayer = null;
        var player = _player;
        var media = _media;
        _media = null;
        // MediaPlayer.Dispose() can block while vout shuts down; do it off the UI thread.
        Task.Run(() =>
        {
            player.Dispose();
            media?.Dispose();
        });
    }

    // ------------------------------------------------------------------ input

    /// <summary>keyPressEvent + eventFilter: Space toggles, Left/Right skip 3s.</summary>
    private void OnKeyDownTunnel(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Space:
                TogglePlayState();
                e.Handled = true;
                break;
            case Key.Left:
                SkipBack(3000);
                e.Handled = true;
                break;
            case Key.Right:
                SkipForward(3000);
                e.Handled = true;
                break;
        }
    }

#pragma warning disable CS0618 // DragEventArgs.Data (IDataObject) is still the portable API in 11.3
    private static IEnumerable<string> DroppedFiles(DragEventArgs e) =>
        e.Data.GetFiles()?.Select(f => f.TryGetLocalPath()).Where(p => p != null).Cast<string>() ?? [];

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        // 检查是否为视频文件或字幕文件
        var ok = DroppedFiles(e).Any(p =>
        {
            var ext = Path.GetExtension(p).ToLowerInvariant();
            return VideoExts.Contains(ext) || SubtitleExts.Contains(ext);
        });
        e.DragEffects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        foreach (var path in DroppedFiles(e))
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (SubtitleExts.Contains(ext))
            {
                // 处理字幕文件
                AddSubtitleToPlayer(path);
            }
            else if (VideoExts.Contains(ext))
            {
                // 处理视频文件
                SetVideo(path);
                Play();
                break; // 只处理第一个视频文件
            }
        }
    }
#pragma warning restore CS0618
}
