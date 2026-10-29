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

internal sealed class HistoricalLibraryWindow : Window
{
 private readonly CatalogRepository catalog;private readonly IReadOnlyCollection<Guid> selected;private readonly Func<Guid,IReadOnlyCollection<Guid>,Task> apply;
 private readonly TabControl tabs=MigrationViews.Tabs();private readonly ListBox list=new();private readonly TextBox details=new() { IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto };
 private readonly Button preview=new() { Content=T("Check tag template for the selected items …") };
 internal int EntryCount=>list.Items.Count;
 internal HistoricalLibraryWindow(Window owner,CatalogRepository catalog,IReadOnlyCollection<Guid> selected,Func<Guid,IReadOnlyCollection<Guid>,Task> apply)
 {
  Owner=owner;this.catalog=catalog;this.selected=selected;this.apply=apply;Title=T("Historical templates · FlixyPix");Width=1100;Height=730;MinWidth=800;MinHeight=480;WindowStartupLocation=WindowStartupLocation.CenterOwner;ShowInTaskbar=false;
  var root=new DockPanel();
  foreach(var title in new[]{T("Tag templates"),T("Search drafts"),T("Job drafts"),T("Parser drafts")})tabs.Items.Add(new TabItem { Header=title });DockPanel.SetDock(tabs,Dock.Top);root.Children.Add(tabs);
  var frame=new WindowFrame(T("Historical templates and inactive drafts"),T("Search, parser and job drafts are inactive. Stored commands are only shown as text."),root,"",scroll:false);
  preview.Margin=new Thickness(8,0,0,0);frame.Buttons.Children.Add(preview);frame.Add(T("Close"),Close,cancel:true);Content=frame;
  var grid=new Grid { Margin=new Thickness(0,12,0,0) };grid.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(330) });grid.ColumnDefinitions.Add(new ColumnDefinition());root.Children.Add(grid);grid.Children.Add(list);Grid.SetColumn(details,1);grid.Children.Add(details);
  list.DisplayMemberPath=nameof(HistoryChoice.Label);list.SelectionChanged+=(_,_)=>ShowDetails();tabs.SelectionChanged+=(_,e)=>{if(ReferenceEquals(e.Source,tabs))LoadEntries();};preview.Click+=async (_,_)=>await PreviewAsync();tabs.SelectedIndex=0;LoadEntries();
 }
 private sealed record HistoryChoice(object Value,string Label);
 internal void ShowParserDrafts()=>tabs.SelectedIndex=3;
 private void LoadEntries()
 {
  list.ItemsSource=tabs.SelectedIndex switch
  {
   1=>catalog.GetLegacySearchDrafts().Select(s=>new HistoryChoice(s,s.Name+T(" · inactive"))).ToArray(),
   2=>catalog.GetLegacyJobDrafts().Select(j=>new HistoryChoice(j,(j.ClipName??j.LegacyId)+T(" · inactive"))).ToArray(),
   3=>catalog.GetLegacyParserDrafts().Select(p=>new HistoryChoice(p,T("Parser {0} · inactive", p.LegacyId))).ToArray(),
   _=>catalog.GetTagPresets().Select(p=>new HistoryChoice(p,p.Name+" · "+(p.Kind==MediaKind.Image?T("Images"):"Videos"))).ToArray()
  };list.SelectedIndex=list.Items.Count>0?0:-1;ShowDetails();
 }
 private void ShowDetails()
 {
  var value=(list.SelectedItem as HistoryChoice)?.Value;
  details.Text=value switch
  {
   TagPreset p=>T("{0}\n{1}\n\nTags: {2}\n\n", p.Name, MigrationViews.Projection(p.Status), string.Join(", ",p.Tags))+string.Join('\n',p.TagCategories.Select(c=>c.Key+": "+string.Join(", ",c.Value)))+T("\n\nNotes:\n")+string.Join('\n',p.Warnings),
   LegacySearchDraft s=>T("{0}\nInactive search draft · {1}\n\nRecognized fields:\n", s.Name, MigrationViews.Projection(s.Status))+string.Join('\n',s.Fields.Select(f=>f.Key+": "+f.Value))+T("\n\nNotes:\n")+string.Join('\n',s.Warnings),
   LegacyJobDraft j=>T("Job draft {0}\nInactive · {1}\n\nSource: {2}\nHistorical target: {3}\nClip target: {4}\nDuration: {5}\n\nStored command (text only):\n{6}\n\nNotes:\n",
    j.LegacyId, MigrationViews.Projection(j.Status), j.InputPath, j.OutputPath, j.ClipDestinationPath, j.DurationSeconds,
    j.OriginalCommand)+string.Join('\n',j.Warnings),
   LegacyParserDraft p=>T("Parser draft {0}\nInactive · {1}\nChecksum: {2}\n\nRecognized text fields:\n", p.LegacyId,
    MigrationViews.Projection(p.Status),
    p.PayloadSha256)+string.Join('\n',p.Strings.Select(f=>f.Key+": "+f.Value))+T("\n\nNumbers and options:\n")+string.Join('\n',p.Numbers.Select(f=>f.Key+": "+f.Value).Concat(p.Flags.Select(f=>f.Key+": "+(f.Value?T("yes"):T("no")))))+T("\n\nNotes:\n")+string.Join('\n',p.Warnings),
   _=>T("There are no historical entries in this area yet.")
  };
  preview.IsEnabled=value is TagPreset p2&&p2.Status!=HistoricalProjectionStatus.Opaque&&selected.Count>0;
 }
 private async Task PreviewAsync()
 {
  if((list.SelectedItem as HistoryChoice)?.Value is not TagPreset preset)return;
  try
  {
   var plan=catalog.PreviewTagPreset(preset.Id,selected);var panel=new DockPanel { Margin=new Thickness(20) };
   var dialog=new Window { Owner=this,ShowInTaskbar=false,Title=T("Preview of the tag template"),Width=1000,Height=620,Content=panel,WindowStartupLocation=WindowStartupLocation.CenterOwner };
   var header=new TextBlock { Text=T("Template “{0}” · {1} items\nTags are added; existing tags are kept.", preset.Name, plan.Changes.Count),TextWrapping=TextWrapping.Wrap };DockPanel.SetDock(header,Dock.Top);panel.Children.Add(header);
   var buttons=new StackPanel { Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right };DockPanel.SetDock(buttons,Dock.Bottom);panel.Children.Add(buttons);
   var cancel=new Button { Content=T("Cancel"),IsCancel=true };buttons.Children.Add(cancel);var accept=new Button { Content=T("Add tags now"),IsEnabled=plan.Changes.Count>0 };accept.Click+=(_,_)=>dialog.DialogResult=true;buttons.Children.Add(accept);
   var changes=MigrationViews.List();MigrationViews.Columns(changes,("Medium","Title",240),(T("Previous tags"),"Before",320),(T("Tags after applying"),"After",360));changes.ItemsSource=plan.Changes.Select(c=>new
    { c.Title,Before=string.Join(", ",c.BeforeTags),After=string.Join(", ",c.AfterTags) });panel.Children.Add(changes);
   if(dialog.ShowDialog()==true){preview.IsEnabled=false;await apply(preset.Id,selected);}
  }
  catch(Exception error){Message.Show(this,error.Message,T("Tag template"),MessageBoxButton.OK,MessageBoxImage.Warning);}
  finally{ShowDetails();}
 }
}
