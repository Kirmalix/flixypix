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

internal sealed class MediaAvailabilityWindow : Window
{
 private readonly CatalogRepository catalog;private readonly ListView list=MigrationViews.List();private readonly TextBlock pageLabel=new();private readonly Button previous=new() { Content=T("Back") },next=new() { Content=T("Check next page") };
 private readonly TaskCompletionSource ready=new(TaskCreationOptions.RunContinuationsAsynchronously);private int offset;private bool checking;
 internal Task ReadyTask=>ready.Task;
 internal int OfflineCount { get; private set; }
 internal MediaAvailabilityWindow(Window owner,CatalogRepository catalog)
 {
  Owner=owner;this.catalog=catalog;Title=T("Media availability · FlixyPix");Width=1080;Height=680;MinWidth=800;MinHeight=450;WindowStartupLocation=WindowStartupLocation.CenterOwner;ShowInTaskbar=false;
  var root=new DockPanel();
  var paging=new WrapPanel { Margin=new Thickness(0,8,0,0) };DockPanel.SetDock(paging,Dock.Bottom);root.Children.Add(paging);paging.Children.Add(previous);paging.Children.Add(pageLabel);paging.Children.Add(next);
  var frame=new WindowFrame(T("Media availability"),T("Only the shown page is checked. Drives that are temporarily unavailable are not queried; offline does not mean deleted."),root,"",scroll:false);
  frame.Add(T("Close"),Close,primary:true,cancel:true);Content=frame;
  MigrationViews.Columns(list,(T("Path"),"Path",650),("Status","Status",220),(T("Bytes"),"Size",140));root.Children.Add(list);previous.Click+=async (_,_)=>{offset=Math.Max(0,offset-120);await LoadPageAsync();};next.Click+=async (_,_)=>{offset+=120;await LoadPageAsync();};
  Loaded+=async (_,_)=>{try{await LoadPageAsync();ready.TrySetResult();}catch(Exception error){ready.TrySetException(error);}};
  Closing+=(_,e)=>{if(checking)e.Cancel=true;};
 }
 private async Task LoadPageAsync()
 {
  checking=true;previous.IsEnabled=next.IsEnabled=false;
  try
  {
   var total=await Task.Run(()=>catalog.QueryPage(new() { Limit=1 }).TotalCount);var rows=await Task.Run(()=>catalog.CheckMediaAvailability(offset));OfflineCount=rows.Count(r=>r.Status==MediaAvailability.TemporarilyOffline);
   list.ItemsSource=rows.Select(r=>new { r.Path,Status=r.Status switch { MediaAvailability.Available=>T("Available"),MediaAvailability.Missing=>T("File missing"),MediaAvailability.TemporarilyOffline=>T("Temporarily offline"),_=>T("Unable to access") },r.Size });
   pageLabel.Text=T("{0:N0}–{1:N0} of {2:N0}", (rows.Count==0?0:offset+1), offset+rows.Count, total);pageLabel.Margin=new Thickness(8,9,14,0);previous.IsEnabled=offset>0;next.IsEnabled=offset+rows.Count<total;
  }
  catch(Exception error){Message.Show(this,error.Message,T("Availability check"),MessageBoxButton.OK,MessageBoxImage.Warning);}
  finally{checking=false;}
 }
}
