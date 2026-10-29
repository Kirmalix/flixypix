using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

internal sealed partial class VideoPlayerWindow
{
 private Task EditTransformAsync()
 {
  if(media.Kind!=MediaKind.Video)throw new InvalidOperationException(T("Time-based views are meant for videos."));
  var current=manualTransform??timeline.At(LastPositionSeconds); var c=CultureInfo.InvariantCulture;
  var fields=Dialogs.Fields(this,T("View at time"),
   (T("Seconds"),LastPositionSeconds.ToString("0.###",c)),
   (T("X relative to the playback area (0 = left, negative = outside)"),current.X.ToString("0.####",c)),
   (T("Y relative to the playback area (0 = top)"),current.Y.ToString("0.####",c)),
   (T("Width relative to the playback area (1 = full width)"),current.Width.ToString("0.####",c)),
   (T("Height relative to the playback area (1 = full height)"),current.Height.ToString("0.####",c)));
  if(fields==null)return Task.CompletedTask;
  double Parse(int index)=>double.TryParse(fields[index],NumberStyles.Float,c,out var value)?value:throw new ArgumentException(T("Please enter numbers with a decimal point."));
  var step=new PlayerTransform(Parse(0),Parse(1),Parse(2),Parse(3),Parse(4));
  var updated=PlayerTransformTimeline.Validate(timeline with { Enabled=true,Steps=timeline.Steps.Where(s=>s.PositionSeconds!=step.PositionSeconds).Append(step).ToList() });
  timeline=repository.SavePlayerTransforms(media.Id,updated); transforms.IsChecked=true; manualTransform=null; ApplyTransform();
  status.Text=T("View saved · ")+TimeSpan.FromSeconds(step.PositionSeconds).ToString(@"hh\:mm\:ss"); return Task.CompletedTask;
 }
 private Task ManageTransformsAsync()
 {
  var list=new ListBox { ItemsSource=timeline.Steps.Select(step=>new TransformChoice(step,T("{0:0.###} s · X {1:0.###}, Y {2:0.###} · {3:0.###} × {4:0.###}", step.PositionSeconds, step.X, step.Y, step.Width, step.Height))).ToArray(),DisplayMemberPath="Label",MinHeight=200 };
  var panel=new DockPanel { Margin=new Thickness(20) }; var actions=new WrapPanel(); DockPanel.SetDock(actions,Dock.Bottom); panel.Children.Add(actions); panel.Children.Add(list);
  var dialog=new Window { Owner=this,ShowInTaskbar=false,Title=T("Time-based views"),Content=panel,Width=680,Height=430,WindowStartupLocation=WindowStartupLocation.CenterOwner };
  var seekButton=new Button { Content=T("Jump to the point in time") }; seekButton.Click+=async (_,_)=>{ if(list.SelectedItem is TransformChoice choice)await Command(()=>SeekToAsync(choice.Transform.PositionSeconds)); }; actions.Children.Add(seekButton);
  var editButton=new Button { Content=T("Edit …") }; editButton.Click+=async (_,_)=>
  {
   if(list.SelectedItem is not TransformChoice choice)return;
   await Command(async()=>{ await SeekToAsync(choice.Transform.PositionSeconds); manualTransform=choice.Transform; dialog.Close(); await EditTransformAsync(); });
  }; actions.Children.Add(editButton);
  var remove=new Button { Content=T("Remove") }; remove.Click+=(_,_)=>
  {
   if(list.SelectedItem is not TransformChoice choice)return;
   if(Message.Show(dialog,T("Remove this view time?"),T("Remove view"),MessageBoxButton.YesNo,MessageBoxImage.Question,MessageBoxResult.No)!=MessageBoxResult.Yes)return;
   timeline=repository.SavePlayerTransforms(media.Id,timeline with { Steps=timeline.Steps.Where(s=>s.PositionSeconds!=choice.Transform.PositionSeconds).ToList() }); manualTransform=null; ApplyTransform();
   list.ItemsSource=timeline.Steps.Select(step=>new TransformChoice(step,T("{0:0.###} s · X {1:0.###}, Y {2:0.###} · {3:0.###} × {4:0.###}", step.PositionSeconds, step.X, step.Y, step.Width, step.Height))).ToArray();
  }; actions.Children.Add(remove);
  var close=new Button { Content=T("Close"),IsCancel=true }; close.Click+=(_,_)=>dialog.Close(); actions.Children.Add(close); dialog.ShowDialog(); return Task.CompletedTask;
 }
 private sealed record TransformChoice(PlayerTransform Transform,string Label);
}
