using System.Collections.Generic;
using System.Linq;

namespace Mio.Player;

public enum MediaTrackType
{
    Audio,
    Subtitle
}

public sealed class TrackInfo
{
    public MediaTrackType Type { get; set; }

    public int Id { get; set; }

    public string DisplayName { get; set; } = string.Empty;

    public string? Title { get; set; }

    public string? Language { get; set; }

    public string? Codec { get; set; }

    public bool IsSelected { get; set; }

    public bool IsExternal { get; set; }

    public bool IsDefault { get; set; }

    public bool IsForced { get; set; }

    public static string BuildDisplayName(
        MediaTrackType type,
        int id,
        string? title,
        string? language,
        string? codec,
        bool isExternal,
        bool isDefault,
        bool isForced)
    {
        var name = FirstNonEmpty(title, language, codec)
            ?? (type == MediaTrackType.Audio ? $"Audio Track {id}" : $"Subtitle Track {id}");

        var badges = new List<string>();
        if (isExternal)
        {
            badges.Add("External");
        }

        if (isForced)
        {
            badges.Add("Forced");
        }

        if (isDefault)
        {
            badges.Add("Default");
        }

        return badges.Count == 0
            ? name
            : $"{name} ({string.Join(", ", badges)})";
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        return values
            .Select(value => value?.Trim())
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }
}
