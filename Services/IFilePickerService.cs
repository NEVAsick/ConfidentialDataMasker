using System.Collections.Generic;
using System.Threading.Tasks;

namespace ConfidentialDataMasker.Services;

public interface IFilePickerService
{

    Task<string?> PickFolderAsync(string title);

    Task<IReadOnlyList<string>> PickPdfFilesAsync(string title);
}
