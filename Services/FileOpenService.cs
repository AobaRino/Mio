using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace Mio.Services;

public static class FileOpenService
{
    public static async Task<string?> TryGetFirstFilePathAsync(DataPackageView dataView)
    {
        if (!dataView.Contains(StandardDataFormats.StorageItems))
        {
            return null;
        }

        var items = await dataView.GetStorageItemsAsync();
        foreach (var item in items)
        {
            if (item is StorageFile file && !string.IsNullOrWhiteSpace(file.Path))
            {
                return file.Path;
            }
        }

        return null;
    }
}
