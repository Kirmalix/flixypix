using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
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
 private readonly TextBlock abLoopLabel=RangeLabel(),sceneRangeLabel=RangeLabel();
 private readonly Button abStart=Letter("A",T("Set A")),abEnd=Letter("B",T("Set B")),abReset=Symbol("",T("Reset A–B"));
 private static TextBlock RangeLabel()
 {
  var label=new TextBlock{VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(6,0,8,0),FontSize=11,Visibility=Visibility.Collapsed};
  label.Typography.NumeralAlignment=FontNumeralAlignment.Tabular;label.SetResourceReference(TextBlock.ForegroundProperty,"Muted");return label;
 }
 /// <summary>A loop point as a plain letter in the symbol style of the bar.</summary>
 private static Button Letter(string letter,string tip)
 {
  var button=Symbol(letter,tip);button.FontFamily=new FontFamily("Segoe UI");button.FontWeight=FontWeights.SemiBold;button.FontSize=12;return button;
 }
 /// <summary>Symbols that belong together in one rounded group, optionally led by a muted symbol naming the group.</summary>
 private static Border Pill(string? glyph,string? tip,params UIElement[] parts)
 {
  var row=new StackPanel{Orientation=Orientation.Horizontal};
  if(glyph!=null)
  {
   var lead=new TextBlock{Text=glyph,FontFamily=SettingsUi.Icons,FontSize=12,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(8,0,2,0),ToolTip=tip};
   lead.SetResourceReference(TextBlock.ForegroundProperty,"Subtle");row.Children.Add(lead);
  }
  foreach(var part in parts)
  {
   if(part is Button button){button.Padding=new Thickness(7,3,7,3);button.Margin=new Thickness(0);if(button.FontFamily.Source!="Segoe UI")button.FontSize=13;}
   row.Children.Add(part);
  }
  var pill=new Border{CornerRadius=new CornerRadius(6),Padding=new Thickness(2),Margin=new Thickness(0,0,8,0),BorderThickness=new Thickness(1),Child=row,VerticalAlignment=VerticalAlignment.Center};
  pill.SetResourceReference(Border.BorderBrushProperty,"DividerSoft");return pill;
 }
 /// <summary>Loop and scene marking as two small groups of symbols under the time line; their times appear only while set.</summary>
 private FrameworkElement BuildRangeControls()
 {
  var row=new WrapPanel{Margin=new Thickness(8,0,8,2)};
  abStart.Click+=(_,_)=>AbLoopUpdate=Command(()=>ChangeAbLoopAsync(0));abEnd.Click+=(_,_)=>AbLoopUpdate=Command(()=>ChangeAbLoopAsync(1));abReset.Click+=(_,_)=>AbLoopUpdate=Command(()=>ChangeAbLoopAsync(2));
  row.Children.Add(Pill("",T("A–B loop"),abStart,abEnd,abReset,abLoopLabel));
  Button Action(string glyph,string label,string action)
  {
   var button=Symbol(glyph,label+KeyHint(action));AutomationProperties.SetName(button,label);
   button.Click+=async(_,_)=>{await Command(()=>RunActionAsync(action));UpdateRangeControls();};return button;
  }
  row.Children.Add(Pill(null,null,Action("",T("Set bookmark"),PlayerActions.CreateBookmark)));
  row.Children.Add(Pill(null,null,Action("",T("Scene start"),PlayerActions.ClipStart),Action("",T("Scene end"),PlayerActions.ClipEnd),
   Action("",T("Create scene"),PlayerActions.ClipCreate),sceneRangeLabel));
  UpdateRangeControls();return row;
 }
 private void UpdateRangeControls()
 {
  abStart.IsEnabled=player!=null&&durationSeconds>0;abEnd.IsEnabled=abStart.IsEnabled&&AbLoop.Start.HasValue;abReset.Visibility=AbLoop.Start.HasValue?Visibility.Visible:Visibility.Collapsed;
  abReset.IsEnabled=AbLoop.Start.HasValue;
  abLoopLabel.Text=AbLoop.Start is {} a?RangeTime(a)+" → "+(AbLoop.End is {} b?RangeTime(b):"…"):"";
  abLoopLabel.Visibility=AbLoop.Start.HasValue?Visibility.Visible:Visibility.Collapsed;
  sceneRangeLabel.Text=ClipStart is null&&ClipEnd is null?"":(ClipStart is {} start?RangeTime(start):"…")+" → "+(ClipEnd is {} end?RangeTime(end):"…");
  sceneRangeLabel.Visibility=sceneRangeLabel.Text.Length>0?Visibility.Visible:Visibility.Collapsed;
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
