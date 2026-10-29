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

internal sealed class MigrationConflictWindow : Window
{
 private readonly List<CheckBox> fields=[];
 internal IReadOnlyList<string> SelectedFields=>fields.Where(c=>c.IsChecked==true).Select(c=>(string)c.Tag).ToArray();
 internal void SelectField(string name)=>fields.Single(c=>Equals(c.Tag,name)).IsChecked=true;
 internal MigrationConflictWindow(Window owner,MigrationConflictPreview preview)
 {
  Owner=owner;Title=T("Media conflict · FlixyPix");Width=1120;Height=760;MinWidth=850;MinHeight=500;WindowStartupLocation=WindowStartupLocation.CenterOwner;ShowInTaskbar=false;
  var root=new DockPanel();
  var frame=new WindowFrame(T("Media conflict: apply selected source values"),T("Only marked fields receive the source value. All other fields keep their current value. Identity and origin stay protected; media files are not changed."),root,"",scroll:false);
  var accept=frame.Add(T("Apply selected source values"),()=>DialogResult=true,primary:true);accept.IsEnabled=false;frame.Add(T("Keep current state"),Close,cancel:true);Content=frame;
  var panel=new StackPanel();root.Children.Add(new ScrollViewer { Content=panel,VerticalScrollBarVisibility=ScrollBarVisibility.Auto });
  var incoming=preview.Conflict.IncomingValues;
  if(incoming==null){panel.Children.Add(new TextBlock { Text=T("No comparable field values are stored for this older conflict. Please reconcile a new snapshot."),TextWrapping=TextWrapping.Wrap });return;}
  foreach(var field in preview.Conflict.ChangedFields.Where(f=>f is not ("id" or "legacyId" or "extra")&&incoming.ContainsKey(f)))
  {
   var row=new Grid { Margin=new Thickness(0,0,0,12) };row.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(210) });row.ColumnDefinitions.Add(new ColumnDefinition());row.ColumnDefinitions.Add(new ColumnDefinition());
   var check=new CheckBox { Content=MigrationViews.Field(field),Tag=field,Margin=new Thickness(0,7,12,0),VerticalAlignment=VerticalAlignment.Top };fields.Add(check);row.Children.Add(check);
   check.Checked+=(_,_)=>accept.IsEnabled=fields.Any(c=>c.IsChecked==true);check.Unchecked+=(_,_)=>accept.IsEnabled=fields.Any(c=>c.IsChecked==true);
   preview.CurrentValues.TryGetValue(field,out var current);
   AddValue(1,T("Current value"),MigrationViews.Value(current));AddValue(2,T("Source value"),MigrationViews.Value(incoming[field]));panel.Children.Add(row);
   void AddValue(int column,string label,string text)
   {var cell=new StackPanel { Margin=new Thickness(0,0,10,0) };cell.Children.Add(new TextBlock { Text=label,FontSize=12
    });cell.Children.Add(new TextBox {
    Text=text,IsReadOnly=true,TextWrapping=TextWrapping.Wrap,MaxHeight=120,VerticalScrollBarVisibility=ScrollBarVisibility.Auto
    });Grid.SetColumn(cell,column);row.Children.Add(cell);}
  }
  if(fields.Count==0)panel.Children.Add(new TextBlock { Text=T("There are no data fields available to import."),TextWrapping=TextWrapping.Wrap });
 }
}

internal sealed class PathMappingPreviewWindow : Window
{
 internal PathMappingPreviewWindow(Window owner,PathMappingPlan plan)
 {
  Owner=owner;Title=T("Assign catalog paths · FlixyPix");Width=1100;Height=720;MinWidth=800;MinHeight=480;WindowStartupLocation=WindowStartupLocation.CenterOwner;ShowInTaskbar=false;
  var root=new DockPanel();
  var frame=new WindowFrame(T("Preview of the catalog paths"),T("Only catalog entries are changed. Files and drives are not checked; files are not moved."),root,"",scroll:false);
  frame.Status.Text=T("{0:N0} path changes · {1:N0} conflicts", plan.Changes.Count, plan.Conflicts.Count);
  frame.Add(T("Apply displayed catalog paths"),()=>DialogResult=true,primary:true).IsEnabled=plan.Changes.Count>0&&plan.Conflicts.Count==0;frame.Add(T("Cancel"),Close,cancel:true);Content=frame;
  var tabs=MigrationViews.Tabs();var changes=MigrationViews.List();MigrationViews.Columns(changes,(T("Previous path"),"Before",470),(T("New path"),"After",470));changes.ItemsSource=plan.Changes;
  tabs.Items.Add(new TabItem { Header=T("All path changes"),Content=changes });tabs.Items.Add(new TabItem {
   Header=T("Conflicts"),Content=new TextBox {
   IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Text=plan.Conflicts.Count==0?T("No collisions in the catalog plan."):string.Join("\n\n",plan.Conflicts)
   } });root.Children.Add(tabs);
 }
}
