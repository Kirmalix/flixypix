using System.IO;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

public partial class MainWindow
{
    internal async Task RunPlaybackStateProbeAsync(string directory)
    {
        ConfigureSmokeTools();
        var fixtures=Path.Combine(directory,"fixtures"); Directory.CreateDirectory(fixtures);
        var path=Path.Combine(fixtures,"synthetic.mp4");
        var info=new System.Diagnostics.ProcessStartInfo(_settings.Ffmpeg!) { UseShellExecute=false, CreateNoWindow=true, RedirectStandardError=true };
        foreach(var argument in new[] { "-hide_banner","-loglevel","error","-nostdin","-n","-f","lavfi","-i","testsrc=size=160x90:rate=10","-t","1","-c:v","libx264","-pix_fmt","yuv420p",path }) info.ArgumentList.Add(argument);
        using(var process=System.Diagnostics.Process.Start(info)!)
        {
            var error=process.StandardError.ReadToEndAsync(); await process.WaitForExitAsync();
            if(process.ExitCode!=0) throw new InvalidOperationException(await error);
        }
        var item=_repository.Upsert(new MediaItem { Path=path, Kind=MediaKind.Video, Title="Synthetic state probe", DurationSeconds=1 });
        try
        {
            await RunPlaybackLifetimeSmokeAsync(item,directory);
            await Play(item,0); var video=_videoWindow!; var native=_player!;
            await native.SeekAsync(.9); await native.PauseAsync(false);
            using(var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(15))) await native.WaitForEndAsync(timeout.Token);
            await video.ToggleStillsModeAsync(); await video.StepFramesAsync(-1); await video.ToggleStillsModeAsync();
            await video.SeekToAsync(.1);
            if(await native.GetPausedAsync()!=true) throw new InvalidOperationException("Leaving stills mode retained automatic EOF resume.");
        }
        finally { await StopPlayer(); }
    }

    private async Task RunPlaybackLifetimeSmokeAsync(MediaItem original, string directory)
    {
        var detailsCollapsedBefore=_settings.DetailsCollapsed;
        await StopPlayer();
        var copy = Path.Combine(directory, "fixtures", "playback-lifetime-copy.mp4");
        File.Copy(original.Path, copy);
        var second = _repository.Upsert(new MediaItem { Path = copy, Kind = MediaKind.Video, Title = "Synthetic second playback", DurationSeconds = original.DurationSeconds });
        try
        {
            GoHome(); await _lastRefresh;
            var a = Play(original, 0);
            var b = Play(second, 0);
            await Task.WhenAll(a, b);
            if (_playingId != second.Id || _playback.Current?.Window != _videoWindow || _videoWindow?.HasPlayer != true)
                throw new InvalidOperationException("Overlapping player starts did not retain the last requested session.");
            if (_view != "Video" || SelectedMedia.Count != 1 || Selected?.Id != second.Id || !MediaList.SelectedItems.Cast<MediaCard>().Any(c => c.Item.Id == second.Id))
                throw new InvalidOperationException("Successful overlapping playback did not select and reveal only the current video.");
            var video = _videoWindow!;
            var native = _player!;
            video.Repeat=false;
            await native.SeekAsync(Math.Max(0, (second.DurationSeconds ?? 2)-.2));
            await native.PauseAsync(false);
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15))) await native.WaitForEndAsync(timeout.Token);
            for(var attempt=0;attempt<100 && !video.IsPaused;attempt++) { await video.PollOnceAsync(); await Task.Delay(20); }
            if (!video.IsPaused) throw new InvalidOperationException("EOF did not synchronize the pause state.");
            await video.SeekToAsync(0);
            if (await native.GetPausedAsync()!=false) throw new InvalidOperationException("Seeking back from EOF did not resume.");
            await video.TogglePauseAsync();
            await video.SeekToAsync(.1);
            if (await native.GetPausedAsync()!=true) throw new InvalidOperationException("Seeking out of a user pause resumed unexpectedly.");
            await native.SeekAsync(Math.Max(0,(second.DurationSeconds ?? 2)-.1)); await native.PauseAsync(false);
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15))) await native.WaitForEndAsync(timeout.Token);
            await video.TogglePauseAsync();
            if (await native.GetPausedAsync()!=false || await native.GetPositionAsync() is not < .8)
                throw new InvalidOperationException("Play at EOF did not restart the video.");
            await video.PauseForPrivacyAsync(); await video.SeekToAsync(.1);
            if (await native.GetPausedAsync()!=true) throw new InvalidOperationException("A seek resumed the privacy pause.");
            var firstStop = StopPlayer();
            var stopping = !firstStop.IsCompleted;
            var repeatedStop = StopPlayer();
            if (stopping && !ReferenceEquals(firstStop, repeatedStop)) throw new InvalidOperationException("Repeated stop did not share cleanup.");
            await Task.WhenAll(firstStop, repeatedStop);
            if (_player is not null || _videoWindow is not null || _repository.GetHistory(second.Id).Count(h => h.Kind == HistoryKind.Play) != 1)
                throw new InvalidOperationException("Playback cleanup lost resources or finalized history more than once.");
            foreach(var collapsed in new[]{false,true})
            {
                await Play(second, 0);
                _settings=_settings with{DetailsCollapsed=collapsed};_detailsAutoFolded=false;SetDetails(!collapsed);
                await Dispatcher.InvokeAsync(()=>{},System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                var pane=DetailsPane.IsVisible;var rail=DetailsRail.IsVisible;var width=DetailsColumn.Width;
                var flickered=false;
                System.Windows.DependencyPropertyChangedEventHandler visibility=(_,_)=>flickered=true;
                EventHandler resized=(_,_)=>{if(DetailsColumn.Width!=width)flickered=true;};
                var descriptor=System.ComponentModel.DependencyPropertyDescriptor.FromProperty(System.Windows.Controls.ColumnDefinition.WidthProperty,typeof(System.Windows.Controls.ColumnDefinition));
                DetailsPane.IsVisibleChanged+=visibility;DetailsRail.IsVisibleChanged+=visibility;descriptor.AddValueChanged(DetailsColumn,resized);
                try
                {
                    _videoWindow!.Close();
                    for (var attempt=0; attempt<100 && (_videoWindow!=null || !MediaList.IsKeyboardFocusWithin); attempt++) await Task.Delay(50);
                    await Task.Delay(350);
                    if (_videoWindow!=null || !MediaList.IsKeyboardFocusWithin || Selected?.Id!=second.Id)
                        throw new InvalidOperationException("Closing the player did not return focus to its tile.");
                    if(flickered || DetailsPane.IsVisible!=pane || DetailsRail.IsVisible!=rail || DetailsColumn.Width!=width)
                        throw new InvalidOperationException($"Closing the player rebuilt the {(collapsed?"collapsed":"expanded")} inspector.");
                }
                finally{DetailsPane.IsVisibleChanged-=visibility;DetailsRail.IsVisibleChanged-=visibility;descriptor.RemoveValueChanged(DetailsColumn,resized);}
            }
            var asked=false;
            var cancel=Dispatcher.BeginInvoke(() =>
            {
                var dialog=System.Windows.Application.Current.Windows.OfType<MessageWindow>().FirstOrDefault();
                if(dialog!=null) { asked=dialog.Title==T("Delete file") || dialog.Title==T("Permanently delete file"); dialog.Choices.First(button=>button.IsCancel).RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); }
            }, System.Windows.Threading.DispatcherPriority.Loaded);
            var keyboard=new byte[256]; GetKeyboardState(keyboard); var shifted=(byte[])keyboard.Clone(); shifted[0x10]=shifted[0xA0]=128; foreach(var code in new[] {0x11,0x12,0xA2,0xA3,0xA4,0xA5}) shifted[code]=0;
            try
            {
                SetKeyboardState(shifted);
                var target=(System.Windows.UIElement)System.Windows.Input.Keyboard.FocusedElement;
                target.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,System.Windows.PresentationSource.FromVisual(target)!,0,System.Windows.Input.Key.Delete) { RoutedEvent=System.Windows.Input.Keyboard.PreviewKeyDownEvent });
            }
            finally { SetKeyboardState(keyboard); cancel.Abort(); }
            if(!asked || !File.Exists(second.Path)) throw new InvalidOperationException("Shift+Delete after closing did not ask, or cancelling removed the file.");
            File.WriteAllText(Path.Combine(directory, "playback-lifetime-result.txt"), "PASS: overlapping native starts, shared stop, single history finalization, focus/hotkey return, and stable expanded/collapsed inspector on player close.");
        }
        finally { await StopPlayer();_settings=_settings with{DetailsCollapsed=detailsCollapsedBefore}; _repository.Delete(second.Id); File.Delete(copy); }
    }
}
