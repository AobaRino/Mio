using Mio.Player;

namespace Mio.Tests;

public class VideoLayoutTests
{
    private const double Tolerance = 0.001;

    [Fact]
    public void NoInsetWhenPanelMatchesVideoAspect()
    {
        Assert.Equal(0, VideoLayout.CalculateHorizontalInset(1920, 1080, 16.0 / 9.0), Tolerance);
    }

    [Fact]
    public void InsetsBothSidesWhenPanelIsWiderThanVideo()
    {
        // 4:3 视频放进 1920x1080 面板：可见宽度 1440，两侧各留 240
        Assert.Equal(240, VideoLayout.CalculateHorizontalInset(1920, 1080, 4.0 / 3.0), Tolerance);
    }

    [Fact]
    public void NoInsetWhenPanelIsTallerThanVideo()
    {
        // 上下黑边不影响水平方向
        Assert.Equal(0, VideoLayout.CalculateHorizontalInset(1000, 1000, 16.0 / 9.0), Tolerance);
    }

    // 加载中、切换文件的瞬间这些值都可能为 0，绝不能算出 NaN 或负数
    [Theory]
    [InlineData(0, 1080, 1.777)]
    [InlineData(1920, 0, 1.777)]
    [InlineData(1920, 1080, 0)]
    [InlineData(-1920, 1080, 1.777)]
    public void ReturnsZeroForDegenerateInput(double width, double height, double aspect)
    {
        Assert.Equal(0, VideoLayout.CalculateHorizontalInset(width, height, aspect), Tolerance);
    }

    [Fact]
    public void InsetNeverExceedsHalfThePanel()
    {
        // 极窄视频塞进极宽面板，内缩仍必须留下正的可见宽度
        var inset = VideoLayout.CalculateHorizontalInset(4000, 1000, 0.01);
        Assert.InRange(inset, 0, 2000);
    }
}
