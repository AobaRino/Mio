using Mio.Player;

namespace Mio.Tests;

public class TrackInfoTests
{
    private static string Build(
        MediaTrackType type = MediaTrackType.Audio,
        int id = 1,
        string? title = null,
        string? language = null,
        string? codec = null,
        bool isExternal = false,
        bool isDefault = false,
        bool isForced = false)
    {
        return TrackInfo.BuildDisplayName(type, id, title, language, codec, isExternal, isDefault, isForced);
    }

    [Fact]
    public void PrefersTitleOverLanguageAndCodec()
    {
        Assert.Equal("Director Commentary", Build(title: "Director Commentary", language: "eng", codec: "aac"));
    }

    [Fact]
    public void FallsBackToLanguageWhenTitleMissing()
    {
        Assert.Equal("jpn", Build(language: "jpn", codec: "aac"));
    }

    [Fact]
    public void FallsBackToCodecWhenTitleAndLanguageMissing()
    {
        Assert.Equal("aac", Build(codec: "aac"));
    }

    [Theory]
    [InlineData(MediaTrackType.Audio, 3, "Audio Track 3")]
    [InlineData(MediaTrackType.Subtitle, 5, "Subtitle Track 5")]
    public void FallsBackToTypeAndIdWhenNothingKnown(MediaTrackType type, int id, string expected)
    {
        Assert.Equal(expected, Build(type, id));
    }

    // mpv 对缺失字段返回空串而不是 null，两者都要当作"没有"
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void TreatsBlankTitleAsMissing(string title)
    {
        Assert.Equal("eng", Build(title: title, language: "eng"));
    }

    [Fact]
    public void TrimsWhitespaceAroundName()
    {
        Assert.Equal("Commentary", Build(title: "  Commentary  "));
    }

    [Fact]
    public void AppendsBadgesInFixedOrder()
    {
        Assert.Equal(
            "eng (External, Forced, Default)",
            Build(language: "eng", isExternal: true, isDefault: true, isForced: true));
    }

    [Fact]
    public void AppendsOnlyTheBadgesThatApply()
    {
        Assert.Equal("eng (Default)", Build(language: "eng", isDefault: true));
    }

    [Fact]
    public void OmitsBracketsWhenNoBadges()
    {
        Assert.Equal("eng", Build(language: "eng"));
    }
}
