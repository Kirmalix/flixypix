using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

public partial class MainWindow
{
    private MediaGridTransition? _gridTransition;
    private readonly HashSet<Guid> _expandedVideoStacks=[];
    private bool _showVideoStacks=true;
    private bool _stacksLoaded;
    private MediaQuery WithVideoStacks(MediaQuery query,Guid? reveal=null)
    {
        if(!_stacksLoaded){var state=_repository.GetVideoStackPresentation();_expandedVideoStacks.UnionWith(state.Expanded);_showVideoStacks=state.Show;_stacksLoaded=true;}
        var isWorklist=_filter.HasThumbnail is not null || _filter.UnratedOnly || _filter.UntaggedOnly || _reviewStates.Count>0;
        var eligible=!isWorklist && _playlist==null && (query.Kind is null or MediaKind.Video) && (_view is "Video" or "all" or "folders" or "favorites" or "watchlater");
        if(eligible && _showVideoStacks && reveal is {} id && _repository.GetVideoStacksFor([id]).FirstOrDefault() is {} stack && !_expandedVideoStacks.Contains(stack.Id))
        {
            var matches=_repository.GetVideoStackMatches(query,[stack.Id]);
            if(matches.TryGetValue(stack.Id,out var match) && match.Representative!=id && _repository.MatchingMedia(query,[id]).Contains(id)){_expandedVideoStacks.Add(stack.Id);SaveStackPresentation();}
        }
        return query with {GroupVideoStacks=eligible&&_showVideoStacks,ExpandedVideoStacks=_expandedVideoStacks.ToArray()};
    }
    private void SaveStackPresentation()=>_repository.SaveVideoStackPresentation(_showVideoStacks,_expandedVideoStacks.ToArray());
    private void StackArrowLoaded(object sender,RoutedEventArgs e)
    {
        if(sender is not TextBlock arrow || arrow.DataContext is not MediaCard card)return;
        var turn=new System.Windows.Media.RotateTransform(card.StackAngle);arrow.RenderTransform=turn;
        if(Motion.Smooth && card.PreviousStackAngle!=card.StackAngle)
            turn.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty,new System.Windows.Media.Animation.DoubleAnimation(card.PreviousStackAngle,card.StackAngle,TimeSpan.FromMilliseconds(140)){EasingFunction=new System.Windows.Media.Animation.CubicEase{EasingMode=System.Windows.Media.Animation.EasingMode.EaseOut},FillBehavior=System.Windows.Media.Animation.FillBehavior.Stop});
    }
    private void ToggleVideoStack(object sender,RoutedEventArgs e)
    {
        if(CardOf(sender)?.Stack is not {} stack)return;
        e.Handled=true;
        _lastRefresh=SetVideoStackExpandedAsync(stack,CardOf(sender)!.Item.Id);
    }
    private async Task SetVideoStackExpandedAsync(VideoStack stack,Guid representative)
    {
        CancelBand();var expanded=_expandedVideoStacks.Add(stack.Id);
        if(!expanded)_expandedVideoStacks.Remove(stack.Id);
        var retained=SelectedMedia.Where(m=>expanded || !stack.MediaIds.Contains(m.Id)).ToList();
        if(!expanded && _repository.GetMedia(representative) is {} visible)retained.Add(visible);
        var retainedIds=retained.Select(m=>m.Id).ToHashSet();
        MediaList.SelectedItems.Clear();var loaded=MediaList.Items.Cast<MediaCard>().Select(c=>c.Item.Id).ToHashSet();
        foreach(var card in MediaList.Items.Cast<MediaCard>().Where(c=>retainedIds.Contains(c.Item.Id)))MediaList.SelectedItems.Add(card);
        _offWindowSelection=retained.Where(m=>!loaded.Contains(m.Id)).ToArray();
        SaveStackPresentation();await RefreshAsync();
        var focused=MediaList.Items.OfType<MediaCard>().FirstOrDefault(c=>c.Item.Id==representative);
        if(focused!=null){MediaList.UpdateLayout();if(MediaList.ItemContainerGenerator.ContainerFromItem(focused) is ListBoxItem tile)tile.Focus();}
    }
    private VideoStack? EditStack(IReadOnlyList<MediaItem> items)
    {
        var focused=(Keyboard.FocusedElement as FrameworkElement)?.DataContext as MediaCard ?? MediaList.SelectedItem as MediaCard;
        var master=items.FirstOrDefault(i=>i.Id==focused?.Item.Id)??items[0];
        var name=new TextBox{Name="StackNameInput",Margin=new Thickness(0,4,0,12)};
        var choice=new ComboBox{Name="StackMasterChoice",ItemsSource=items,DisplayMemberPath=nameof(MediaItem.Title),SelectedItem=master,Margin=new Thickness(0,4,0,16)};
        var dialog=new Window{Owner=this,Title=T("Video stack"),Width=420,SizeToContent=SizeToContent.Height,WindowStartupLocation=WindowStartupLocation.CenterOwner,ResizeMode=ResizeMode.NoResize};
        var ok=new Button{Content=T("Create"),IsDefault=true,MinWidth=90};
        ok.Click+=(_,_)=>dialog.DialogResult=true;
        var cancel=new Button{Content=T("Cancel"),IsCancel=true,Margin=new Thickness(8,0,0,0),MinWidth=90};
        dialog.Content=new StackPanel{Margin=new Thickness(24),Children={new TextBlock{Text=T("Name")},name,new TextBlock{Text=T("Master video")},choice,new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Children={ok,cancel}}}};
        return dialog.ShowDialog()==true ? new VideoStack(Guid.NewGuid(),name.Text,((MediaItem)choice.SelectedItem).Id,items.Select(i=>i.Id).ToArray()) : null;
    }
    private void SaveStackWithMoves(VideoStack target)
    {
        var sources=_repository.GetVideoStacks().Where(s=>s.Id!=target.Id && s.MediaIds.Intersect(target.MediaIds).Any()).ToArray();
        if(sources.Length>0 && Message.Show(this,T("Move the selected videos from their existing stacks?"),T("Video stacks"),MessageBoxButton.YesNo,MessageBoxImage.Question,MessageBoxResult.No)!=MessageBoxResult.Yes)return;
        _repository.MoveVideoStackMembers(target);Refresh(target.MasterId);
    }
    private void DecorateVideoStacks(IEnumerable<MediaCard> cards,MediaQuery query)
    {
        if(!query.GroupVideoStacks)return;
        var list=cards.ToArray();
        var stacks=_repository.GetVideoStacksFor(list.Select(c=>c.Item.Id).ToArray());
        var byMedia=stacks.SelectMany(s=>s.MediaIds.Select(id=>(id,s))).ToDictionary(p=>p.id,p=>p.s);
        if(byMedia.Count==0)return;
        var matches=_repository.GetVideoStackMatches(query,stacks.Select(s=>s.Id).ToArray());
        foreach(var card in list)if(byMedia.TryGetValue(card.Item.Id,out var stack) && matches.TryGetValue(stack.Id,out var match))card.SetStack(stack,query.ExpandedVideoStacks.Contains(stack.Id),match.Representative,match.Count);
    }
    private IEnumerable<MenuSpec> VideoStackEntries(IReadOnlyList<MediaItem> items)
    {
        var stacks=_repository.GetVideoStacks();var current=stacks.FirstOrDefault(s=>s.MediaIds.Contains(items[0].Id));
        if(items.Count>=2)yield return MenuSpec.Command(T("Group into video stack …"),()=>{
            if(EditStack(items) is {} stack)SaveStackWithMoves(stack);
        });
        if(current!=null)
        {
            yield return MenuSpec.Command(T("Expand or collapse stack"),()=>{_ = SetVideoStackExpandedAsync(current,items[0].Id);});
            yield return MenuSpec.Command(T("Select stack videos"),async ()=>{
                _expandedVideoStacks.Add(current.Id);SaveStackPresentation();await RefreshAsync();
                var query=_window!.Value.Query with{GroupVideoStacks=false};
                var matching=await Task.Run(()=>_repository.MatchingMedia(query,current.MediaIds));
                var selected=current.MediaIds.Where(matching.Contains).Select(id=>_repository.GetMedia(id)).OfType<MediaItem>().ToArray();
                MediaList.SelectedItems.Clear();foreach(var card in MediaList.Items.Cast<MediaCard>().Where(c=>matching.Contains(c.Item.Id)))MediaList.SelectedItems.Add(card);
                var loaded=MediaList.Items.Cast<MediaCard>().Select(c=>c.Item.Id).ToHashSet();_offWindowSelection=selected.Where(m=>!loaded.Contains(m.Id)).ToArray();UpdateSelectionBar();
            });
            yield return MenuSpec.Command(T("Set as stack master"),()=>{_repository.SaveVideoStack(current with {MasterId=items[0].Id});Refresh(items[0].Id);},enabled:items.Count==1);
            yield return MenuSpec.Command(T("Rename stack …"),()=>{var fields=Dialogs.Fields(this,T("Video stack"),(T("Name"),current.Name??""));if(fields!=null){_repository.SaveVideoStack(current with{Name=fields[0]});Refresh();}});
            yield return MenuSpec.Command(T("Remove from stack"),()=>{_repository.RemoveVideoStackMembers(current.Id,current.Revision,items.Select(i=>i.Id).ToArray());Refresh();});
            yield return MenuSpec.Command(T("Dissolve stack"),()=>{_repository.DeleteVideoStack(current.Id,current.Revision);Refresh(items[0].Id);});
        }
        yield return MenuSpec.Submenu(T("Add to video stack"),stacks.Select(stack=>MenuSpec.Command(stack.Name??_repository.GetMedia(stack.MasterId)?.Title??T("Video stack"),()=>{SaveStackWithMoves(stack with{MediaIds=stack.MediaIds.Concat(items.Select(i=>i.Id)).Distinct().ToArray()});})));
        foreach(var entry in VideoStackViewEntries())yield return entry;
    }
    private void VideoStackViewMenu(object sender,RoutedEventArgs e){ViewPopup.IsOpen=false;MenuBelow(ViewButton,VideoStackViewEntries());}
    private IEnumerable<MenuSpec> VideoStackViewEntries()
    {
        var stacks=_repository.GetVideoStacks();
        yield return MenuSpec.Command(T("Expand all stacks"),()=>{_expandedVideoStacks.UnionWith(stacks.Select(s=>s.Id));SaveStackPresentation();Refresh();});
        yield return MenuSpec.Command(T("Collapse all stacks"),()=>{_expandedVideoStacks.Clear();_offWindowSelection=[];MediaList.SelectedItems.Clear();SaveStackPresentation();Refresh();});
        yield return new MenuCommand(T("Show video stacks"),()=>{_showVideoStacks=!_showVideoStacks;SaveStackPresentation();Refresh();}){Checked=_showVideoStacks};
    }
}
