using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using FlixyPix.Core;
using Microsoft.Win32;

namespace FlixyPix.Desktop;

internal sealed class MigrationReportWindow : Window
{
 private readonly CatalogRepository catalog;private readonly ComboBox reports=new();private readonly TextBlock summary=new() { TextWrapping=TextWrapping.Wrap };
 private readonly TextBlock pageLabel=new();private readonly TabControl tabs=MigrationViews.Tabs();private readonly ListView tables=MigrationViews.List(),rows=MigrationViews.List(),conflicts=MigrationViews.List();
 private readonly Button previous=new() { Content=T("Back") },next=new() { Content=T("Next") },export=new() { Content=T("Export complete report …") },resolve=new() { Content=T("Check selected media conflict …"),IsEnabled=false };
 private readonly Func<Task>? onChanged;private int offset;private bool exporting,resolving;
 internal int VisibleRowCount=>rows.Items.Count;
 internal void ShowRecords()=>tabs.SelectedIndex=1;
 internal MigrationReportWindow(Window owner,CatalogRepository catalog,string? selectedBatch=null,Func<Task>? onChanged=null)
 {
  Owner=owner;this.catalog=catalog;this.onChanged=onChanged;Title=T("Migration report · FlixyPix");Width=1160;Height=820;MinWidth=800;MinHeight=500;WindowStartupLocation=WindowStartupLocation.CenterOwner;ShowInTaskbar=false;
  var root=new DockPanel();
  var header=new StackPanel { Margin=new Thickness(0,0,0,8) };DockPanel.SetDock(header,Dock.Top);root.Children.Add(header);
  summary.Margin=new Thickness(0,8,0,0);header.Children.Add(reports);header.Children.Add(summary);
  var choices=catalog.GetMigrationReports().Select(r=>new ReportChoice(r,T("Source {0} · {1:N0} source rows · export {2}…", r.SourceId, r.Rows, r.ExportSha256[..12]))).ToArray();
  reports.ItemsSource=choices;reports.DisplayMemberPath=nameof(ReportChoice.Label);
  // Paging belongs to the list; report actions sit in the fixed footer.
  var paging=new StackPanel { Orientation=Orientation.Horizontal,Margin=new Thickness(0,8,0,0) };DockPanel.SetDock(paging,Dock.Bottom);root.Children.Add(paging);
  pageLabel.VerticalAlignment=VerticalAlignment.Center;pageLabel.Margin=new Thickness(10,0,10,0);paging.Children.Add(previous);paging.Children.Add(pageLabel);paging.Children.Add(next);
  var frame=new WindowFrame(T("Import and preserved legacy data"),null,root,"",scroll:false);
  foreach(var button in new[]{export,resolve}){button.Margin=new Thickness(8,0,0,0);frame.Buttons.Children.Add(button);}
  frame.Add(T("Close"),Close,primary:true,cancel:true);Content=frame;
  MigrationViews.Columns(tables,(T("Source table"),"Table",260),(T("Rows"),"Rows",100),(T("Partly imported"),"PartiallyProjected",170),(T("Raw data only"),"RawRetained",140),(T("Invalid"),"Invalid",100));
  MigrationViews.Columns(rows,("Position","Ordinal",80),(T("Table"),"Table",160),(T("Old key"),"Key",130),("Status","State",170),(T("Note code"),"Code",240),(T("Target IDs"),"Targets",330));
  MigrationViews.Columns(conflicts,(T("Area"),"Kind",130),(T("Target ID"),"TargetId",300),(T("Differing fields"),"Fields",330),(T("Decision"),"Resolution",200));
  tabs.Items.Add(new TabItem { Header=T("Tables"),Content=tables });tabs.Items.Add(new TabItem { Header=T("Records"),Content=rows });tabs.Items.Add(new TabItem { Header=T("Conflicts"),Content=conflicts });root.Children.Add(tabs);
  reports.SelectionChanged+=(_,_)=>{offset=0;LoadPage();};tabs.SelectionChanged+=(_,e)=>{if(ReferenceEquals(e.Source,tabs)){offset=0;LoadPage();}};
  previous.Click+=(_,_)=>{offset=Math.Max(0,offset-120);LoadPage();};next.Click+=(_,_)=>{offset+=120;LoadPage();};export.Click+=async (_,_)=>await ExportAsync();
  conflicts.SelectionChanged+=(_,_)=>resolve.IsEnabled=!resolving&&conflicts.SelectedItem is ConflictRow row&&row.Conflict.Kind=="media"&&row.Conflict.IncomingValues!=null;
  resolve.Click+=async (_,_)=>await ResolveAsync();Closing+=(_,e)=>{if(exporting||resolving)e.Cancel=true;};reports.SelectedItem=choices.FirstOrDefault(c=>c.Report.Batch==selectedBatch)??choices.LastOrDefault();LoadPage();
 }
 private sealed record ReportChoice(MigrationReport Report,string Label);
 private sealed record ConflictRow(MigrationConflict Conflict)
 {
  public string Kind=>Conflict.Kind;public string TargetId=>Conflict.TargetId;public string Fields=>string.Join(", ",Conflict.ChangedFields.Select(MigrationViews.Field));
  public string Resolution=>Conflict.Resolution switch { "preserved-existing"=>T("Keep local state"),"replaced-explicitly"=>T("Explicitly replaced"),_=>T("Selected fields applied") };
 }
 internal static string Summary(MigrationReport r)=>T("{0:N0} source rows · {1:N0} binary values ({2:N0} bytes) · {3:N0} conflicts\n", r.Rows, r.Blobs, r.BlobBytes, r.Conflicts)+
  T("Preserved as raw data only: {0:N0} · partially imported: {1:N0} · invalid: {2:N0}", r.Tables.Sum(t=>t.RawRetained), r.Tables.Sum(t=>t.PartiallyProjected), r.Tables.Sum(t=>t.Invalid));
 private void LoadPage()
 {
  if(reports.SelectedItem is not ReportChoice choice){summary.Text=T("This library has no migration report yet.");previous.IsEnabled=next.IsEnabled=export.IsEnabled=false;return;}
  var report=choice.Report;summary.Text=Summary(report)+T("\nSource rows and target objects are counted separately. Values kept only in raw form are preserved but not yet fully supported.");
  tables.ItemsSource=report.Tables;
  long total=tabs.SelectedIndex==2?report.Conflicts:report.Rows;var count=0;
  if(tabs.SelectedIndex==1){var page=catalog.GetMigrationRows(report.Batch,offset);count=page.Count;rows.ItemsSource=page.Select(r=>new { r.Ordinal,r.Table,r.Key,State=MigrationViews.Status(r.Status),r.Code,Targets=string.Join(", ",r.TargetIds) });}
  else if(tabs.SelectedIndex==2){var page=catalog.GetMigrationConflicts(report.Batch,offset);count=page.Count;conflicts.ItemsSource=page.Select(c=>new ConflictRow(c));}
  previous.IsEnabled=tabs.SelectedIndex>0&&offset>0;next.IsEnabled=tabs.SelectedIndex>0&&offset+count<total;
  pageLabel.Text=tabs.SelectedIndex==0?T("Table overview"):T("{0:N0}–{1:N0} of {2:N0}", (count==0?0:offset+1), offset+count, total);pageLabel.Margin=new Thickness(8,9,14,0);export.IsEnabled=!exporting;
  resolve.Visibility=tabs.SelectedIndex==2?Visibility.Visible:Visibility.Collapsed;
 }
 private async Task ResolveAsync()
 {
  if(reports.SelectedItem is not ReportChoice choice||conflicts.SelectedItem is not ConflictRow row||!Guid.TryParse(row.TargetId,out var id))return;
  try
  {
   var preview=catalog.PreviewMigrationMediaConflict(choice.Report.Batch,id);var dialog=new MigrationConflictWindow(this,preview);if(dialog.ShowDialog()!=true)return;
   resolving=true;resolve.IsEnabled=false;
   await Task.Run(()=>catalog.ResolveMigrationMediaConflict(choice.Report.Batch,id,dialog.SelectedFields,preview.CurrentSha256));
   if(onChanged!=null)await onChanged();LoadPage();
  }
  catch(Exception error){Message.Show(this,T("The change was not applied. If data changed in the meantime, please open a new preview.\n\n")+error.Message,T("Media conflict"),MessageBoxButton.OK,MessageBoxImage.Warning);}
  finally{resolving=false;resolve.IsEnabled=conflicts.SelectedItem is ConflictRow selected&&selected.Conflict.Kind=="media"&&selected.Conflict.IncomingValues!=null;}
 }
 private async Task ExportAsync()
 {
  if(reports.SelectedItem is not ReportChoice choice)return;
  var picker=new SaveFileDialog { AddToRecent = Privacy.AddToRecent, Title=T("New file for the complete migration report"),Filter=T("Migration report|*.json"),FileName="FlixyPix-Migrationsbericht.json" };if(picker.ShowDialog(this)!=true)return;
  exporting=true;export.IsEnabled=false;
  try{await Task.Run(()=>catalog.ExportMigrationReport(choice.Report.Batch,picker.FileName));Message.Show(this,T("The complete report was saved as a new file."),T("Migration report"));}
  catch(Exception error){Message.Show(this,error.Message,T("The report could not be saved"),MessageBoxButton.OK,MessageBoxImage.Warning);}
  finally{exporting=false;export.IsEnabled=true;}
 }
}

internal static class MigrationViews
{
 internal static Brush Background=>Theme.Brush("WindowBackground");
 internal static ListView List()
 {
  var list=new ListView { Background=Background,Margin=new Thickness(0,12,0,0) };list.SetValue(VirtualizingPanel.IsVirtualizingProperty,true);list.SetValue(ScrollViewer.CanContentScrollProperty,true);
  return list;
 }
 internal static TabControl Tabs()
 {
  var tabs=new TabControl { Background=Background,BorderBrush=Theme.Brush("FieldBorder") };
  var style=new Style(typeof(TabItem));var template=new ControlTemplate(typeof(TabItem));var border=new FrameworkElementFactory(typeof(Border),"body");border.SetResourceReference(Border.BackgroundProperty,"Field");border.SetValue(Border.PaddingProperty,new Thickness(14,9,14,9));
  var content=new FrameworkElementFactory(typeof(ContentPresenter));content.SetValue(ContentPresenter.ContentSourceProperty,"Header");border.AppendChild(content);template.VisualTree=border;
  var selected=new Trigger { Property=TabItem.IsSelectedProperty,Value=true
   };selected.Setters.Add(new Setter(Border.BackgroundProperty,new
   DynamicResourceExtension("ItemSelected"),"body"));template.Triggers.Add(selected);style.Setters.Add(new
   Setter(Control.TemplateProperty,template));style.Setters.Add(new Setter(Control.ForegroundProperty,new
   DynamicResourceExtension("Text")));tabs.ItemContainerStyle=style;return tabs;
 }
 internal static void Columns(ListView list,params (string Label,string Property,double Width)[] columns)
 {var view=new GridView();foreach(var c in columns)view.Columns.Add(new GridViewColumn { Header=c.Label,DisplayMemberBinding=new Binding(c.Property),Width=c.Width });list.View=view;}
 internal static string Status(string value)=>value switch { "partially-projected"=>T("Partly imported"),"raw-retained"=>T("Raw data only"),"invalid"=>T("Invalid"),"projected"=>T("Imported"),_=>value };
 internal static string Projection(HistoricalProjectionStatus status)=>status switch { HistoricalProjectionStatus.Complete=>T("Fully understood"),HistoricalProjectionStatus.Partial=>T("Partly understood"),_=>T("Preserved only") };
 internal static string Field(string name)=>name switch {
  "title"=>T("Title"),"path"=>T("File path"),"kind"=>T("Media type"),"rating"=>T("Rating"),"tags"=>"Tags","description"=>T("Description"),"category"=>T("Media category"),"tagCategories"=>T("Tag categories"),"isFavorite"=>T("Pinned"),"watchLater"=>T("Watch later"),"addedAt"=>T("Added"),"playbackPositionSeconds"=>T("Playback position"),"width"=>T("Width"),"height"=>T("Height"),"fileSize"=>T("File size"),"durationSeconds"=>T("Duration"),"thumbnailPath"=>T("Preview file path"),"playCount"=>T("Play count"),"viewCount"=>T("View count"),"fileCreatedAt"=>T("File created"),_=>name
  };
 internal static string Value(JsonElement value)=>value.ValueKind switch
 {JsonValueKind.Null=>T("Not set"),JsonValueKind.Undefined=>T("Not present"),JsonValueKind.String=>value.GetString()??"",JsonValueKind.True=>T("Yes"),JsonValueKind.False=>T("No"),JsonValueKind.Array=>string.Join(", ",value.EnumerateArray().Select(Value)),JsonValueKind.Object=>string.Join('\n',value.EnumerateObject().Select(p=>p.Name+": "+Value(p.Value))),_=>value.GetRawText()};
}
