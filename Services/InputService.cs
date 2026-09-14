namespace Mio.Services;

public static class InputService
{
    public const double SeekStepSeconds = 5;
    public const double LargeSeekStepSeconds = 10;
    public const double VolumeStep = 5;

    // FileOpenPicker 的过滤列表。mpv 能打开的远不止这些，其余格式走拖放即可；
    // 这里只列日常会遇到的，让对话框默认就把无关文件藏起来。
    public static readonly string[] MediaFileExtensions =
    {
        ".mp4", ".mkv", ".mov", ".avi", ".webm", ".m4v", ".wmv", ".flv",
        ".ts", ".m2ts", ".mpg", ".mpeg", ".vob",
        ".mp3", ".flac", ".aac", ".m4a", ".ogg", ".opus", ".wav"
    };
}
