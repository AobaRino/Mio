using Mio.Player;

namespace Mio.Tests;

public class MediaSourceTests
{
    [Theory]
    [InlineData("https://example.com/video.mp4")]
    [InlineData("http://example.com/stream")]
    [InlineData("rtsp://camera.local/live")]
    [InlineData("rtmp://server/app/stream")]
    public void RecognizesRemoteSources(string source)
    {
        Assert.True(MediaSource.IsRemote(source));
    }

    // 盘符路径和 UNC 都会被 Uri 解析成 file scheme，必须归入本地
    [Theory]
    [InlineData(@"C:\videos\clip.mp4")]
    [InlineData(@"\\server\share\clip.mkv")]
    [InlineData("relative/clip.mp4")]
    [InlineData("clip.mp4")]
    [InlineData("")]
    [InlineData("   ")]
    public void TreatsPathsAsLocal(string source)
    {
        Assert.False(MediaSource.IsRemote(source));
    }

    [Fact]
    public void NormalizeLeavesRemoteSourceUntouched()
    {
        const string url = "https://example.com/a%20b/video.mp4?token=1";
        Assert.Equal(url, MediaSource.Normalize(url));
    }

    [Fact]
    public void NormalizeExpandsLocalPathToFullPath()
    {
        var normalized = MediaSource.Normalize("clip.mp4");

        Assert.True(Path.IsPathFullyQualified(normalized));
        Assert.EndsWith("clip.mp4", normalized);
    }

    [Fact]
    public void DisplayNameUsesFileNameForLocalPath()
    {
        Assert.Equal("clip.mp4", MediaSource.GetDisplayName(@"C:\videos\clip.mp4"));
    }

    [Fact]
    public void DisplayNameUsesLastUrlSegment()
    {
        Assert.Equal("video.mp4", MediaSource.GetDisplayName("https://example.com/media/video.mp4"));
    }

    [Fact]
    public void DisplayNameDecodesPercentEscapes()
    {
        Assert.Equal("my video.mp4", MediaSource.GetDisplayName("https://example.com/my%20video.mp4"));
    }

    [Fact]
    public void DisplayNameFallsBackToHostWhenUrlHasNoPath()
    {
        Assert.Equal("example.com", MediaSource.GetDisplayName("https://example.com/"));
    }

    [Fact]
    public void DisplayNameHandlesEmptyInput()
    {
        Assert.Equal("Mio", MediaSource.GetDisplayName(""));
    }
}
