using System.Collections.Generic;

namespace Mio.Player;

public static class TrackSelection
{
    /// <summary>
    /// 循环切换到下一条轨道，返回其 id；列表为空时返回 null。
    /// 当前轨道不在列表里（未选中、或刚被移除）时从第一条开始。
    /// </summary>
    public static int? GetNextTrackId(IReadOnlyList<TrackInfo> tracks, int? selectedTrackId)
    {
        if (tracks.Count == 0)
        {
            return null;
        }

        // 找不到时 IndexOf 返回 -1，加一后正好落在第一条。
        var nextIndex = (IndexOf(tracks, selectedTrackId) + 1) % tracks.Count;
        return tracks[nextIndex].Id;
    }

    public static int IndexOf(IReadOnlyList<TrackInfo> tracks, int? trackId)
    {
        if (trackId is null)
        {
            return -1;
        }

        for (var index = 0; index < tracks.Count; index++)
        {
            if (tracks[index].Id == trackId.Value)
            {
                return index;
            }
        }

        return -1;
    }
}
