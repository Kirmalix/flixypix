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

internal sealed record NewLibraryMigration(string DirectoryPath,string ReportPath,ImportResult Result);

public partial class MainWindow
{

 private static string? PickLegacyExchange(Window owner)
 {
  var picker=new OpenFileDialog { AddToRecent = Privacy.AddToRecent, Title=T("Choose the exported old catalog"),Filter=T("Legacy catalog export|*.jsonl"),CheckFileExists=true };
  return picker.ShowDialog(owner)==true?picker.FileName:null;
 }
 private async void ImportLegacyNewLibrary()
 { if(PickLegacyExchange(this) is {} input)await ImportLegacyNewLibraryAsync(input); }

 private async Task ImportLegacyNewLibraryAsync(string input)
 {
  var parent=new OpenFolderDialog { AddToRecent = Privacy.AddToRecent, Title=T("Choose the folder for the new, separate library") };if(parent.ShowDialog(this)!=true)return;
  var fields=Dialogs.Fields(this,T("New library from legacy data"),(T("Name of the new subfolder"),$"FlixyPix-migriert-{DateTime.Now:yyyyMMdd-HHmmss}"));if(fields==null)return;
  var name=fields[0].Trim();
  if(name.Length==0||name is "." or ".."||name!=Path.GetFileName(name)||name.IndexOfAny(Path.GetInvalidFileNameChars())>=0||name.EndsWith('.')||name.EndsWith(' '))
  {Message.Show(this,Tc("migration", "Please enter a valid new folder name."));return;}
  var target=Path.Combine(parent.FolderName,name);
  await RunOperation(T("Create new library from legacy data"),async token=>
  {
   var migrated=await MigrateLegacyToNewLibraryAsync(input,target,Guid.NewGuid().ToString("N"),token);
   ShowNewLibraryMigration(migrated);
  },refresh:false);
 }

 internal static async Task<NewLibraryMigration> MigrateLegacyToNewLibraryAsync(string input,string destination,string sourceId,CancellationToken token=default,bool adoptAppSettings=true)
 {
  var target=Path.GetFullPath(destination);FileAccessPolicy.Current.EnsureWritable(target);
  if(Directory.Exists(target)||File.Exists(target))throw new IOException(T("The target folder already exists. Please choose a new name."));
  var parent=Path.GetDirectoryName(target)??throw new ArgumentException(T("A parent target folder is missing."));
  if(!Directory.Exists(parent))throw new DirectoryNotFoundException(T("The parent target folder is missing."));
  token.ThrowIfCancellationRequested();var work=Path.Combine(parent,".flixypix-migration-"+Guid.NewGuid().ToString("N"));
  FileAccessPolicy.Current.EnsureWritable(work);Directory.CreateDirectory(work);var published=false;
  try
  {
   var result=await Task.Run(()=>
   {
    using var catalog=new CatalogRepository(Path.Combine(work,"catalog.db"));
    var imported=catalog.ImportLegacyJsonl(input,new LegacyImportOptions { SourceId=sourceId,AdoptAppSettings=adoptAppSettings },token);
    if(imported.Migration==null)throw new InvalidDataException(T("The old catalog was not imported.\n")+string.Join('\n',imported.Errors));
    catalog.ExportMigrationReport(imported.Migration.Batch,Path.Combine(work,"migration-report.json"));return imported;
   },token);
   token.ThrowIfCancellationRequested();FileAccessPolicy.Current.EnsureWritable(target);FileAccessPolicy.Current.EnsureWritable(work);
   Directory.Move(work,target);published=true;
   return new(target,Path.Combine(target,"migration-report.json"),result);
  }
  finally { if(!published)DeleteOwnedMigrationWork(work); }
 }
 private static void DeleteOwnedMigrationWork(string path)
 {
  // No recursive deletion: a failed import owns only its new top-level database/report files.
  if(!Directory.Exists(path))return;
  FileAccessPolicy.Current.EnsureWritable(path);var directory=new DirectoryInfo(path);
  if(!directory.Name.StartsWith(".flixypix-migration-",StringComparison.Ordinal)||(directory.Attributes&FileAttributes.ReparsePoint)!=0)return;
  var files=directory.EnumerateFileSystemInfos().ToArray();
  if(files.Any(f=>(f.Attributes&(FileAttributes.Directory|FileAttributes.ReparsePoint))!=0))return;
  foreach(var file in files){FileAccessPolicy.Current.EnsureWritable(file.FullName);File.Delete(file.FullName);}Directory.Delete(path,false);
 }
 private void ShowNewLibraryMigration(NewLibraryMigration migrated)
 {
  var report=migrated.Result.Migration!;var panel=new StackPanel();
  var dialog=new Window { Owner=this,Title=T("New library created · FlixyPix"),Width=820,SizeToContent=SizeToContent.Height,MaxHeight=760,WindowStartupLocation=WindowStartupLocation.CenterOwner,ShowInTaskbar=false };
  panel.Children.Add(new TextBlock { Text=migrated.DirectoryPath+"\n\n"+MigrationReportWindow.Summary(report)+
   T("\n\nMedia paths are kept. Media files and drives were not checked; a drive that is temporarily unavailable therefore does not count as data loss.\n\nThe current catalog stays open."),TextWrapping=TextWrapping.Wrap });
  if(migrated.Result.Errors.Count>0)panel.Children.Add(new TextBlock {
   Text=Tn(migrated.Result.Errors.Count, "1 projection error: the original data is kept. Please check the report.",
   "{0} projection errors: the original data is kept. Please check the report."),TextWrapping=TextWrapping.Wrap,Margin=new
   Thickness(0,12,0,0),Foreground=SettingsUi.Error });
  if(migrated.Result.Compatibility is { IsComplete: false } compatibility)panel.Children.Add(CompatibilityPanel(compatibility));
  var frame=new WindowFrame(T("The separate library has been created"),null,panel,"");dialog.Content=frame;
  var buttons=frame.Buttons;
  var details=new Button { Content=T("View migration report"),Margin=new Thickness(8,0,0,0) };details.Click+=(_,_)=>{using var catalog=new CatalogRepository(Path.Combine(migrated.DirectoryPath,"catalog.db"));new MigrationReportWindow(dialog,catalog).ShowDialog();};buttons.Children.Add(details);
  var choose=new Button { Content=T("Open on next start"),Margin=new Thickness(8,0,0,0) };choose.Click+=(_,_)=>
  {
   FileAccessPolicy.Current.EnsureWritable(migrated.DirectoryPath);_pendingDataDirectory=migrated.DirectoryPath;
   if(_persistPreferences)(_settings with { DataDirectory=migrated.DirectoryPath }).Save();
   choose.IsEnabled=false;choose.Content=T("Chosen for the next start");
  };buttons.Children.Add(choose);
  var restart=new Button { Content=T("Restart now with this library"),Style=(Style)Application.Current.Resources["Primary"],Margin=new Thickness(8,0,0,0) };restart.Click+=(_,_)=>
  {
   // A fresh process opens the new library cleanly; the current catalog closes normally first.
   FileAccessPolicy.Current.EnsureWritable(migrated.DirectoryPath);_pendingDataDirectory=migrated.DirectoryPath;
   if(_persistPreferences)(_settings with { DataDirectory=migrated.DirectoryPath }).Save();
   dialog.Close();if(_persistPreferences&&Environment.ProcessPath is {} exe){Process.Start(new ProcessStartInfo(exe){UseShellExecute=false});Close();}
  };if(_persistPreferences)buttons.Children.Add(restart);
  frame.Add(T("Close"),dialog.Close,cancel:true);dialog.ShowDialog();
 }
 /// <summary>Areas of a catalog from another version of the original: what was complete, partial or absent. Everything readable was
 /// imported; the rest is named so nobody looks for it in vain.</summary>
 internal static FrameworkElement CompatibilityPanel(LegacyCompatibilityReport report)
 {
  var panel=new StackPanel { Margin=new Thickness(0,14,0,0) };
  panel.Children.Add(new TextBlock { Text=T("This catalog differs from what FlixyPix expects. Everything readable was imported:"),TextWrapping=TextWrapping.Wrap,FontWeight=FontWeights.SemiBold });
  foreach(var area in report.Areas)
  {
   var (glyph,brush,state)=area.Status switch
   {
    LegacyAreaStatus.Complete=>("",SettingsUi.Good,T("complete")),
    LegacyAreaStatus.Partial=>("",SettingsUi.Gold,T("partly")),
    _=>("",SettingsUi.Muted,T("not in this catalog")),
   };
   var row=new DockPanel { Margin=new Thickness(0,6,0,0) };
   var icon=SettingsUi.Icon(glyph,14);icon.Foreground=brush;icon.Margin=new Thickness(0,1,8,0);icon.VerticalAlignment=VerticalAlignment.Top;DockPanel.SetDock(icon,Dock.Left);row.Children.Add(icon);
   var missing=area.MissingTables.Concat(area.MissingColumns).ToList();
   row.Children.Add(new TextBlock { Text=T(area.Area)+" · "+state+(area.Status==LegacyAreaStatus.Partial&&missing.Count>0?T(" (missing: {0})",string.Join(", ",missing)):""),TextWrapping=TextWrapping.Wrap });
   panel.Children.Add(row);
  }
  if(report.UnknownTables.Count>0)panel.Children.Add(new TextBlock { Text=T("Unknown tables were kept unchanged in the migration report: {0}",string.Join(", ",report.UnknownTables)),TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,8,0,0),Foreground=SettingsUi.Muted });
  return panel;
 }
 private async void ImportLegacySnapshot()
 {
  var sources=_repository.GetLegacySources();if(sources.Count==0){Message.Show(this,T("This catalog has no assigned old catalog source yet. Please create a new library from legacy data first."));return;}
  var input=PickLegacyExchange(this);if(input==null)return;
  var source=ChooseLegacySource(sources);if(source==null)return;
  await RunOperation(T("Reconcile old catalog snapshot"),async token=>
  {
   var result=await Task.Run(()=>_repository.ImportLegacyJsonl(input,new LegacyImportOptions { SourceId=source.SourceId },token),token);
   if(result.Migration==null){ShowImportResult(result);return;}
   await RefreshAsync(includePreviews:false);new MigrationReportWindow(this,_repository,result.Migration.Batch,()=>RefreshAsync(includePreviews:false)).ShowDialog();
  },refresh:false);
 }
 private LegacySource? ChooseLegacySource(IReadOnlyList<LegacySource> sources)
 {
  var dialog=new Window { Owner=this,Title=T("Assign snapshot · FlixyPix"),Width=720,SizeToContent=SizeToContent.Height,WindowStartupLocation=WindowStartupLocation.CenterOwner,ShowInTaskbar=false };
  var choices=sources.Select(s=>new SourceChoice(s,T("{0} · previous snapshot {1}…", s.SourceId, s.SnapshotSha256[..Math.Min(12,s.SnapshotSha256.Length)]))).ToArray();
  var combo=new ComboBox { ItemsSource=choices,DisplayMemberPath=nameof(SourceChoice.Label),SelectedIndex=0 };
  var frame=new WindowFrame(T("Assign snapshot to an existing source"),T("Which source imported earlier should be updated? Local edits are kept; differences are listed in the report afterwards. Media files are not opened."),combo,"");
  frame.Add(T("Reconcile snapshot with this source"),()=>dialog.DialogResult=true,primary:true);frame.Add(T("Cancel"),dialog.Close,cancel:true);dialog.Content=frame;
  return dialog.ShowDialog()==true?(combo.SelectedItem as SourceChoice)?.Source:null;
 }
 private sealed record SourceChoice(LegacySource Source,string Label);
 private void ShowMigrationReports()=>new MigrationReportWindow(this,_repository,onChanged:()=>RefreshAsync(includePreviews:false)).ShowDialog();
 private async void MapMigrationPaths()
 {
  var fields=Dialogs.Fields(this,T("Assign media paths in the catalog"),(T("Previous folder prefix"),Selected is {} item?Path.GetDirectoryName(item.Path)??"":@"X:\BisherigeMedien"),(T("Current folder prefix"),@"U:\Medien"));if(fields==null)return;
  await RunOperation(T("Assign media paths"),async token=>
  {
   var plan=await Task.Run(()=>_repository.PlanPathMapping([new(fields[0],fields[1])],token),token);
   if(new PathMappingPreviewWindow(this,plan).ShowDialog()!=true)return;
   var count=await Task.Run(()=>_repository.ApplyPathMappingPlan(plan,token),token);await RefreshAsync(includePreviews:false);
   Message.Show(this,Tn(count, "1 catalog path changed. The media file was neither opened nor moved.", "{0:N0} catalog paths changed. The media files were neither opened nor moved."),T("Path assignment"));
  },refresh:false);
 }
 private void ShowHistoricalLibrary()=>new HistoricalLibraryWindow(this,_repository,MediaList.SelectedItems.OfType<MediaCard>().Select(c=>c.Item.Id).ToArray(),async (preset,ids)=>
 {
  await RunOperation(T("Apply tag template"),async token=>{var changed=await Task.Run(()=>_repository.ApplyTagPreset(preset,ids,token),token);await
   RefreshAsync(includePreviews:false);Message.Show(this,Tn(changed, "Tags added to 1 item.",
   "Tags added to {0} items."),T("Tag template"));},refresh:false);
 }).ShowDialog();
 private void ShowOfflineExportHelp()=>Dialogs.Text(this,T("Prepare offline export"),
  T("Old SDF catalogs are first exported in Windows Sandbox. The original programs and the SQLCE provider belong only in the offline guest.\n\nPreparation in the project folder:\n.\\scripts\\original\\New-OfflineScenario.ps1 -Database ..\\mmvdb.sdf -PasswordFile ..\\mmvdb-password.txt\n\nThe matching Offline.wsb is then in the newly created subfolder of artifacts\\offline-scenario.\n\nGuide: docs\\original\\windows-sandbox-einrichten.md\n\nThe media drive is not needed for the export and is not mounted in the guest. This notice starts no programs and changes no Windows feature."));
}
