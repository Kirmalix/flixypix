using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using FlixyPix.Core;

namespace FlixyPix.Desktop;
internal sealed partial class VideoPlayerWindow
{
 internal AbLoopState AbLoop {get;private set;}=new();
 internal Task AbLoopUpdate {get;private set;}=Task.CompletedTask;
 internal string AbLoopSummary=>abLoopLabel.Text;
 internal FrameworkElement ControlsSurface=>controlBar;
 private static string RangeTime(double seconds)=>TimeSpan.FromSeconds(seconds).ToString(seconds>=3600?@"h\:mm\:ss\.fff":@"m\:ss\.fff",System.Globalization.CultureInfo.CurrentCulture);
 private readonly SemaphoreSlim abLoopGate=new(1,1);
 private readonly TextBlock abLoopLabel=new(){VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(8,0,8,0)};
 private readonly TextBlock sceneRangeLabel=new(){VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(8,0,8,0)};
 private readonly Button abStart=new(){Content=T("Set A")},abEnd=new(){Content=T("Set B")},abReset=new(){Content=T("Reset A–B")};
 private FrameworkElement BuildRangeControls()
 {
  var row=new WrapPanel{Margin=new Thickness(0,3,0,3)};
  foreach(var button in new[]{abStart,abEnd,abReset})
  {button.Style=(Style)Application.Current.Resources["Quiet"];button.Margin=new Thickness(2,0,2,0);AutomationProperties.SetName(button,(string)button.Content);row.Children.Add(button);}
  abStart.Click+=(_,_)=>AbLoopUpdate=Command(()=>ChangeAbLoopAsync(0));abEnd.Click+=(_,_)=>AbLoopUpdate=Command(()=>ChangeAbLoopAsync(1));abReset.Click+=(_,_)=>AbLoopUpdate=Command(()=>ChangeAbLoopAsync(2));row.Children.Add(abLoopLabel);
  foreach(var(label,action)in new[]{(T("Set bookmark"),PlayerActions.CreateBookmark),(T("Scene start"),PlayerActions.ClipStart),(T("Scene end"),PlayerActions.ClipEnd),(T("Create scene"),PlayerActions.ClipCreate)})
  {
   var button=new Button{Content=label,Style=(Style)Application.Current.Resources["Quiet"],Margin=new Thickness(2,0,2,0)};AutomationProperties.SetName(button,label);
   button.Click+=async(_,_)=>{await Command(()=>RunActionAsync(action));UpdateRangeControls();};row.Children.Add(button);
  }
  row.Children.Add(sceneRangeLabel);UpdateRangeControls();return row;
 }
 private void UpdateRangeControls()
 {
  abStart.IsEnabled=player!=null&&durationSeconds>0;abEnd.IsEnabled=abStart.IsEnabled&&AbLoop.Start.HasValue;abReset.IsEnabled=AbLoop.Start.HasValue;
  abLoopLabel.Text=AbLoop.Start is {} a?"A: "+RangeTime(a)+" · B: "+(AbLoop.End is {} b?RangeTime(b):"–"):"A–B: –";
  sceneRangeLabel.Text=T("Scene start")+": "+(ClipStart is {} start?RangeTime(start):"–")+" · "+T("Scene end")+": "+(ClipEnd is {} end?RangeTime(end):"–");
 }
 /// <param name="action">0 sets A, 1 sets B, 2 clears both points.</param>
 private async Task ChangeAbLoopAsync(int action)
 {
  await abLoopGate.WaitAsync();
  try
  {
   if(closing)return;var duration=durationSeconds??throw new InvalidOperationException(T("The player is not ready yet."));
   var next=action switch{0=>AbLoop.WithStart(LastPositionSeconds,duration),1=>AbLoop.WithEnd(LastPositionSeconds,duration),2=>new AbLoopState(),_=>throw new ArgumentOutOfRangeException(nameof(action))};
   try{await RequirePlayer().SetAbLoopAsync(next);}
   catch{AbLoop=new();UpdateRangeControls();throw;}
   AbLoop=next;UpdateRangeControls();
  }
  finally{abLoopGate.Release();}
 }
}
