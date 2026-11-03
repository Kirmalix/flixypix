using System.IO;
using System.Windows;
using System.Windows.Controls;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

public partial class MainWindow
{
 internal void ShowDuplicates(IReadOnlyList<MediaItem>? selection=null)
 {
  if(!LeaveRoleView())return;
  var window=new DuplicateSearchWindow(this,_repository,selection??SelectedMedia,selection!=null);
  window.OpenSimilar=scope=>ShowSimilarMedia(scope:scope,owner:window);window.ShowDialog();
  if(window.NavigationTarget is {} item)ShowInLibrary(item);
 }
}

/// <summary>One explicit entry for byte equality and perceptual similarity. SHA scans only read files.</summary>
internal sealed class DuplicateSearchWindow:Window
{
 private readonly CatalogRepository repository;
 private readonly Guid[] selection;
 private readonly ComboBox scope=new(){MinWidth=220,Margin=new Thickness(0,0,12,8)};
 private readonly Button exact=new(){Content=T("Find identical files"),Margin=new Thickness(0,0,8,8)},similar=new(){Content=T("Find similar items"),Margin=new Thickness(0,0,8,8)},cancel=new(){Content=T("Cancel"),Visibility=Visibility.Collapsed};
 private readonly ListBox groups=new(){Width=260,Margin=new Thickness(0,0,12,0)};
 private readonly StackPanel comparison=new();
 private readonly TextBlock errors=new(){TextWrapping=TextWrapping.Wrap};
 private readonly WindowFrame frame;
 private CancellationTokenSource? running;
 private bool closeAfterRun;
 internal Action<IReadOnlyList<MediaItem>?>? OpenSimilar {get;set;}
 internal MediaItem? NavigationTarget {get;private set;}
 internal DuplicateScanResult? Result {get;private set;}
 internal Task Searching {get;private set;}=Task.CompletedTask;
 internal Button ExactButton=>exact;
 internal Button SimilarButton=>similar;
 internal Button CancelButton=>cancel;
 internal string StatusText=>frame.Status.Text;
 internal bool SelectionScope {get=>scope.SelectedIndex==1;set=>scope.SelectedIndex=value?1:0;}
 internal DuplicateSearchWindow(Window owner,CatalogRepository repository,IReadOnlyList<MediaItem> selected,bool selectionScope=false)
 {
  Owner=owner;this.repository=repository;selection=selected.Select(i=>i.Id).Distinct().ToArray();
  Title=T("Find duplicates");Width=1100;Height=740;MinWidth=760;MinHeight=480;ShowInTaskbar=false;WindowStartupLocation=WindowStartupLocation.CenterOwner;
  scope.Items.Add(new ComboBoxItem{Content=T("Whole library")});scope.Items.Add(new ComboBoxItem{Content=Tn(selection.Length,"Selection (1 item)","Selection ({0:N0} items)"),IsEnabled=selection.Length>0});scope.SelectedIndex=selectionScope&&selection.Length>0?1:0;
  var top=new WrapPanel();top.Children.Add(scope);top.Children.Add(exact);top.Children.Add(similar);top.Children.Add(cancel);
  exact.Click+=(_,_)=>Searching=SearchExactAsync();similar.Click+=(_,_)=>OpenVisual();cancel.Click+=(_,_)=>running?.Cancel();
  var detail=new ScrollViewer{Content=comparison,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};
  var body=new DockPanel();DockPanel.SetDock(top,Dock.Top);body.Children.Add(top);
  var errorView=new ScrollViewer{Content=errors,MaxHeight=120,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};DockPanel.SetDock(errorView,Dock.Bottom);body.Children.Add(errorView);
  DockPanel.SetDock(groups,Dock.Left);body.Children.Add(groups);body.Children.Add(detail);
  groups.SelectionChanged+=(_,_)=>ShowGroup();
  frame=new WindowFrame(T("Find duplicates"),T("SHA-256 compares file content. Visual similarity compares appearance. Searching does not remove items or files."),body,scroll:false,wrapActions:true);
  frame.Add(T("Close"),Close,primary:true,cancel:true);Content=frame;
  scope.SelectionChanged+=(_,_)=>{if(running==null){Result=null;groups.ItemsSource=null;comparison.Children.Clear();errors.Text="";frame.Status.Text="";}};
  Closing+=(_,e)=>{if(running!=null){e.Cancel=true;closeAfterRun=true;running.Cancel();}};
 }
 private IReadOnlyList<MediaItem>? CurrentScope()=>SelectionScope?selection.Select(repository.GetMedia).OfType<MediaItem>().ToArray():null;
 private void OpenVisual()
 {
  if(running!=null)return;OpenSimilar?.Invoke(CurrentScope());
 }
 internal async Task SearchExactAsync()
 {
  if(running!=null)return;using var source=running=new CancellationTokenSource();var selected=SelectionScope;
  exact.IsEnabled=similar.IsEnabled=scope.IsEnabled=false;cancel.Visibility=Visibility.Visible;Result=null;groups.ItemsSource=null;comparison.Children.Clear();errors.Text="";
  try
  {
   frame.Status.Text=T("Search for identical files");
   var items=await Task.Run<IReadOnlyList<MediaItem>>(()=>selected?selection.Select(repository.GetMedia).OfType<MediaItem>().ToArray():repository.Search(),source.Token);
   var progress=new Progress<DuplicateScanProgress>(p=>{if(ReferenceEquals(running,source)&&!source.IsCancellationRequested)frame.Status.Text=T("SHA-256 · {0}/{1} · {2}",p.Processed,p.Total,Path.GetFileName(p.Path));});
   Result=await DuplicateFinder.FindAsync(items,progress,source.Token);source.Token.ThrowIfCancellationRequested();
   groups.ItemsSource=Result.Groups.Select((g,i)=>new ListBoxItem{Content=T("Group {0} · {1:N0} bytes",i+1,g.FileSize),Tag=g,Padding=new Thickness(8)}).ToArray();groups.SelectedIndex=groups.Items.Count>0?0:-1;
   errors.Text=Result.Errors.Count==0?"":T("Not checked:")+"\n"+string.Join("\n",Result.Errors);
   frame.Status.Text=Tn(Result.Groups.Count,"1 group with identical file content.","{0:N0} groups with identical file content.");
  }
  catch(OperationCanceledException){Result=null;frame.Status.Text=T("Cancelled");}
  catch(Exception error)when(error is IOException or UnauthorizedAccessException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException){Result=null;frame.Status.Text=error.Message;}
  finally{running=null;exact.IsEnabled=similar.IsEnabled=scope.IsEnabled=true;cancel.Visibility=Visibility.Collapsed;if(closeAfterRun)Close();}
 }
 private void ShowGroup()
 {
  comparison.Children.Clear();if(groups.SelectedItem is not ListBoxItem{Tag:DuplicateGroup group})return;
  comparison.Children.Add(new TextBlock{Text="SHA-256: "+group.Sha256,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,12)});
  foreach(var item in group.Items)
  {
   var card=new StackPanel();card.Children.Add(new TextBlock{Text=string.IsNullOrWhiteSpace(item.Title)?Path.GetFileName(item.Path):item.Title,FontWeight=FontWeights.SemiBold,TextWrapping=TextWrapping.Wrap});
   card.Children.Add(new TextBlock{Text=item.Path,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,6,0,8)});
   var show=new Button{Content=T("Show in library"),HorizontalAlignment=HorizontalAlignment.Left};show.Click+=(_,_)=>{NavigationTarget=item;Close();};card.Children.Add(show);
   comparison.Children.Add(new Border{Child=card,Background=SettingsUi.CardBackground,BorderBrush=SettingsUi.CardBorder,BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(8),Padding=new Thickness(12),Margin=new Thickness(0,0,0,12)});
  }
 }
}
