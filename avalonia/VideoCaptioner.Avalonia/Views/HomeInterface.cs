using Avalonia;
using Avalonia.Controls;
using VideoCaptioner.Avalonia.Controls;
using VideoCaptioner.Avalonia.Services;
using VideoCaptioner.Avalonia.Views.Home;

namespace VideoCaptioner.Avalonia.Views;

/// <summary>Port of videocaptioner/ui/view/home_interface.py.</summary>
public class HomeInterface : UserControl
{
    private readonly StepBar _pivot = new();
    private readonly Panel _stackedWidget = new();
    private readonly Dictionary<string, Control> _pages = new();
    private string? _currentTaskId; // 当前流程的任务 ID

    public TaskCreationInterface TaskCreationInterface { get; } = new();
    public TranscriptionInterface TranscriptionInterface { get; } = new();
    public SubtitleInterface SubtitleOptimizationInterface { get; } = new();
    public VideoSynthesisInterface VideoSynthesisInterface { get; } = new();

    public HomeInterface()
    {
        // Workbench layout: the 4 steps of the pipeline as a stepper on top, the step content below
        var layout = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*"),
            Margin = new Thickness(28, 12, 28, 20),
        };
        // step buttons have 8px inner padding; pull the bar left so the first dot lines up with the content
        _pivot.Margin = new Thickness(-8, 0, 0, 0);
        layout.Children.Add(_pivot);
        Grid.SetRow(_stackedWidget, 1);
        _stackedWidget.Margin = new Thickness(0, 12, 0, 0);
        layout.Children.Add(_stackedWidget);
        Content = layout;

        AddSubInterface(TaskCreationInterface, "TaskCreationInterface", I18n.Tr("任务创建", "HomeInterface"));
        AddSubInterface(TranscriptionInterface, "TranscriptionInterface", I18n.Tr("语音转录", "HomeInterface"));
        AddSubInterface(SubtitleOptimizationInterface, "SubtitleInterface", I18n.Tr("字幕优化与翻译", "HomeInterface"));
        AddSubInterface(VideoSynthesisInterface, "VideoSynthesisInterface", I18n.Tr("字幕视频合成", "HomeInterface"));

        SetCurrent("TaskCreationInterface");

        TaskCreationInterface.Finished += SwitchToTranscription;
        TranscriptionInterface.Finished += SwitchToSubtitleOptimization;
        SubtitleOptimizationInterface.Finished += SwitchToVideoSynthesis;
    }

    private void AddSubInterface(Control widget, string objectName, string text)
    {
        widget.Name = objectName;
        widget.IsVisible = false;
        _pages[objectName] = widget;
        _stackedWidget.Children.Add(widget);
        _pivot.AddItem(objectName, text, () => SetCurrent(objectName));
    }

    /// <summary>Show a tab by object name (TaskCreationInterface, TranscriptionInterface, ...).</summary>
    public void ShowTab(string objectName) => SetCurrent(objectName);

    private void SetCurrent(string objectName)
    {
        foreach (var (k, p) in _pages) p.IsVisible = k == objectName;
        _pivot.SetCurrentItem(objectName);
    }

    private async void SwitchToTranscription(string filePath)
    {
        // 流程开始，生成新的 task_id
        _currentTaskId = await NewTaskIdAsync();
        TranscriptionInterface.SetTask(filePath, needNextTask: true, taskId: _currentTaskId);
        TranscriptionInterface.Process();
        SetCurrent("TranscriptionInterface");
    }

    private void SwitchToSubtitleOptimization(string filePath, string videoPath)
    {
        // 继续使用同一个 task_id
        SubtitleOptimizationInterface.SetTask(filePath, videoPath, needNextTask: true, taskId: _currentTaskId);
        SubtitleOptimizationInterface.Process();
        SetCurrent("SubtitleInterface");
    }

    private void SwitchToVideoSynthesis(string videoPath, string subtitlePath)
    {
        // 继续使用同一个 task_id，流程结束后清空
        var taskId = _currentTaskId;
        _currentTaskId = null;
        VideoSynthesisInterface.SetTask(videoPath, subtitlePath, needNextTask: true, taskId: taskId);
        VideoSynthesisInterface.Process();
        SetCurrent("VideoSynthesisInterface");
    }

    private static async Task<string?> NewTaskIdAsync()
    {
        try
        {
            return (await Bridge.Instance.RequestAsync("new_task_id"))?.GetValue<string>();
        }
        catch
        {
            return null;
        }
    }
}
