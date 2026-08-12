using System.Globalization;
using Mio.Interop;
using Mio.Player;

namespace Mio.Tests;

public class MpvCommandTests
{
    [Fact]
    public void LoadFileReplacesCurrentMedia()
    {
        Assert.Equal(new[] { "loadfile", @"C:\video.mp4", "replace" }, MpvCommand.LoadFile(@"C:\video.mp4"));
    }

    [Fact]
    public void SeekAppendsExactFlag()
    {
        Assert.Equal(new[] { "seek", "12.5", "absolute+exact" }, MpvCommand.Seek(12.5, "absolute"));
    }

    [Fact]
    public void SeekSupportsNegativeRelativeOffsets()
    {
        Assert.Equal(new[] { "seek", "-5", "relative+exact" }, MpvCommand.Seek(-5, "relative"));
    }

    // mpv 只认小数点；若跟随系统区域设置输出逗号，seek 会被解析成两个参数
    [Fact]
    public void SeekUsesInvariantDecimalSeparator()
    {
        var original = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal("12.5", MpvCommand.Seek(12.5, "absolute")[1]);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = original;
        }
    }

    [Fact]
    public void SubAddSelectsByDefault()
    {
        Assert.Equal(new[] { "sub-add", @"C:\sub.srt", "select" }, MpvCommand.SubAdd(@"C:\sub.srt"));
    }

    [Theory]
    [InlineData(0, "0x00000000")]
    [InlineData(unchecked((int)0x80004005), "0x80004005")]
    [InlineData(unchecked((int)0x80004002), "0x80004002")]
    public void FormatsHResultAsEightHexDigits(int hresult, string expected)
    {
        Assert.Equal(expected, ComHelpers.FormatHResult(hresult));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(unchecked((int)0x80004005), true)]
    public void FailedFollowsHResultSignBit(int hresult, bool expected)
    {
        Assert.Equal(expected, ComHelpers.Failed(hresult));
    }

    [Fact]
    public void TrackListPropertyBuildsIndexedPath()
    {
        Assert.Equal("track-list/2/lang", MpvProperty.TrackListProperty(2, "lang"));
    }
}
