using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

internal sealed class PublicationGroupMembersWindow:Window
{
 internal const int PageSize=120;
 private readonly CatalogRepository repository;
 private readonly HashSet<PublicationGroupMember> existing;
 private readonly HashSet<PublicationGroupMember> selected=[];
 private readonly List<PublicationGroupMember> selectionOrder=[];
 private readonly Func<IReadOnlyList<PublicationGroupMember>,Task> apply;
 internal TextBox Search {get;}=new(){MinWidth=180};
 internal ComboBox Kind {get;}=new(){MinWidth=140};
 internal ListBox Rows {get;}=new();
 internal Button Previous {get;}
 internal Button Next {get;}
 internal Button ApplyButton {get;}
 internal Task LoadTask {get;private set;}=Task.CompletedTask;
 internal Task ApplyTask {get;private set;}=Task.CompletedTask;
 internal IReadOnlyCollection<PublicationGroupMember> Selected=>selectionOrder;
 internal string FailureText=>frame.Status.Text;
 private readonly WindowFrame frame;
 private readonly DispatcherTimer searchTimer=new(){Interval=TimeSpan.FromMilliseconds(250)};
 private CancellationTokenSource? loading;
 private int page;
 private long total;
 private bool applying,closed;
 internal PublicationGroupMembersWindow(Window owner,CatalogRepository repository,IEnumerable<PublicationGroupMember> existing,Func<IReadOnlyList<PublicationGroupMember>,Task> apply)
 {
  this.repository=repository;this.existing=existing.ToHashSet();this.apply=apply;
  Owner=owner;Title=T("Add members");Width=900;Height=680;MinWidth=600;MinHeight=420;ShowInTaskbar=false;WindowStartupLocation=WindowStartupLocation.CenterOwner;
  Kind.ItemsSource=new[]{T("Videos"),T("Image sets")};Kind.SelectedIndex=0;
  var header=new DockPanel{Margin=new Thickness(0,0,0,12)};DockPanel.SetDock(Kind,Dock.Left);Kind.Margin=new Thickness(0,0,12,0);header.Children.Add(Kind);header.Children.Add(Search);
  System.Windows.Automation.AutomationProperties.SetName(Search,T("Search"));Search.ToolTip=T("Search");
  var body=new DockPanel();DockPanel.SetDock(header,Dock.Top);body.Children.Add(header);body.Children.Add(Rows);
  frame=new WindowFrame(T("Add members"),T("Publication groups"),body,scroll:false,wrapActions:true);
  Previous=frame.Add(T("Previous page (Page↑)"),()=>ChangePage(-1));
  Next=frame.Add(T("Next page (Page↓)"),()=>ChangePage(1));
  frame.Add(T("Cancel"),()=>DialogResult=false,cancel:true);
  ApplyButton=frame.Add(T("Add"),()=>ApplyTask=ApplyAsync(),primary:true);Content=frame;
  searchTimer.Tick+=(_,_)=>{searchTimer.Stop();page=0;LoadTask=LoadAsync();};
  Search.TextChanged+=(_,_)=>{searchTimer.Stop();searchTimer.Start();};
  Search.KeyDown+=(_,e)=>{if(e.Key==Key.Enter){e.Handled=true;searchTimer.Stop();page=0;LoadTask=LoadAsync();}};
  Kind.SelectionChanged+=(_,_)=>{searchTimer.Stop();page=0;LoadTask=LoadAsync();};
  PreviewKeyDown+=(_,e)=>
  {
   if(Keyboard.Modifiers!=ModifierKeys.None)return;
   if(e.Key==Key.PageUp&&Previous.IsEnabled){e.Handled=true;ChangePage(-1);}
   else if(e.Key==Key.PageDown&&Next.IsEnabled){e.Handled=true;ChangePage(1);}
  };
  Loaded+=(_,_)=>LoadTask=LoadAsync();
  Closing+=(_,e)=>{if(applying)e.Cancel=true;};
  Closed+=(_,_)=>{closed=true;searchTimer.Stop();loading?.Cancel();};UpdateActions();
 }
 private void ChangePage(int delta)
 {
  if(applying||loading!=null||delta<0&&page==0||delta>0&&(page+1)*PageSize>=total)return;
  searchTimer.Stop();page+=delta;LoadTask=LoadAsync();
 }
 private async Task LoadAsync()
 {
  loading?.Cancel();var source=loading=new CancellationTokenSource();var token=source.Token;
  var images=Kind.SelectedIndex==1;var text=Search.Text.Trim();var offset=page*PageSize;
  Rows.Items.Clear();UpdateActions();
  try
  {
   IReadOnlyList<(PublicationGroupMember Member,string Label,string Path)> choices;
   if(images)
   {
    var sets=await Task.Run(()=>repository.GetImageSets().Where(s=>text.Length==0||s.Name.Contains(text,StringComparison.CurrentCultureIgnoreCase)||s.Description?.Contains(text,StringComparison.CurrentCultureIgnoreCase)==true).ToArray(),token);
    if(token.IsCancellationRequested||closed)return;
    total=sets.Length;choices=sets.Skip(offset).Take(PageSize).Select(s=>(new PublicationGroupMember(PublicationGroupMemberKind.ImageSet,s.Id),s.Name,s.Description??"")).ToArray();
   }
   else
   {
    var result=await Task.Run(()=>repository.QueryPage(new(){Kind=MediaKind.Video,Text=text,Offset=offset,Limit=PageSize},token),token);
    if(token.IsCancellationRequested||closed)return;
    total=result.TotalCount;choices=result.Items.Select(m=>(new PublicationGroupMember(PublicationGroupMemberKind.Video,m.Id),m.Title,m.Path)).ToArray();
   }
   foreach(var choice in choices)
   {
    var member=choice.Member;var included=existing.Contains(member);
    var box=new CheckBox{Content=new TextBlock{Text=choice.Label,TextWrapping=TextWrapping.Wrap},Tag=member,ToolTip=choice.Path,IsChecked=included||selected.Contains(member),IsEnabled=!included,Margin=new Thickness(0,4,0,4)};
    box.Checked+=(_,_)=>{if(selected.Add(member))selectionOrder.Add(member);UpdateActions();};box.Unchecked+=(_,_)=>{selected.Remove(member);selectionOrder.Remove(member);UpdateActions();};Rows.Items.Add(box);
   }
   frame.Status.Text=T("{0:N0} of {1:N0} entries",choices.Count,total);
  }
  catch(OperationCanceledException){}
  catch(Exception error)when(error is InvalidOperationException or ArgumentException or Microsoft.Data.Sqlite.SqliteException)
  {if(!token.IsCancellationRequested&&!closed)frame.Status.Text=error.Message;}
  finally{if(ReferenceEquals(loading,source)){loading=null;UpdateActions();}source.Dispose();}
 }
 private void UpdateActions()
 {
  Previous.IsEnabled=!applying&&loading==null&&page>0;Next.IsEnabled=!applying&&loading==null&&(page+1)*PageSize<total;
  ApplyButton.Content=T("Add")+" ("+selected.Count+")";ApplyButton.IsEnabled=!applying&&selected.Count>0;
 }
 private async Task ApplyAsync()
 {
  if(applying||selected.Count==0)return;
  searchTimer.Stop();loading?.Cancel();applying=true;Search.IsEnabled=Kind.IsEnabled=Rows.IsEnabled=frame.Buttons.IsEnabled=false;UpdateActions();
  try{await apply(selectionOrder.ToArray());applying=false;DialogResult=true;}
  catch(Exception error)when(error is InvalidOperationException or ArgumentException or Microsoft.Data.Sqlite.SqliteException or OperationCanceledException)
  {frame.Status.Text=error.Message;}
  finally{applying=false;Search.IsEnabled=Kind.IsEnabled=Rows.IsEnabled=frame.Buttons.IsEnabled=true;UpdateActions();}
 }
}
