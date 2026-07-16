namespace Mio.Player;

public static class MpvProperty
{
    public const string DisplaySwapChain = "display-swapchain";
    public const string D3D11CompositionSize = "d3d11-composition-size";
    public const string Pause = "pause";
    public const string TimePosition = "time-pos";
    public const string Duration = "duration";
    public const string VideoWidth = "width";
    public const string VideoHeight = "height";
    public const string VideoAspect = "video-params/aspect";
    public const string DisplayWidth = "dwidth";
    public const string DisplayHeight = "dheight";
    public const string Volume = "volume";
    public const string IdleActive = "idle-active";
    public const string EofReached = "eof-reached";
    public const string MediaTitle = "media-title";
    public const string TrackListCount = "track-list/count";
    public const string Aid = "aid";
    public const string Sid = "sid";
    public const string SubVisibility = "sub-visibility";

    public static string TrackListProperty(int index, string name)
    {
        return $"track-list/{index}/{name}";
    }
}
