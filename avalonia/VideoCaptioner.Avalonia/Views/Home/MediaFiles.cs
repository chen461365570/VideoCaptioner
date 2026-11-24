using Avalonia.Input;
using Avalonia.Platform.Storage;
using VideoCaptioner.Avalonia.Models;
using VideoCaptioner.Avalonia.Services;

namespace VideoCaptioner.Avalonia.Views.Home;

/// <summary>Shared helpers of the task creation / transcription pages (media file dialog filter, drop handling).</summary>
internal static class MediaFiles
{
    /// <summary>file extension (lower case, no dot) is in SupportedVideoFormats | SupportedAudioFormats.</summary>
    public static bool IsSupported(string ext) =>
        Options.SupportedVideoFormats.Contains(ext) || Options.SupportedAudioFormats.Contains(ext);

    public static string Ext(string path) => Path.GetExtension(path).TrimStart('.').ToLowerInvariant();

    /// <summary>QFileDialog.getOpenFileName(self, "选择媒体文件", desktop, "媒体文件 (...);;视频文件 (...);;音频文件 (...)").</summary>
    public static Task<string?> PickMediaAsync(string context)
    {
        var video = Options.SupportedVideoFormats.Select(f => "*." + f).ToArray();
        var audio = Options.SupportedAudioFormats.Select(f => "*." + f).ToArray();
        return Dialogs.OpenFileAsync(I18n.Tr("选择媒体文件", context),
            (I18n.Tr("媒体文件", context), video.Concat(audio).ToArray()),
            (I18n.Tr("视频文件", context), video),
            (I18n.Tr("音频文件", context), audio));
    }

#pragma warning disable CS0618 // IDataObject API (still supported in Avalonia 11.3)
    /// <summary>mimeData().hasUrls()</summary>
    public static bool HasFiles(DragEventArgs e) => e.Data.Contains(DataFormats.Files);

    /// <summary>[u.toLocalFile() for u in mimeData().urls()]</summary>
    public static IEnumerable<string> DroppedPaths(DragEventArgs e) =>
        e.Data.GetFiles()?.Select(f => f.TryGetLocalPath()).Where(p => p != null).Cast<string>() ?? [];
#pragma warning restore CS0618
}
