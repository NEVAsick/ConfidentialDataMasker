using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace ConfidentialDataMasker.Services;

public class FilePickerService : IFilePickerService
{
    private readonly TopLevel _topLevel;

    public FilePickerService(TopLevel topLevel)
    {
        _topLevel = topLevel;
    }

    public async Task<string?> PickFolderAsync(string title)
    {
        var folders = await _topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false
        });

        return folders.FirstOrDefault()?.TryGetLocalPath();
    }

    public async Task<IReadOnlyList<string>> PickPdfFilesAsync(string title)
    {
        var files = await _topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = true,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("PDF-файлы") { Patterns = new[] { "*.pdf" } }
            }
        });

        return files
            .Select(f => f.TryGetLocalPath())
            .Where(p => p is not null)
            .Select(p => p!)
            .ToList();
    }
}
