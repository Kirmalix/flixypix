using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

public partial class MainWindow
{
    internal async Task RunVideoStackSmokeAsync(string directory)
    {
        static void Check(bool value,string message){if(!value)throw new InvalidOperationException("Video stacks: "+message);}
        var originalTransition=_gridTransition;
        var previous=CaptureViewState();var preferences=_repository.GetPreferences();
        var ids=new List<Guid>();VideoStack? stack=null;
        try
        {
            foreach(var title in new[]{"Alpha","Beta","Zeta"})
                ids.Add(_repository.Upsert(new MediaItem{Path=Path.Combine(directory,"stack-"+title+".mp4"),Title=title,Kind=MediaKind.Video,Tags=["stack-smoke"]}).Id);
            _ = Dispatcher.BeginInvoke(()=>{
                var dialog=OwnedWindows.Cast<Window>().Single(w=>w.Title==T("Video stack"));
                Visuals<TextBox>(dialog).Single(t=>t.Name=="StackNameInput").Text="Smoke stack";
                Visuals<ComboBox>(dialog).Single(t=>t.Name=="StackMasterChoice").SelectedIndex=1;
                var create=Visuals<Button>(dialog).Single(b=>Equals(b.Content,T("Create")));create.RaiseEvent(new RoutedEventArgs(Button.ClickEvent,create));
            },DispatcherPriority.ApplicationIdle);
            var draft=EditStack(ids.Take(2).Select(id=>_repository.GetMedia(id)!).ToArray());
            Check(draft?.MasterId==ids[1] && draft.Name=="Smoke stack",$"creation dialog lost master or name: master={draft?.MasterId}, expected={ids[1]}, name={draft?.Name}");
            stack=_repository.SaveVideoStack(draft!);
            ApplyViewState(new ViewState{View="Video",Tag="stack-smoke"});await _lastRefresh;await RefreshAsync();
            Check(_total==2,"collapsed count");
            Check(ViewItems(_window).Count==3,"whole-view export omitted collapsed members");
            ApplyViewState(new ViewState{View="Video",Tag="stack-smoke",Filter=new MediaFilter{UnratedOnly=true}});await _lastRefresh;await RefreshAsync();Check(_total==3,"work queue hid stack members");
            ApplyViewState(new ViewState{View="Video",Tag="stack-smoke"});await _lastRefresh;await RefreshAsync();
            await RefreshAsync(ids[0]);Check(_total==3 && SelectedMedia.Any(m=>m.Id==ids[0]),"explicit reveal did not expand a hidden video");
            await SetVideoStackExpandedAsync(stack,ids[1]);
            var representative=MediaList.Items.OfType<MediaCard>().Single(c=>c.Item.Id==ids[1]);
            Check(representative.Stack?.Id==stack.Id && representative.StackMatches==2,"representative decoration");
            MediaList.SelectedItem=representative;MediaList.UpdateLayout();
            var container=(ListBoxItem)MediaList.ItemContainerGenerator.ContainerFromItem(representative);
            var button=Visuals<Button>(container).Single(b=>Equals(b.Tag,representative.StackLabel));
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent,button));await _lastRefresh;
            Check(_total==3,"button expansion");
            MediaList.UpdateLayout();await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);Capture((FrameworkElement)RootContent,Path.Combine(directory,"video-stack-expanded.png"));
            _gridTransition?.Clear();_gridTransition=new MediaGridTransition(MediaList,()=>true);
            await SetVideoStackExpandedAsync(stack,ids[1]);await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
            Check(_gridTransition.HasMotion,"layout transition did not start");
            Check(_gridTransition.MovingDistance>0,"layout transition has no intermediate movement");
            await Task.Delay(280);Check(!_gridTransition.HasMotion,"layout transition did not clean up");
            _gridTransition.Capture();_gridTransition.Apply();_gridTransition.Capture();_gridTransition.Apply();
            await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
            Check(_gridTransition.HasMotion,"an outdated callback cancelled the newest transition");
            await Task.Delay(280);Check(!_gridTransition.HasMotion,"replaced transition retained visuals");
            await SetVideoStackExpandedAsync(stack,ids[1]);
            var member=MediaList.Items.OfType<MediaCard>().Single(c=>c.Item.Id==ids[0]);MediaList.SelectedItem=member;
            await SetVideoStackExpandedAsync(stack,ids[1]);
            Check(_total==2 && SelectedMedia.All(m=>m.Id!=ids[0]),"collapse retained hidden selection");
            _repository.Upsert(_repository.GetMedia(ids[0])! with{Tags=["stack-smoke","stack-hit"]});
            ApplyViewState(new ViewState{View="Video",Tag="stack-hit"});await _lastRefresh;await RefreshAsync();
            var hit=MediaList.Items.OfType<MediaCard>().Single();
            Check(hit.Item.Id==ids[0] && hit.StackRepresentative==ids[0] && hit.StackMatches==1,"filtered representative");
            Check(_repository.GetVideoStacks().Single(s=>s.Id==stack.Id).MasterId==ids[1],"filter changed master");
            MediaList.UpdateLayout();await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);Capture((FrameworkElement)RootContent,Path.Combine(directory,"video-stack-filtered.png"));
            ApplyViewState(new ViewState{View="Video",Tag="stack-smoke"});await _lastRefresh;await RefreshAsync();
            // A confirmed catalog removal uses the same transition as a disk deletion, after its commit.
            _repository.SavePreferences(preferences with{General=preferences.General with{ConfirmRemove=false}});
            RemoveMedia([_repository.GetMedia(ids[1])!],false);await _lastRefresh;
            Check(_total==2 && _repository.GetVideoStacks().All(s=>s.Id!=stack.Id),"master removal did not dissolve pair");
            await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
            File.WriteAllText(Path.Combine(directory,"video-stacks-result.txt"),"PASS: button expansion, collapse selection, filtered representative, master preservation, catalog removal and dissolution.");
        }
        finally
        {
            _gridTransition?.Clear();_gridTransition=originalTransition;
            if(stack!=null)_expandedVideoStacks.Remove(stack.Id);
            foreach(var id in ids)_repository.Delete(id);
            _repository.SavePreferences(preferences);SaveStackPresentation();ApplyViewState(previous);await _lastRefresh;
        }
    }
}
