using Mio.Player;

namespace Mio.Tests;

public class PlayerStateTests
{
    [Fact]
    public void AspectRatioPrefersVideoParams()
    {
        var state = new PlayerState
        {
            VideoAspect = 2.35,
            DisplayWidth = 1920,
            DisplayHeight = 1080,
            VideoWidth = 640,
            VideoHeight = 480
        };

        Assert.Equal(2.35, state.VideoAspectRatio, 0.001);
    }

    [Fact]
    public void AspectRatioFallsBackToDisplaySize()
    {
        var state = new PlayerState
        {
            DisplayWidth = 1920,
            DisplayHeight = 1080,
            VideoWidth = 640,
            VideoHeight = 480
        };

        Assert.Equal(1920.0 / 1080.0, state.VideoAspectRatio, 0.001);
    }

    [Fact]
    public void AspectRatioFallsBackToVideoSize()
    {
        var state = new PlayerState { VideoWidth = 640, VideoHeight = 480 };

        Assert.Equal(640.0 / 480.0, state.VideoAspectRatio, 0.001);
    }

    [Fact]
    public void AspectRatioIsZeroWhenNothingKnown()
    {
        Assert.Equal(0, PlayerState.CreateIdle().VideoAspectRatio, 0.001);
    }

    // 这条是 CloneForNewMedia 的契约：显式列出的才保留，其余一律重置。
    // 新增字段若忘了考虑，默认会落在"重置"这一侧，这个测试守的就是这个边界。
    [Fact]
    public void CloneForNewMediaKeepsOnlyPlayerLevelSettings()
    {
        var previous = new PlayerState
        {
            IsPaused = true,
            Volume = 42,
            IsMuted = true,
            CurrentFile = @"C:\old.mp4",
            MediaTitle = "old",
            Position = 123,
            Duration = 456,
            VideoWidth = 1920,
            VideoHeight = 1080,
            DisplayWidth = 1920,
            DisplayHeight = 1080,
            VideoAspect = 1.777,
            SubtitlesVisible = true,
            SelectedAudioTrackId = 3,
            SelectedSubtitleTrackId = 4,
            AudioTracks = new[] { new TrackInfo { Id = 1 } },
            SubtitleTracks = new[] { new TrackInfo { Id = 2 } },
            IsSwapChainReady = true,
            IsVideoReady = true,
            IsEndOfFile = true
        };

        var next = previous.CloneForNewMedia(@"C:\new.mp4", "new");

        // 播放器级设置跨文件延续
        Assert.True(next.IsPaused);
        Assert.Equal(42, next.Volume);
        Assert.True(next.IsMuted);

        Assert.Equal(@"C:\new.mp4", next.CurrentFile);
        Assert.Equal("new", next.MediaTitle);
        Assert.True(next.HasMedia);
        Assert.False(next.IsIdleActive);

        // 其余必须全部回到未知状态，否则切片瞬间会显示上一个文件的信息
        Assert.Equal(0, next.Position);
        Assert.Equal(0, next.Duration);
        Assert.Equal(0, next.VideoWidth);
        Assert.Equal(0, next.VideoHeight);
        Assert.Equal(0, next.DisplayWidth);
        Assert.Equal(0, next.DisplayHeight);
        Assert.Equal(0, next.VideoAspect);
        Assert.Equal(0, next.VideoAspectRatio);
        Assert.False(next.SubtitlesVisible);
        Assert.Null(next.SelectedAudioTrackId);
        Assert.Null(next.SelectedSubtitleTrackId);
        Assert.Empty(next.AudioTracks);
        Assert.Empty(next.SubtitleTracks);
        Assert.False(next.IsSwapChainReady);
        Assert.False(next.IsVideoReady);
        Assert.False(next.IsEndOfFile);
    }

    [Fact]
    public void CloneDoesNotAliasMutations()
    {
        var original = new PlayerState { Volume = 50 };
        var clone = original.Clone();

        clone.Volume = 80;

        Assert.Equal(50, original.Volume);
    }
}
