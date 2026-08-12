using Mio.Player;

namespace Mio.Tests;

public class TimeFormatTests
{
    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(1, "0:01")]
    [InlineData(59, "0:59")]
    [InlineData(60, "1:00")]
    [InlineData(599, "9:59")]
    [InlineData(3599, "59:59")]
    public void FormatsSubHourAsMinutesAndSeconds(double seconds, string expected)
    {
        Assert.Equal(expected, TimeFormat.Format(seconds));
    }

    [Theory]
    [InlineData(3600, "1:00:00")]
    [InlineData(3661, "1:01:01")]
    [InlineData(36000, "10:00:00")]
    public void SwitchesToHoursAtOneHour(double seconds, string expected)
    {
        Assert.Equal(expected, TimeFormat.Format(seconds));
    }

    // duration 未知时 mpv 会返回这些值，不能让它们显示成乱码或抛异常
    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-1)]
    [InlineData(-3600)]
    public void FallsBackToZeroForInvalidValues(double seconds)
    {
        Assert.Equal("0:00", TimeFormat.Format(seconds));
    }

    [Fact]
    public void TruncatesFractionalSecondsRatherThanRounding()
    {
        // 59.9 秒必须显示 0:59 而不是 1:00，否则进度条会提前跳到下一分钟
        Assert.Equal("0:59", TimeFormat.Format(59.9));
    }
}
