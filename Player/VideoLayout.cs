namespace Mio.Player;

public static class VideoLayout
{
    /// <summary>
    /// 视频按比例居中铺进面板后，左右黑边各自的宽度。
    /// 底部控件据此内缩，避免视频有黑边时控件贴到窗口边缘而不是画面边缘。
    /// 面板比视频更高（上下黑边）时返回 0——那种情况不影响水平方向。
    /// </summary>
    public static double CalculateHorizontalInset(double panelWidth, double panelHeight, double videoAspectRatio)
    {
        if (panelWidth <= 0 || panelHeight <= 0 || videoAspectRatio <= 0)
        {
            return 0;
        }

        var panelAspectRatio = panelWidth / panelHeight;
        if (panelAspectRatio <= videoAspectRatio)
        {
            return 0;
        }

        var visibleVideoWidth = panelHeight * videoAspectRatio;
        return (panelWidth - visibleVideoWidth) / 2;
    }
}
