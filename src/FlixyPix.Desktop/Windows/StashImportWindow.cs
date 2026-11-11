using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using FlixyPix.Core;
using Microsoft.Win32;

namespace FlixyPix.Desktop;

/// <summary>Takes records from a Stash export folder in four steps: source, path mapping, selection with field decisions, import.
/// Nothing in the catalog changes before the last step; local values stay unless a field is chosen.</summary>
internal sealed class StashImportWindow : Window
{
 /// <summary>One source record with the choices made for it.</summary>
 internal sealed class Row(StashImportPreviewRow preview):INotifyPropertyChanged
 {
  public StashImportPreviewRow Preview {get;private set;}=preview;
  private bool include;
  public bool Include {get=>include;set{if(include==value||!Selectable&&value)return;include=value;PropertyChanged?.Invoke(this,new(nameof(Include)));}}
  public string Label=>Kind(Preview.Draft.Kind)+" · "+Preview.Draft.Name+" — "+Status;
  public string Status=>StatusOf(Preview);
  public bool Selectable=>Preview.Supported&&!Preview.Draft.HasErrors&&Preview.Identity.State!=StashIdentityState.Invalid;
  public StashIdentityCandidate? Identity {get;set;}
  public bool NewIdentity {get;set;}
  public StashTargetReference? Target {get;set;}
  public bool CreateTarget {get;set;}
  public string? TargetName {get;set;}
  public bool ConfirmVersions {get;set;}
  public HashSet<string> UseSource {get;}=new(StringComparer.Ordinal);
  public event PropertyChangedEventHandler? PropertyChanged;
  /// <summary>A record that still needs a decision before it can be imported.</summary>
  public string? Missing=>Preview.Identity.State==StashIdentityState.Ambiguous&&Identity==null&&!NewIdentity?T("Choose which earlier record this is."):
   Preview.Binding is {Detached:true}&&Target==null&&!CreateTarget?T("Choose a local target or create a new one."):
   Preview.Binding==null&&Preview.LocalMatches.Count>1&&Target==null&&!CreateTarget?T("Choose the matching local entry or create a new one."):null;
 }

 private static readonly StashEntityKind[] Order=[StashEntityKind.Performer,StashEntityKind.Studio,StashEntityKind.Scene,StashEntityKind.Image,StashEntityKind.Tag,StashEntityKind.Gallery,StashEntityKind.Group];
 private const int BatchSize=256;
 private readonly CatalogRepository repository;
 private readonly StashArchiveResolver archives;
 /// <summary>Called after an import with the videos it created or updated, so their stored thumbnails can be shown.</summary>
 private readonly Action<IReadOnlyList<Guid>> imported;
 private readonly WindowFrame frame;
 private readonly ContentControl page=new();
 private readonly Button back,next,stop;
 private int step;
 private CancellationTokenSource? work;
 internal StashExportSnapshot? Snapshot {get;private set;}
 internal StashSourceProfile? Profile {get;private set;}
 internal IReadOnlyList<Row> Rows=>rows;
 private readonly List<Row> rows=[];
 internal Task Work {get;private set;}=Task.CompletedTask;
 internal string Report {get;private set;}="";
 internal string StatusText=>frame.Status.Text;
 // Step 1
 private readonly ComboBox sources=new(){MinWidth=320,Margin=new Thickness(0,4,0,8)};
 private readonly TextBox folder=new(){MinWidth=420},name=new(){MaxLength=256,Margin=new Thickness(0,4,0,8)};
 // Step 2
 private readonly StackPanel mappings=new();
 private readonly ComboBox performerCategory=new(){IsEditable=true,MinWidth=240,Margin=new Thickness(0,4,0,8)},studioCategory=new(){IsEditable=true,MinWidth=240,Margin=new Thickness(0,4,0,8)};
 // Step 3
 private readonly ListBox list=new(){MinWidth=380};
 private readonly ComboBox kindFilter=new(){MinWidth=200,Margin=new Thickness(0,0,8,8)};
 private readonly StackPanel details=new(){Margin=new Thickness(16,0,0,0)};
 private readonly TextBlock summary=new(){TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,8)};

 internal StashImportWindow(Window owner,CatalogRepository repository,StashArchiveResolver archives,Action<IReadOnlyList<Guid>> imported)
 {
  this.repository=repository;this.archives=archives;this.imported=imported;Owner=owner;
  Title=T("Import from Stash");Width=1120;Height=780;MinWidth=760;MinHeight=520;ShowInTaskbar=false;WindowStartupLocation=WindowStartupLocation.CenterOwner;
  frame=new WindowFrame(T("Import from Stash"),T("Reads a Stash {0} metadata export folder. The source and the media files stay unchanged; nothing in the catalog changes before the last step.",StashExport.CheckedVersions),page,scroll:false);
  stop=frame.Add(T("Cancel"),()=>work?.Cancel());stop.Visibility=Visibility.Collapsed;
  back=frame.Add(T("Back"),()=>GoTo(step-1));
  next=frame.Add(T("Next"),()=>Work=NextAsync(),primary:true);
  frame.Add(T("Close"),Close,cancel:true);
  Content=frame;
  Closing+=(_,e)=>{if(work!=null){e.Cancel=true;work.Cancel();}};
  Closed+=(_,_)=>{Snapshot?.Dispose();Snapshot=null;};
  BuildSource();BuildMapping();BuildSelection();GoTo(0);
 }

 private static string Kind(StashEntityKind kind)=>kind switch
 {
  StashEntityKind.Performer=>T("Performer"),StashEntityKind.Studio=>T("Studio"),StashEntityKind.Tag=>T("Tag"),StashEntityKind.Scene=>T("Scene"),
  StashEntityKind.Image=>T("Image"),StashEntityKind.Gallery=>T("Gallery"),StashEntityKind.Group=>T("Group"),_=>T("Other record"),
 };
 internal static string StatusOf(StashImportPreviewRow row)=>!row.Supported?T("Not imported"):row.Draft.HasErrors?T("Needs corrections"):
  row.Identity.State==StashIdentityState.Ambiguous?T("Ambiguous"):row.Binding is {Detached:true}?T("Local target removed"):
  row.Binding==null&&row.LocalMatches.Count>0?T("Matches a local entry"):row.Binding==null?T("New"):
  row.Fields.Any(f=>f.Status==StashFieldStatus.Conflict)?T("Conflict"):row.Fields.Any(f=>f.Status is StashFieldStatus.New or StashFieldStatus.SourceChanged)?T("Changed"):T("Unchanged");

 private void GoTo(int index)
 {
  if(work!=null)return;step=Math.Clamp(index,0,3);
  page.Content=step switch{0=>sourcePage,1=>mappingPage,2=>selectionPage,_=>reportPage};
  back.IsEnabled=step is 1 or 2;next.Content=step switch{0=>T("Read export"),1=>T("Show preview"),2=>T("Import"),_=>T("Done")};next.IsEnabled=step<3;
  if(step==2)RefreshList();
 }

 private Busy Begin(string text)
 {
  var source=new CancellationTokenSource();work=source;next.IsEnabled=back.IsEnabled=false;stop.Visibility=Visibility.Visible;frame.Status.Text=text;
  return new(this,source);
 }
 private sealed class Busy(StashImportWindow window,CancellationTokenSource source):IDisposable
 {
  public CancellationToken Token=>source.Token;
  public void Dispose(){window.work=null;source.Dispose();window.stop.Visibility=Visibility.Collapsed;window.next.IsEnabled=window.step<3;window.back.IsEnabled=window.step is 1 or 2;}
 }

 private async Task NextAsync()
 {
  if(work!=null)return;
  try
  {
   if(step==0)await ReadAsync(folder.Text.Trim());
   else if(step==1)await PreviewAsync();
   else if(step==2)await ApplyAsync();
  }
  catch(OperationCanceledException){frame.Status.Text=T("Cancelled");}
  catch(Exception error)when(error is IOException or InvalidDataException or ArgumentException or InvalidOperationException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
  {frame.Status.Text=error.Message;}
 }

 // ---- Step 1: source -------------------------------------------------------------------------------------------------
 private FrameworkElement sourcePage=null!;
 private void BuildSource()
 {
  var saved=repository.GetStashSources();
  sources.ItemsSource=saved.Select(profile=>(object)profile.Name+" · "+profile.SourceDirectory).Prepend(T("New source")).ToArray();sources.SelectedIndex=0;
  sources.SelectionChanged+=(_,_)=>{if(sources.SelectedIndex>0){var profile=saved[sources.SelectedIndex-1];folder.Text=profile.SourceDirectory;name.Text=profile.Name;}};
  var browse=SettingsUi.SmallButton(T("Choose …"));
  browse.Click+=(_,_)=>{var picker=new OpenFolderDialog{AddToRecent=Privacy.AddToRecent,Title=T("Choose the Stash export folder")};if(picker.ShowDialog(this)==true){folder.Text=picker.FolderName;if(name.Text.Length==0)name.Text=Path.GetFileName(picker.FolderName);}};
  var row=new DockPanel{Margin=new Thickness(0,4,0,8)};DockPanel.SetDock(browse,Dock.Right);row.Children.Add(browse);row.Children.Add(folder);
  sourcePage=new StackPanel{Children={Label(T("Saved source")),sources,Label(T("Export folder")),row,SettingsUi.Hint(T("The folder Stash wrote with “Export to JSON”, containing scenes, images, performers and the other sections.")),
   Label(T("Name of the source")),name}};
 }
 private static TextBlock Label(string text)=>new(){Text=text,FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,8,0,0)};

 internal async Task ReadAsync(string path)
 {
  if(string.IsNullOrWhiteSpace(path)||!Directory.Exists(path))throw new ArgumentException(T("Choose an existing export folder."));
  using var busy=Begin(T("Reading the export …"));
  Snapshot?.Dispose();Snapshot=null;
  var progress=new Progress<StashReadProgress>(p=>frame.Status.Text=Tn(p.Files,"Reading the export … 1 record","Reading the export … {0:N0} records"));
  Snapshot=await StashExport.ReadDirectoryAsync(path,AppStorage.WorkDirectory,null,busy.Token,progress);
  var full=Path.GetFullPath(path);
  Profile=repository.GetStashSources().FirstOrDefault(p=>CatalogRepository.PathKey(p.SourceDirectory)==CatalogRepository.PathKey(full));
  if(name.Text.Trim().Length==0)name.Text=Profile?.Name??Path.GetFileName(Path.TrimEndingDirectorySeparator(full));
  var(snapshot,known)=(Snapshot,Profile?.PathMappings??[]);
  PrepareMapping(await Task.Run(()=>SuggestPrefixes(snapshot,known,busy.Token),busy.Token));
  frame.Status.Text=Tn(Snapshot.Count,"1 record read.","{0:N0} records read.")+(Snapshot.ErrorDocuments>0?" "+Tn(Snapshot.ErrorDocuments,"1 needs corrections.","{0:N0} need corrections."):"");
  busy.Dispose();GoTo(1);
 }

 // ---- Step 2: paths and categories -----------------------------------------------------------------------------------
 private FrameworkElement mappingPage=null!;
 private void BuildMapping()
 {
  var add=SettingsUi.SmallButton(T("Add mapping"));add.HorizontalAlignment=HorizontalAlignment.Left;add.Margin=new Thickness(0,4,0,8);add.Click+=(_,_)=>AddMapping("","");
  mappingPage=new ScrollViewer{VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Content=new StackPanel{Children={Label(T("Paths")),
   SettingsUi.Hint(T("Stash stores the paths of its own computer. Assign each source folder the local folder that holds the same files; unmapped files cannot be imported.")),
   mappings,add,Label(T("Category for performers")),performerCategory,Label(T("Category for studios")),studioCategory,
   SettingsUi.Hint(T("Stash tags become plain tags. Performers and studios become directory entries in these categories."))}}};
 }
 private void AddMapping(string source,string target)
 {
  var from=new TextBox{Text=source,MinWidth=300,Margin=new Thickness(0,0,8,0)};var to=new TextBox{Text=target,MinWidth=300,Margin=new Thickness(0,0,8,0)};
  var browse=SettingsUi.SmallButton(T("Choose …"));var remove=SettingsUi.SmallButton(T("Remove"));
  var row=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(0,4,0,4),Children={from,new TextBlock{Text="→",VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,8,0)},to,browse,remove},Tag=(from,to)};
  browse.Click+=(_,_)=>{var picker=new OpenFolderDialog{AddToRecent=Privacy.AddToRecent,Title=T("Local folder for {0}",from.Text)};if(picker.ShowDialog(this)==true)to.Text=picker.FolderName;};
  remove.Click+=(_,_)=>mappings.Children.Remove(row);
  mappings.Children.Add(row);
 }
 /// <summary>The top folders of the source files that no saved mapping covers.</summary>
 private static IReadOnlyList<string> SuggestPrefixes(StashExportSnapshot snapshot,IReadOnlyList<StashPathPrefix> known,CancellationToken token)
 {
  var suggested=new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
  for(var offset=0;offset<snapshot.Count&&suggested.Count<24;)
  {
   token.ThrowIfCancellationRequested();var page=snapshot.ReadPage(offset,256);if(page.Count==0)break;offset+=page.Count;
   foreach(var document in page.Where(d=>d.Kind==StashEntityKind.File&&d.Document is not null))
    if(document.Document!.Value.TryGetProperty("path",out var value)&&value.ValueKind==JsonValueKind.String&&TopFolder(value.GetString()!) is {} top&&
       !known.Any(m=>top.StartsWith(m.SourcePrefix.Replace('\\','/').TrimEnd('/'),StringComparison.OrdinalIgnoreCase)))suggested.Add(top);
  }
  return suggested.ToArray();
 }
 /// <summary>Saved mappings plus suggestions, and the categories with a performer or studio role.</summary>
 private void PrepareMapping(IReadOnlyList<string> suggested)
 {
  mappings.Children.Clear();
  foreach(var mapping in Profile?.PathMappings??[])AddMapping(mapping.SourcePrefix,mapping.TargetPrefix);
  foreach(var top in suggested)AddMapping(top,"");
  if(mappings.Children.Count==0)AddMapping("","");
  var directory=repository.GetDirectory();
  string[] Categories(TagRole role,string fallback)=>directory.Where(e=>e.Role==role&&e.Category.Length>0).Select(e=>e.Category).Distinct(StringComparer.OrdinalIgnoreCase).DefaultIfEmpty(fallback).ToArray();
  performerCategory.ItemsSource=Categories(TagRole.Performer,T("Performers"));performerCategory.SelectedIndex=0;
  studioCategory.ItemsSource=Categories(TagRole.Studio,T("Studios"));studioCategory.SelectedIndex=0;
 }
 /// <summary>The first folder below the root of a Stash path, for example /data or C:/Media.</summary>
 private static string? TopFolder(string path)
 {
  var normalized=path.Replace('\\','/');var parts=normalized.Split('/',StringSplitOptions.RemoveEmptyEntries);
  if(parts.Length<2)return null;
  return normalized.StartsWith("//",StringComparison.Ordinal)?"//"+parts[0]+"/"+parts[1]:parts[0].EndsWith(':')?parts[0]+"/"+parts[1]:"/"+parts[0];
 }
 internal void SetMappings(params (string Source,string Target)[] values)
 {mappings.Children.Clear();foreach(var(source,target)in values)AddMapping(source,target);}

 internal async Task PreviewAsync()
 {
  var values=mappings.Children.OfType<StackPanel>().Select(row=>((ValueTuple<TextBox,TextBox>)row.Tag).Item1.Text.Trim()+"\n"+((ValueTuple<TextBox,TextBox>)row.Tag).Item2.Text.Trim())
   .Select(text=>text.Split('\n')).Where(pair=>pair[0].Length>0||pair[1].Length>0).ToArray();
  if(values.Any(pair=>pair[0].Length==0||pair[1].Length==0))throw new ArgumentException(T("Each mapping needs a source folder and a local folder."));
  var mapped=values.Select(pair=>new StashPathPrefix(pair[0],pair[1])).ToList();
  var full=Path.GetFullPath(Snapshot!.SourceDirectory);var sourceName=name.Text.Trim().Length>0?name.Text.Trim():Path.GetFileName(full);
  Profile=Profile==null?repository.SaveStashSource(new(){Name=sourceName,SourceDirectory=full,PathMappings=mapped})
   :repository.SaveStashSource(Profile with {Name=sourceName,PathMappings=mapped},Profile.Revision);
  using var busy=Begin(T("Preparing the preview …"));
  var(snapshot,profile,performers,studios)=(Snapshot,Profile,Category(performerCategory),Category(studioCategory));
  var loaded=new List<StashImportPreviewRow>();
  for(var offset=0;offset<snapshot.Count;)
  {
   busy.Token.ThrowIfCancellationRequested();
   var page=await Task.Run(()=>repository.PreviewStashImport(snapshot,profile.Id,performers,studios,offset,256,busy.Token),busy.Token);
   if(page.Count==0)break;offset+=page.Count;loaded.AddRange(page);frame.Status.Text=T("Preparing the preview … {0:N0} / {1:N0}",offset,snapshot.Count);
  }
  rows.Clear();
  // File and folder records only describe the media; they appear through their scenes, images and galleries.
  foreach(var preview in loaded.Where(p=>p.Draft.Kind is not (StashEntityKind.File or StashEntityKind.Folder))
   .OrderBy(p=>Array.IndexOf(Order,p.Draft.Kind) is var i&&i<0?Order.Length:i).ThenBy(p=>p.Draft.Name,StringComparer.CurrentCultureIgnoreCase))
  {
   var row=new Row(preview);row.PropertyChanged+=(_,_)=>UpdateSummary();
   if(preview.Identity.State==StashIdentityState.Matched||preview.Identity.State==StashIdentityState.ConfirmationRequired&&preview.Identity.Candidates.Count==1)row.Identity=preview.Identity.Candidates[0];
   if(preview.Binding==null&&preview.LocalMatches.Count==1)row.Target=preview.LocalMatches[0].Target;
   // New records are proposed; existing ones are only taken when chosen, and records needing a decision wait for it.
   row.Include=row.Selectable&&row.Missing==null&&preview.Binding==null&&preview.Identity.State==StashIdentityState.New;
   rows.Add(row);
  }
  kindFilter.ItemsSource=new[]{T("All records")}.Concat(Order.Where(k=>rows.Any(r=>r.Preview.Draft.Kind==k)).Select(Kind)).Append(T("Needs attention")).ToArray();kindFilter.SelectedIndex=0;
  frame.Status.Text=Tn(rows.Count,"1 record in the preview.","{0:N0} records in the preview.");
  busy.Dispose();GoTo(2);
 }
 private static string Category(ComboBox box)=>(box.Text??"").Trim();

 // ---- Step 3: selection ------------------------------------------------------------------------------------------------
 private FrameworkElement selectionPage=null!;
 private void BuildSelection()
 {
  var template=new DataTemplate();var check=new FrameworkElementFactory(typeof(CheckBox));
  check.SetBinding(CheckBox.IsCheckedProperty,new Binding(nameof(Row.Include)){Mode=BindingMode.TwoWay});
  check.SetBinding(ContentControl.ContentProperty,new Binding(nameof(Row.Label)));check.SetBinding(UIElement.IsEnabledProperty,new Binding(nameof(Row.Selectable)));
  template.VisualTree=check;list.ItemTemplate=template;
  VirtualizingPanel.SetIsVirtualizing(list,true);VirtualizingPanel.SetVirtualizationMode(list,VirtualizationMode.Recycling);
  list.SelectionChanged+=(_,_)=>ShowDetails();kindFilter.SelectionChanged+=(_,_)=>RefreshList();
  var all=SettingsUi.SmallButton(T("Select all new"));all.Click+=(_,_)=>{foreach(var row in rows)if(row.Missing==null&&row.Preview.Binding==null)row.Include=true;UpdateSummary();};
  var none=SettingsUi.SmallButton(T("Select none"));none.Click+=(_,_)=>{foreach(var row in rows)row.Include=false;UpdateSummary();};
  var dependencies=SettingsUi.SmallButton(T("Add referenced records"),T("Adds performers, studios and media that the selected records refer to and that were not imported before."));
  dependencies.Click+=(_,_)=>{IncludeDependencies();UpdateSummary();};
  var tools=new WrapPanel{Children={kindFilter,all,none,dependencies}};
  var left=new DockPanel();DockPanel.SetDock(tools,Dock.Top);DockPanel.SetDock(summary,Dock.Bottom);left.Children.Add(tools);left.Children.Add(summary);left.Children.Add(list);
  var grid=new Grid();grid.ColumnDefinitions.Add(new(){Width=new GridLength(5,GridUnitType.Star)});grid.ColumnDefinitions.Add(new(){Width=new GridLength(6,GridUnitType.Star)});
  var right=new ScrollViewer{VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Content=details};Grid.SetColumn(right,1);
  grid.Children.Add(left);grid.Children.Add(right);selectionPage=grid;
 }
 private void RefreshList()
 {
  var index=kindFilter.SelectedIndex;var kinds=Order.Where(k=>rows.Any(r=>r.Preview.Draft.Kind==k)).ToArray();
  list.ItemsSource=index<=0?rows:index<=kinds.Length?rows.Where(r=>r.Preview.Draft.Kind==kinds[index-1]).ToArray():rows.Where(r=>r.Missing!=null||!r.Selectable).ToArray();
  if(list.SelectedIndex<0&&list.Items.Count>0)list.SelectedIndex=0;UpdateSummary();
 }
 private void UpdateSummary()
 {
  var chosen=rows.Count(r=>r.Include);
  summary.Text=T("{0:N0} of {1:N0} records selected.",chosen,rows.Count)+(chosen>BatchSize?" "+T("They are imported in batches of {0}; each batch is complete or not imported at all.",BatchSize):"");
  next.IsEnabled=step==2&&chosen>0&&work==null;
 }
 private void IncludeDependencies()
 {
  var byOrdinal=rows.ToDictionary(r=>r.Preview.Draft.SourceOrdinal);var queue=new Queue<Row>(rows.Where(r=>r.Include));
  while(queue.TryDequeue(out var row))
  {
   var referenced=row.Preview.Draft.References.Where(r=>r.Reference.Relation is "performer" or "studio").Select(r=>r.TargetOrdinal).OfType<long>()
    .Concat(row.Preview.Draft.Members.Select(m=>m.SourceOrdinal));
   foreach(var ordinal in referenced)
    if(byOrdinal.TryGetValue(ordinal,out var other)&&!other.Include&&other.Preview.Binding is not {Detached:false}&&other.Missing==null&&other.Selectable){other.Include=true;queue.Enqueue(other);}
  }
 }

 private void ShowDetails()
 {
  details.Children.Clear();if(list.SelectedItem is not Row row)return;var preview=row.Preview;var draft=preview.Draft;
  details.Children.Add(new TextBlock{Text=Kind(draft.Kind)+" · "+draft.Name,FontSize=16,FontWeight=FontWeights.SemiBold,TextWrapping=TextWrapping.Wrap});
  details.Children.Add(SettingsUi.Hint(draft.RelativePath+" · "+row.Status));
  foreach(var issue in draft.Issues)details.Children.Add(new TextBlock{Text=(issue.Severity==StashIssueSeverity.Error?"⚠ ":"ℹ ")+issue.Message,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,4,0,0)});
  if(!row.Selectable){details.Children.Add(SettingsUi.Hint(draft.HasErrors?T("Correct the record in Stash and export again."):T("This kind of record is kept in the export and not imported.")));return;}
  if(preview.Identity.Candidates.Count>1||preview.Identity.State==StashIdentityState.ConfirmationRequired)
  {
   var identity=new ComboBox{Margin=new Thickness(0,4,0,8),ItemsSource=preview.Identity.Candidates.Select(c=>(object)T("Earlier record {0}",c.Id.ToString("N")[..8])).Append(T("A different record (register as new)")).ToArray()};
   identity.SelectedIndex=row.Identity is {} chosen?preview.Identity.Candidates.ToList().FindIndex(c=>c.Id==chosen.Id):row.NewIdentity?preview.Identity.Candidates.Count:-1;
   identity.SelectionChanged+=(_,_)=>{var i=identity.SelectedIndex;row.NewIdentity=i==preview.Identity.Candidates.Count;row.Identity=i>=0&&!row.NewIdentity?preview.Identity.Candidates[i]:null;Refresh(row);};
   details.Children.Add(Label(T("Earlier import")));details.Children.Add(identity);
  }
  if(draft.Kind!=StashEntityKind.Tag&&(preview.LocalMatches.Count>0||preview.Binding is {Detached:true}))
  {
   var options=preview.LocalMatches.Select(m=>(object)m.Label).Append(T("Create a new entry")).ToArray();
   var target=new ComboBox{Margin=new Thickness(0,4,0,8),ItemsSource=options};
   target.SelectedIndex=row.CreateTarget?preview.LocalMatches.Count:row.Target is {} t?preview.LocalMatches.ToList().FindIndex(m=>m.Target==t):-1;
   target.SelectionChanged+=(_,_)=>{var i=target.SelectedIndex;row.CreateTarget=i==preview.LocalMatches.Count;row.Target=i>=0&&!row.CreateTarget?preview.LocalMatches[i].Target:null;Refresh(row);ShowDetails();};
   details.Children.Add(Label(T("Local target")));details.Children.Add(target);
  }
  if(draft.Kind is StashEntityKind.Performer or StashEntityKind.Studio&&row.Target==null&&preview.Binding is not {Detached:false})
  {
   var entry=new TextBox{Text=row.TargetName??draft.Name,MaxLength=256,Margin=new Thickness(0,4,0,8)};
   entry.TextChanged+=(_,_)=>row.TargetName=entry.Text.Trim()==draft.Name.Trim()||entry.Text.Trim().Length==0?null:entry.Text.Trim();
   details.Children.Add(Label(T("Name in the directory")));details.Children.Add(entry);
  }
  if(draft.Media is {} media&&media.Files.Count(f=>f.TargetPath!=null&&f.ArchiveSource==null)>1)
  {
   var versions=new CheckBox{Content=T("Add the further files as versions"),IsChecked=row.ConfirmVersions,Margin=new Thickness(0,8,0,8)};
   versions.Click+=(_,_)=>row.ConfirmVersions=versions.IsChecked==true;details.Children.Add(versions);
   foreach(var file in media.Files.Skip(1))details.Children.Add(SettingsUi.Hint(file.TargetPath??file.OriginalPath));
  }
  if(row.Missing is {} missing)details.Children.Add(new TextBlock{Text=missing,Foreground=SettingsUi.Accent,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,8,0,0)});
  // Field decisions only matter for an existing target; a new one takes every source value.
  var existing=preview.Binding is {Detached:false}||row.Target!=null||draft.Kind==StashEntityKind.Tag&&preview.Binding!=null;
  var changed=preview.Fields.Where(f=>f.Status!=StashFieldStatus.Equal).ToArray();
  if(existing&&changed.Length>0)
  {
   details.Children.Add(Label(T("Fields")));details.Children.Add(SettingsUi.Hint(T("Unchecked fields keep the local value. Checked fields take the value from Stash.")));
   var table=new Grid{Margin=new Thickness(0,6,0,0)};foreach(var width in new[]{1.2,2,2,1})table.ColumnDefinitions.Add(new(){Width=new GridLength(width,GridUnitType.Star)});
   void Cell(int r,int c,UIElement element){Grid.SetRow(element,r);Grid.SetColumn(element,c);table.Children.Add(element);}
   table.RowDefinitions.Add(new());
   foreach(var(text,column)in new[]{(T("Field"),0),(T("Local"),1),(T("Stash"),2)})Cell(0,column,new TextBlock{Text=text,FontWeight=FontWeights.SemiBold});
   foreach(var field in changed)
   {
    table.RowDefinitions.Add(new());var r=table.RowDefinitions.Count-1;
    Cell(r,0,new TextBlock{Text=FieldName(field.Field)+"\n"+FieldStatus(field.Status),TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,6,8,6)});
    var pictures=field.Field is "cover" or "picture";
    Cell(r,1,pictures?Picture(()=>LocalPicture(row),field.Local):new TextBlock{Text=Display(field.Local),TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,6,8,6)});
    Cell(r,2,pictures?Picture(()=>Snapshot?.ReadPicture(draft.SourceOrdinal),field.Source):new TextBlock{Text=Display(field.Source),TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,6,8,6)});
    var take=new CheckBox{Content=T("Take"),IsChecked=row.UseSource.Contains(field.Field),VerticalAlignment=VerticalAlignment.Top,Margin=new Thickness(0,6,0,6)};
    take.Click+=(_,_)=>{if(take.IsChecked==true){row.UseSource.Add(field.Field);row.Include=true;}else row.UseSource.Remove(field.Field);Refresh(row);};
    Cell(r,3,take);
   }
   details.Children.Add(table);
  }
 }
 /// <summary>Shows the details of one record, as a click in the list does.</summary>
 internal void Select(Row row){list.SelectedItem=row;list.ScrollIntoView(row);}
 private void Refresh(Row row){if(row.Missing!=null)row.Include=false;var index=list.SelectedIndex;list.Items.Refresh();list.SelectedIndex=index;UpdateSummary();}
 private static string FieldName(string field)=>field switch
 {
  "title"=>T("Title"),"description"=>T("Description"),"rating"=>T("Rating"),"releaseDate"=>T("Release date"),"tags"=>T("Tags"),"markers"=>T("Scenes and bookmarks"),
  "aliases"=>T("Aliases"),"favorite"=>T("Favorite"),"profile"=>T("Profile"),"name"=>T("Name"),"mediaIds"=>T("Images"),"members"=>T("Members"),"parents"=>T("Parents"),
  "playCount"=>T("Play count"),"oCount"=>T("O counter"),"cover"=>T("Thumbnail"),"picture"=>T("Image"),_=>field,
 };
 /// <summary>The image the local target shows now: the chosen thumbnail of a medium or the image of a directory entry.</summary>
 private byte[]? LocalPicture(Row row)
 {
  var target=row.Target??row.Preview.Binding?.Target;
  if(target?.Tag is {} key)return repository.GetTagPictureIds().TryGetValue(key,out var id)?repository.GetTagAsset(id)?.Data:null;
  return target?.Id is {} media&&repository.GetMedia(media)?.Extra.GetValueOrDefault("thumbnail.assetId") is {} chosen&&Guid.TryParse(chosen,out var asset)?repository.GetMediaAsset(asset)?.Data:null;
 }
 /// <summary>Shows one side of an image field: the image itself with its size, or the size alone when it cannot be shown here.</summary>
 private static FrameworkElement Picture(Func<byte[]?> read,JsonElement value)
 {
  var text=new TextBlock{Text=Display(value),TextWrapping=TextWrapping.Wrap};var panel=new StackPanel{Margin=new Thickness(0,6,8,6),Children={text}};
  if(value.ValueKind!=JsonValueKind.Object)return panel;
  try
  {
   if(read() is not {Length:>0} data)return panel;
   var bitmap=new System.Windows.Media.Imaging.BitmapImage();
   using(var stream=new MemoryStream(data)){bitmap.BeginInit();bitmap.CacheOption=System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;bitmap.DecodePixelWidth=320;bitmap.StreamSource=stream;bitmap.EndInit();}
   bitmap.Freeze();
   var image=new Image{Source=bitmap,MaxWidth=160,MaxHeight=120,Stretch=System.Windows.Media.Stretch.Uniform,HorizontalAlignment=HorizontalAlignment.Left,Margin=new Thickness(0,0,0,4)};
   Privacy.SetBlur(image,true);panel.Children.Insert(0,image);
  }
  catch(Exception error)when(error is IOException or InvalidDataException or NotSupportedException or InvalidOperationException or ArgumentException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException){}
  return panel;
 }
 private static string FieldStatus(StashFieldStatus status)=>status switch
 {
  StashFieldStatus.New=>T("not decided before"),StashFieldStatus.SourceChanged=>T("changed in Stash"),StashFieldStatus.SourceUnchanged=>T("unchanged in Stash"),
  StashFieldStatus.PreviouslyKept=>T("kept locally before"),StashFieldStatus.Conflict=>T("changed in Stash and locally"),_=>"",
 };
 /// <summary>A short readable form of a field value; long lists are cut.</summary>
 private static string Display(JsonElement value)
 {
  var text=value.ValueKind switch
  {
   JsonValueKind.Null or JsonValueKind.Undefined=>"–",JsonValueKind.String=>value.GetString()!,JsonValueKind.True=>T("Yes"),JsonValueKind.False=>T("No"),
   JsonValueKind.Array=>Tn(value.GetArrayLength(),"1 entry","{0:N0} entries")+(value.GetArrayLength()>0?": "+string.Join(", ",value.EnumerateArray().Take(6).Select(Short)):""),
   JsonValueKind.Object when value.TryGetProperty("sha256",out _)&&value.TryGetProperty("bytes",out var bytes)&&bytes.TryGetInt64(out var size)=>MediaCard.SizeText(size),
   _=>value.ToString(),
  };
  return text.Length>400?text[..400]+" …":text;
  static string Short(JsonElement item)=>item.ValueKind==JsonValueKind.Array?string.Join(": ",item.EnumerateArray().Select(part=>part.ToString()).Where(part=>part.Length>0)):
   item.ValueKind==JsonValueKind.Object&&item.TryGetProperty("title",out var title)?title.ToString():item.ToString();
 }

 // ---- Step 4: import -------------------------------------------------------------------------------------------------
 private readonly TextBox reportBox=new(){IsReadOnly=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,BorderThickness=new Thickness(0)};
 private FrameworkElement reportPage=>reportBox;

 private static StashImportItem Item(Row row)
 {
  var preview=row.Preview;var isNew=preview.Binding is not {Detached:false}&&row.Target==null&&!(preview.Draft.Kind==StashEntityKind.Tag&&preview.Binding!=null);
  // Changed fields not taken are recorded as kept, so a later import shows them as decided.
  var fields=isNew?[]:preview.Fields.Where(f=>f.Status!=StashFieldStatus.Equal)
   .Select(f=>new StashFieldChoice(f.Field,row.UseSource.Contains(f.Field)?StashFieldDecision.UseSource:StashFieldDecision.KeepLocal)).ToArray();
  return new(preview.Draft.SourceOrdinal)
  {
   IdentityId=row.NewIdentity?null:row.Identity?.Id,ExpectedIdentityRevision=row.NewIdentity?null:row.Identity?.Revision,
   Target=row.Target,CreateTarget=row.CreateTarget,TargetName=row.TargetName,ConfirmVersions=row.ConfirmVersions,Fields=fields,
  };
 }

 internal async Task ApplyAsync()
 {
  var chosen=rows.Where(r=>r.Include).ToArray();if(chosen.Length==0)return;
  if(chosen.FirstOrDefault(r=>r.Missing!=null) is {} open){list.SelectedItem=open;throw new InvalidOperationException(open.Missing);}
  var(snapshot,profile,performers,studios)=(Snapshot!,Profile!,Category(performerCategory),Category(studioCategory));
  // Batches follow the dependency order, so every record finds what it refers to in its own or an earlier batch.
  var ordered=chosen.OrderBy(r=>Array.IndexOf(Order,r.Preview.Draft.Kind)).ToArray();
  var created=0;var updated=0;var unchanged=0;var warnings=new List<string>();var done=0;string? failure=null;var videos=new List<Guid>();
  using(var busy=Begin(T("Importing …")))
  {
   try
   {
    for(var start=0;start<ordered.Length;start+=BatchSize)
    {
     var batch=ordered.Skip(start).Take(BatchSize).ToArray();profile=repository.GetStashSource(profile.Id)??throw new InvalidOperationException(T("The import source no longer exists."));
     var result=await repository.ApplyStashImportAsync(snapshot,new(profile.Id,profile.Revision,performers,studios,batch.Select(Item).ToArray()),busy.Token,archives);
     created+=result.Created;updated+=result.Updated;unchanged+=result.Unchanged;warnings.AddRange(result.Warnings);done+=batch.Length;
     videos.AddRange(result.Bindings.Where(binding=>binding.Target is {Kind:StashTargetKind.Video,Id:not null}).Select(binding=>binding.Target.Id!.Value));
     frame.Status.Text=T("Importing … {0:N0} / {1:N0}",done,ordered.Length);
    }
   }
   catch(Exception error)when(error is OperationCanceledException or IOException or InvalidDataException or ArgumentException or InvalidOperationException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
   {failure=error is OperationCanceledException?T("Cancelled"):error.Message;}
  }
  if(done>0)imported(videos);
  var lines=new List<string>{T("New: {0:N0} · Updated: {1:N0} · Unchanged: {2:N0}",created,updated,unchanged)};
  if(failure!=null)lines.Add(done>0?T("Stopped after {0:N0} of {1:N0} records; completed batches stay imported. {2}",done,ordered.Length,failure):T("Nothing was imported. {0}",failure));
  var distinct=warnings.Distinct().ToArray();
  if(distinct.Length>0){lines.Add("");lines.Add(T("Notes:"));lines.AddRange(distinct.Take(500));if(distinct.Length>500)lines.Add(T("… and {0:N0} more",distinct.Length-500));}
  Report=string.Join("\n",lines);reportBox.Text=Report;frame.Status.Text=failure??T("Import finished");
  if(failure!=null&&done==0)return;
  Snapshot?.Dispose();Snapshot=null;GoTo(3);
 }
}
