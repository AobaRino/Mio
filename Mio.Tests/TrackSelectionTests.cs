using Mio.Player;

namespace Mio.Tests;

public class TrackSelectionTests
{
    private static IReadOnlyList<TrackInfo> Tracks(params int[] ids)
    {
        return ids.Select(id => new TrackInfo { Id = id, Type = MediaTrackType.Audio }).ToList();
    }

    [Fact]
    public void ReturnsNullWhenNoTracks()
    {
        Assert.Null(TrackSelection.GetNextTrackId(Tracks(), 1));
    }

    [Fact]
    public void AdvancesToNextTrack()
    {
        Assert.Equal(2, TrackSelection.GetNextTrackId(Tracks(1, 2, 3), 1));
    }

    [Fact]
    public void WrapsAroundFromLastToFirst()
    {
        Assert.Equal(1, TrackSelection.GetNextTrackId(Tracks(1, 2, 3), 3));
    }

    [Fact]
    public void StartsFromFirstWhenNothingSelected()
    {
        Assert.Equal(1, TrackSelection.GetNextTrackId(Tracks(1, 2, 3), null));
    }

    [Fact]
    public void StartsFromFirstWhenSelectedTrackIsGone()
    {
        // 切换文件后旧的选中 id 可能已经不在新列表里
        Assert.Equal(1, TrackSelection.GetNextTrackId(Tracks(1, 2, 3), 99));
    }

    [Fact]
    public void SingleTrackStaysOnItself()
    {
        Assert.Equal(7, TrackSelection.GetNextTrackId(Tracks(7), 7));
    }

    [Fact]
    public void HandlesNonContiguousIds()
    {
        // mpv 的轨道 id 不保证连续，不能拿 id 当索引用
        Assert.Equal(10, TrackSelection.GetNextTrackId(Tracks(3, 10, 42), 3));
        Assert.Equal(3, TrackSelection.GetNextTrackId(Tracks(3, 10, 42), 42));
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(3, 2)]
    [InlineData(99, -1)]
    [InlineData(null, -1)]
    public void IndexOfLocatesTrack(int? trackId, int expected)
    {
        Assert.Equal(expected, TrackSelection.IndexOf(Tracks(1, 2, 3), trackId));
    }
}
