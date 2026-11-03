using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

internal sealed class TagParentsWindow:Window
{
 internal sealed class Choice(TagKey key,string label,bool blocked,bool selected):INotifyPropertyChanged
 {
  public TagKey Key=>key;
  public string Label=>label;
  public bool Enabled=>!blocked||Selected;
  public bool Selected {get=>selected;set{if(value==selected||value&&!Enabled)return;selected=value;PropertyChanged?.Invoke(this,new(nameof(Selected)));PropertyChanged?.Invoke(this,new(nameof(Enabled)));}}
  public event PropertyChangedEventHandler? PropertyChanged;
 }
 internal sealed class KeyComparer:IEqualityComparer<TagKey>
 {
  internal static readonly KeyComparer Instance=new();
  public bool Equals(TagKey? a,TagKey? b)=>ReferenceEquals(a,b)||a!=null&&b!=null&&StringComparer.OrdinalIgnoreCase.Equals(a.Category,b.Category)&&StringComparer.OrdinalIgnoreCase.Equals(a.Value,b.Value);
  public int GetHashCode(TagKey key)=>HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(key.Category),StringComparer.OrdinalIgnoreCase.GetHashCode(key.Value));
 }
 internal static string Label(TagKey key)=>key.Value+" \u00b7 "+(key.Category.Length==0?T("No category"):key.Category);
 internal IReadOnlyList<Choice> Choices=>choices;
 internal TextBox Search {get;}=new(){Margin=new Thickness(0,0,0,10)};
 internal Button ApplyButton {get;}
 internal Task ApplyTask {get;private set;}=Task.CompletedTask;
 internal string FailureText=>frame.Status.Text;
 private readonly List<Choice> choices=[];
 private readonly WindowFrame frame;
 private readonly DockPanel body=new();
 private readonly Func<IReadOnlyList<TagKey>,Task> apply;
 private bool applying;
 internal TagParentsWindow(Window owner,TagKey child,IReadOnlyList<DirectoryEntry> entries,IReadOnlyList<TagKey> parents,IReadOnlyList<TagKey> descendants,Func<IReadOnlyList<TagKey>,Task> apply)
 {
  this.apply=apply;Owner=owner;Title=T("Edit parents");Width=780;Height=620;MinWidth=520;MinHeight=400;ShowInTaskbar=false;WindowStartupLocation=WindowStartupLocation.CenterOwner;
  var current=new HashSet<TagKey>(parents,KeyComparer.Instance);var blocked=new HashSet<TagKey>(descendants,KeyComparer.Instance){child};
  foreach(var key in entries.Select(e=>new TagKey(e.Category,e.Tag)).Concat(parents).Distinct(KeyComparer.Instance).OrderBy(Label,StringComparer.CurrentCultureIgnoreCase))choices.Add(new(key,Label(key),blocked.Contains(key),current.Contains(key)));
  var rows=new ListBox();ScrollViewer.SetHorizontalScrollBarVisibility(rows,ScrollBarVisibility.Disabled);
  var box=new FrameworkElementFactory(typeof(CheckBox));box.SetBinding(ToolTipProperty,new Binding(nameof(Choice.Label)));box.SetBinding(ToggleButton.IsCheckedProperty,new Binding(nameof(Choice.Selected)){Mode=BindingMode.TwoWay,UpdateSourceTrigger=UpdateSourceTrigger.PropertyChanged});box.SetBinding(IsEnabledProperty,new Binding(nameof(Choice.Enabled)));box.SetValue(MarginProperty,new Thickness(0,4,0,4));
  var text=new FrameworkElementFactory(typeof(TextBlock));text.SetBinding(TextBlock.TextProperty,new Binding(nameof(Choice.Label)));text.SetValue(TextBlock.TextWrappingProperty,TextWrapping.Wrap);box.AppendChild(text);rows.ItemTemplate=new DataTemplate{VisualTree=box};
  rows.ItemsPanel=new ItemsPanelTemplate(new FrameworkElementFactory(typeof(VirtualizingStackPanel)));VirtualizingStackPanel.SetIsVirtualizing(rows,true);
  void Filter()=>rows.ItemsSource=choices.Where(c=>Search.Text.Length==0||c.Label.Contains(Search.Text,StringComparison.CurrentCultureIgnoreCase)).ToArray();
  Search.ToolTip=T("Search");System.Windows.Automation.AutomationProperties.SetName(Search,T("Search"));Search.TextChanged+=(_,_)=>Filter();Filter();
  DockPanel.SetDock(Search,Dock.Top);body.Children.Add(Search);body.Children.Add(rows);
  frame=new WindowFrame(T("Edit parents"),Label(child)+"\n"+T("Choose any number of parents. The tag itself and its descendants cannot be parents."),body,scroll:false,wrapActions:true);
  frame.Add(T("Cancel"),()=>DialogResult=false,cancel:true);ApplyButton=frame.Add(T("Apply"),()=>ApplyTask=ApplyAsync(),primary:true);Closing+=(_,e)=>{if(applying)e.Cancel=true;};Content=frame;
 }
 private async Task ApplyAsync()
 {
  if(applying)return;var selected=choices.Where(c=>c.Selected).Select(c=>c.Key).ToArray();applying=true;frame.Buttons.IsEnabled=body.IsEnabled=false;
  try{await apply(selected);applying=false;DialogResult=true;}
  catch(Exception error)when(error is ArgumentException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException or OperationCanceledException){frame.Status.Text=error.Message;}
  finally{applying=false;frame.Buttons.IsEnabled=body.IsEnabled=true;}
 }
}
