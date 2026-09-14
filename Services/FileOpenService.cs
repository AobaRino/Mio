using System.Threading.Tasks;
using Mio.Player;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace Mio.Services;

public static class FileOpenService
{
    /// <summary>
    /// 拖放数据里能播的第一项：优先本地文件，其次浏览器拖来的链接。
    /// </summary>
    public static async Task<string?> TryGetFirstMediaSourceAsync(DataPackageView dataView)
    {
        if (dataView.Contains(StandardDataFormats.StorageItems))
        {
            var items = await dataView.GetStorageItemsAsync();
            foreach (var item in items)
            {
                if (item is StorageFile file && !string.IsNullOrWhiteSpace(file.Path))
                {
                    return file.Path;
                }
            }
        }

        // 从浏览器地址栏或链接拖过来时给的是 WebLink，纯文本拖放则落到 Text。
        if (dataView.Contains(StandardDataFormats.WebLink))
        {
            var link = await dataView.GetWebLinkAsync();
            return link.AbsoluteUri;
        }

        if (dataView.Contains(StandardDataFormats.Text))
        {
            var text = (await dataView.GetTextAsync())?.Trim();
            if (MediaSource.IsRemote(text ?? string.Empty))
            {
                return text;
            }
        }

        return null;
    }

    /// <summary>拖放过程中判断是否接受，必须是同步的。</summary>
    public static bool CanAccept(DataPackageView dataView)
    {
        return dataView.Contains(StandardDataFormats.StorageItems)
            || dataView.Contains(StandardDataFormats.WebLink)
            || dataView.Contains(StandardDataFormats.Text);
    }
}
