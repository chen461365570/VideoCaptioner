using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using FluentAvalonia.UI.Controls;

namespace VideoCaptioner.Avalonia.Services;

/// <summary>Helpers replacing qfluentwidgets MessageBox / MessageBoxBase and QFileDialog.</summary>
public static class Dialogs
{
    public static Window? MainWindow =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    public static TopLevel? Top => MainWindow;

    /// <summary>
    /// qfluentwidgets MessageBox(title, content): returns true when the yes button is clicked.
    /// Pass cancelText=null to hide the cancel button (w.cancelButton.hide()).
    /// </summary>
    public static async Task<bool> MessageBoxAsync(string title, string content,
        string? yesText = null, string? cancelText = "", bool selectableContent = true)
    {
        var dlg = new ContentDialog
        {
            Title = title,
            Content = new SelectableTextBlock
            {
                Text = content,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 460,
                IsEnabled = selectableContent,
            },
            PrimaryButtonText = yesText ?? I18n.Tr("确定"),
            CloseButtonText = cancelText == null ? "" : (cancelText.Length == 0 ? I18n.Tr("取消") : cancelText),
            DefaultButton = ContentDialogButton.Primary,
        };
        return await dlg.ShowAsync() == ContentDialogResult.Primary;
    }

    /// <summary>MessageBoxBase-like dialog with arbitrary content (for custom setting dialogs).</summary>
    public static async Task<bool> ShowContentAsync(string title, Control content,
        string? yesText = null, string? cancelText = "")
    {
        var dlg = new ContentDialog
        {
            Title = title,
            Content = content,
            PrimaryButtonText = yesText ?? I18n.Tr("确定"),
            CloseButtonText = cancelText == null ? "" : (cancelText.Length == 0 ? I18n.Tr("取消") : cancelText),
            DefaultButton = ContentDialogButton.Primary,
        };
        return await dlg.ShowAsync() == ContentDialogResult.Primary;
    }

    /// <summary>QFileDialog.getOpenFileName(s). Filters: ("视频文件", ["*.mp4", "*.mkv"]).</summary>
    public static async Task<IReadOnlyList<string>> OpenFilesAsync(string title, bool multiple,
        params (string Name, string[] Patterns)[] filters)
    {
        if (Top is null) return [];
        var files = await Top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = multiple,
            FileTypeFilter = filters.Select(f => new FilePickerFileType(f.Name) { Patterns = f.Patterns }).ToList(),
        });
        return files.Select(f => f.TryGetLocalPath()).Where(p => p != null).Cast<string>().ToList();
    }

    public static async Task<string?> OpenFileAsync(string title, params (string Name, string[] Patterns)[] filters)
        => (await OpenFilesAsync(title, false, filters)).FirstOrDefault();

    /// <summary>QFileDialog.getSaveFileName.</summary>
    public static async Task<string?> SaveFileAsync(string title, string suggestedName,
        params (string Name, string[] Patterns)[] filters)
    {
        if (Top is null) return null;
        var file = await Top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            FileTypeChoices = filters.Select(f => new FilePickerFileType(f.Name) { Patterns = f.Patterns }).ToList(),
        });
        return file?.TryGetLocalPath();
    }

    /// <summary>QFileDialog.getExistingDirectory.</summary>
    public static async Task<string?> OpenFolderAsync(string title, string? startDir = null)
    {
        if (Top is null) return null;
        IStorageFolder? start = null;
        if (!string.IsNullOrEmpty(startDir) && Directory.Exists(startDir))
            start = await Top.StorageProvider.TryGetFolderFromPathAsync(startDir);
        var dirs = await Top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            SuggestedStartLocation = start,
        });
        return dirs.FirstOrDefault()?.TryGetLocalPath();
    }

    /// <summary>QDesktopServices.openUrl for http(s) urls and local files/folders.</summary>
    public static void OpenUrl(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception e)
        {
            Toast.Error(I18n.Tr("打开失败"), e.Message);
        }
    }

    /// <summary>Open the folder containing a file (or the folder itself).</summary>
    public static void OpenFolder(string path)
    {
        var dir = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) OpenUrl(dir);
        else Toast.Warning(I18n.Tr("警告"), I18n.Tr("文件夹不存在"));
    }
}
