using System;
using System.Threading;
using System.Threading.Tasks;

namespace Mio.Player;

public interface IMediaPlayer : IDisposable
{
    event Action<PlayerState>? StateChanged;
    event Action<string>? ErrorOccurred;
    event Action<IntPtr>? SwapChainChanged;

    PlayerState State { get; }

    void Initialize();

    Task LoadAsync(string path, CancellationToken cancellationToken = default);

    void TogglePause();

    void SeekRelative(double seconds);

    void SeekAbsolute(double seconds);

    void SetVolume(double volume);

    void SelectAudioTrack(int trackId);

    void SelectSubtitleTrack(int trackId);

    void DisableSubtitles();

    void AutoSelectSubtitles();

    void ToggleSubtitleVisibility();

    void AddSubtitleFile(string path);

    void SetCompositionSize(int width, int height);
}
