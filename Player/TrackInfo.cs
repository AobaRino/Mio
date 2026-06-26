namespace Mio.Player;

public sealed record TrackInfo(
    int Id,
    string Type,
    string Title,
    string Language,
    bool IsSelected);
