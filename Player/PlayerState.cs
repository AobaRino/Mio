namespace Mio.Player;

public sealed class PlayerState
{
    public bool HasMedia { get; set; }

    public bool IsPaused { get; set; }

    public bool IsIdleActive { get; set; }

    public string? CurrentFile { get; set; }

    public string MediaTitle { get; set; } = "Mio";

    public double Position { get; set; }

    public double Duration { get; set; }

    public int VideoWidth { get; set; }

    public int VideoHeight { get; set; }

    public int DisplayWidth { get; set; }

    public int DisplayHeight { get; set; }

    public double VideoAspect { get; set; }

    public double VideoAspectRatio
    {
        get
        {
            if (VideoAspect > 0)
            {
                return VideoAspect;
            }

            if (DisplayWidth > 0 && DisplayHeight > 0)
            {
                return (double)DisplayWidth / DisplayHeight;
            }

            return VideoWidth > 0 && VideoHeight > 0
                ? (double)VideoWidth / VideoHeight
                : 0;
        }
    }

    public double Volume { get; set; } = 100;

    public bool IsFullscreen { get; set; }

    public bool IsSwapChainReady { get; set; }

    public static PlayerState CreateIdle()
    {
        return new PlayerState
        {
            HasMedia = false,
            IsPaused = false,
            IsIdleActive = true,
            MediaTitle = "Mio",
            Volume = 100
        };
    }

    public PlayerState Clone()
    {
        return (PlayerState)MemberwiseClone();
    }
}
