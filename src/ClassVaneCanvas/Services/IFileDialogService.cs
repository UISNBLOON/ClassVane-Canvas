using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace ClassVaneCanvas.Services;

/// <summary>文件选择（导入 / 导出 .cvcanno 用），实现位于视图层。</summary>
public interface IFileDialogService
{
    /// <summary>选择要打开的文件，取消返回 <c>null</c>。</summary>
    Task<string?> PickOpenFileAsync(string title, string extension, CancellationToken cancellationToken = default);

    /// <summary>选择保存位置，取消返回 <c>null</c>。</summary>
    Task<string?> PickSaveFileAsync(
        string title,
        string suggestedFileName,
        string extension,
        CancellationToken cancellationToken = default);
}

/// <summary>基于 Avalonia StorageProvider 的实现（用主窗口作为宿主）。</summary>
public sealed class StorageProviderFileDialogService : IFileDialogService
{
    private readonly Func<Window?> _hostAccessor;

    public StorageProviderFileDialogService(Func<Window?> hostAccessor)
        => _hostAccessor = hostAccessor ?? throw new ArgumentNullException(nameof(hostAccessor));

    public async Task<string?> PickOpenFileAsync(
        string title,
        string extension,
        CancellationToken cancellationToken = default)
    {
        var host = _hostAccessor();
        if (host is null)
        {
            return null;
        }

        var files = await host.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = new[] { BuildFileType(extension) },
        });

        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickSaveFileAsync(
        string title,
        string suggestedFileName,
        string extension,
        CancellationToken cancellationToken = default)
    {
        var host = _hostAccessor();
        if (host is null)
        {
            return null;
        }

        var file = await host.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedFileName,
            DefaultExtension = extension.TrimStart('.'),
            FileTypeChoices = new[] { BuildFileType(extension) },
            ShowOverwritePrompt = true,
        });

        return file?.TryGetLocalPath();
    }

    private static FilePickerFileType BuildFileType(string extension)
    {
        var normalized = extension.StartsWith('.') ? extension : "." + extension;
        return new FilePickerFileType($"ClassVane Canvas 批注包 (*{normalized})")
        {
            Patterns = new[] { "*" + normalized },
        };
    }
}
