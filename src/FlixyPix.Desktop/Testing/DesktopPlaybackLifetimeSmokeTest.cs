using System.IO;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

public partial class MainWindow
{
    private async Task RunPlaybackLifetimeSmokeAsync(MediaItem original, string directory)
    {
        await StopPlayer();
        var copy = Path.Combine(directory, "fixtures", "playback-lifetime-copy.mp4");
        File.Copy(original.Path, copy);
        var second = _repository.Upsert(new MediaItem { Path = copy, Kind = MediaKind.Video, Title = "Synthetic second playback", DurationSeconds = original.DurationSeconds });
        try
        {
            var a = Play(original, 0);
            var b = Play(second, 0);
            await Task.WhenAll(a, b);
            if (_playingId != second.Id || _playback.Current?.Window != _videoWindow || _videoWindow?.HasPlayer != true)
                throw new InvalidOperationException("Overlapping player starts did not retain the last requested session.");
            var firstStop = StopPlayer();
            var stopping = !firstStop.IsCompleted;
            var repeatedStop = StopPlayer();
            if (stopping && !ReferenceEquals(firstStop, repeatedStop)) throw new InvalidOperationException("Repeated stop did not share cleanup.");
            await Task.WhenAll(firstStop, repeatedStop);
            if (_player is not null || _videoWindow is not null || _repository.GetHistory(second.Id).Count(h => h.Kind == HistoryKind.Play) != 1)
                throw new InvalidOperationException("Playback cleanup lost resources or finalized history more than once.");
            File.WriteAllText(Path.Combine(directory, "playback-lifetime-result.txt"), "PASS: overlapping native starts, shared stop and single history finalization.");
        }
        finally { await StopPlayer(); _repository.Delete(second.Id); File.Delete(copy); }
    }
}
