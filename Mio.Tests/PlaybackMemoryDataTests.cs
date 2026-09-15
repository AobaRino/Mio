using Mio.Services;

namespace Mio.Tests;

public class PlaybackMemoryDataTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void RemembersMidPlaybackPosition()
    {
        var data = new PlaybackMemoryData();

        Assert.True(data.RememberPosition("a.mp4", 600, 3600, T0));
        Assert.Equal(600, data.GetResumePosition("a.mp4"));
    }

    [Fact]
    public void IgnoresPositionsBeforeMinimum()
    {
        var data = new PlaybackMemoryData();

        Assert.False(data.RememberPosition("a.mp4", 5, 3600, T0));
        Assert.Null(data.GetResumePosition("a.mp4"));
    }

    // 看完了就该从头放，不能下次停在片尾字幕上
    [Fact]
    public void ForgetsWhenNearlyFinished()
    {
        var data = new PlaybackMemoryData();
        data.RememberPosition("a.mp4", 600, 3600, T0);

        Assert.True(data.RememberPosition("a.mp4", 3500, 3600, T0));
        Assert.Null(data.GetResumePosition("a.mp4"));
    }

    // 播放开始前 duration 为 0，此时绝不能除零也不能写入垃圾
    [Fact]
    public void IgnoresUnknownDuration()
    {
        var data = new PlaybackMemoryData();

        Assert.False(data.RememberPosition("a.mp4", 600, 0, T0));
        Assert.Empty(data.Resume);
    }

    // 每 300ms 调一次，位置没实质变化时不该标脏，否则 dirty 永远为 true
    [Fact]
    public void ReportsNoChangeForSamePosition()
    {
        var data = new PlaybackMemoryData();
        data.RememberPosition("a.mp4", 600, 3600, T0);

        Assert.False(data.RememberPosition("a.mp4", 600.2, 3600, T0));
    }

    [Fact]
    public void PathLookupIsCaseInsensitive()
    {
        var data = new PlaybackMemoryData();
        data.RememberPosition(@"C:\Videos\A.mp4", 600, 3600, T0);

        Assert.Equal(600, data.GetResumePosition(@"c:\videos\a.mp4"));
    }

    [Fact]
    public void EvictsOldestWhenOverCapacity()
    {
        var data = new PlaybackMemoryData();
        for (var i = 0; i < PlaybackMemoryData.MaxResumeEntries + 5; i++)
        {
            data.RememberPosition($"{i}.mp4", 600, 3600, T0.AddSeconds(i));
        }

        Assert.Equal(PlaybackMemoryData.MaxResumeEntries, data.Resume.Count);
        Assert.Null(data.GetResumePosition("0.mp4"));
        Assert.Null(data.GetResumePosition("4.mp4"));
        Assert.NotNull(data.GetResumePosition("5.mp4"));
        Assert.NotNull(data.GetResumePosition($"{PlaybackMemoryData.MaxResumeEntries + 4}.mp4"));
    }

    [Fact]
    public void ForgetRemovesEntry()
    {
        var data = new PlaybackMemoryData();
        data.RememberPosition("a.mp4", 600, 3600, T0);

        Assert.True(data.Forget("a.mp4"));
        Assert.False(data.Forget("a.mp4"));
        Assert.Null(data.GetResumePosition("a.mp4"));
    }
}
