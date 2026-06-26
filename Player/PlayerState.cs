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
