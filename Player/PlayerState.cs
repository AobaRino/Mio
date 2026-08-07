namespace Mio.Player;

public sealed class PlayerState
{
    public bool HasMedia { get; set; }

    public bool IsPaused { get; set; }

    public bool IsIdleActive { get; set; }

    public bool IsEndOfFile { get; set; }

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

    public bool IsMuted { get; set; }

    public IReadOnlyList<TrackInfo> AudioTracks { get; set; } = Array.Empty<TrackInfo>();

    public IReadOnlyList<TrackInfo> SubtitleTracks { get; set; } = Array.Empty<TrackInfo>();

    public int? SelectedAudioTrackId { get; set; }

    public int? SelectedSubtitleTrackId { get; set; }

    public bool SubtitlesVisible { get; set; }

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

    // 切换文件时只保留播放器级设置，其余全部回到未知状态，避免新文件就绪前
    // overlay 仍在显示上一个文件的时长、轨道和宽高比。这里显式列出的是要
    // 保留的字段，新增字段默认会被重置。
    public PlayerState CloneForNewMedia(string path, string title)
    {
        return new PlayerState
        {
            CurrentFile = path,
            MediaTitle = title,
            HasMedia = true,
            IsIdleActive = false,
            IsPaused = IsPaused,
            Volume = Volume,
            IsMuted = IsMuted,
            IsFullscreen = IsFullscreen
        };
    }
}
