using System.IO;
using System.Windows;
using System.Windows.Controls;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

public partial class MainWindow
{
 private (StorageMode Mode, bool Copy)? _storageChange;
 private DesktopSettings? _storageSourceSettings;
 private string? _storageSourceRoot;
 private bool _initialProfileImport;
 private Func<string,CatalogRepository>? _storageOpenCopy;
 private Action<string>? _storageCheckpoint;
 private List<string>? _storageSelectedLibraries;
 internal void OfferProfileImport(bool initial = false)
 {
  if (AppStorage.Current.Mode != StorageMode.Portable || _operation != null) return;
  if (initial && Message.Show(this,T("Copy existing profile data into this portable edition? The originals are kept."),T("Data and storage"),MessageBoxButton.YesNo,MessageBoxImage.Question,MessageBoxResult.No) != MessageBoxResult.Yes) return;
  try
  {
   var profile=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"FlixyPix");
   var path=Path.Combine(profile,"appsettings.json");
   if(new FileInfo(path).Length>1024*1024) throw new InvalidDataException(T("The previous settings are unexpectedly large."));
   var document=System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(path));
   using(document)
   {
    var source=System.Text.Json.JsonSerializer.Deserialize<DesktopSettings>(document) ?? throw new InvalidDataException(T("The previous settings are invalid."));
    if(!document.RootElement.TryGetProperty("DataDirectory",out _)) source=source with {DataDirectory=Path.Combine(profile,"Library")};
    if(!Path.IsPathFullyQualified(source.DataDirectory)) throw new InvalidDataException(T("The previous library folder is invalid."));
    ShowStorageChange(source.Validated([]),profile,StorageMode.Portable,initial);
   }
  }
  catch(Exception failure) when(failure is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { Message.Show(this,failure.Message,T("Data and storage")); }
 }
 private void ChangeStorageMode()
 {
  if (!_persistPreferences || _operation != null) return;
  ShowStorageChange(_settings,AppStorage.Current.Root,AppStorage.Current.Mode==StorageMode.Portable?StorageMode.Profile:StorageMode.Portable,false);
 }
 private void ShowStorageChange(DesktopSettings sourceSettings,string sourceRoot,StorageMode mode,bool initialImport)
 {
  var target=AppStoragePaths.Resolve(AppContext.BaseDirectory,Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"FlixyPix"),mode);
  var dialog=new Window {Owner=this,Title=T("Data and storage"),Width=660,SizeToContent=SizeToContent.Height,MaxHeight=760,WindowStartupLocation=WindowStartupLocation.CenterOwner};
  var panel=new StackPanel {Margin=new Thickness(24)};
  panel.Children.Add(new TextBlock {Text=(mode==StorageMode.Portable?T("Portable"):T("Windows profile"))+"\n"+sourceRoot+" -> "+target.Root,TextWrapping=TextWrapping.Wrap});
  var copy=new RadioButton {Content=T("Copy current libraries; keep the originals"),IsChecked=true,Margin=new Thickness(0,20,0,8)};
  panel.Children.Add(copy);
  var choices=new List<(string Path,CheckBox Box)>();
  foreach(var path in sourceSettings.Libraries.Select(l=>l.Directory).Prepend(sourceSettings.DataDirectory).Distinct(StringComparer.OrdinalIgnoreCase))
  {
   var box=new CheckBox {Content=path,IsChecked=LibraryCatalogs.SameDirectory(path,sourceSettings.DataDirectory)||path.StartsWith(sourceRoot+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase),Margin=new Thickness(12,4,0,4)};
   choices.Add((path,box));panel.Children.Add(box);
  }
  var useExisting=new RadioButton {Content=T("Use the existing destination data"),Margin=new Thickness(0,8,0,16)};
  panel.Children.Add(useExisting);
  panel.Children.Add(new TextBlock {Text=T("Existing destination libraries and settings are preserved. Media files are not copied. Other FlixyPix windows must be closed."),TextWrapping=TextWrapping.Wrap});
  panel.Children.Add(new TextBlock {Text=T("Existing translation overrides are kept; screenshot name conflicts receive a new name."),TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,8,0,0)});
  var external=sourceSettings.ExternalPrograms.Select(p=>p.Path).Concat(sourceSettings.QuickAccessFolders).Concat(new[]{sourceSettings.Ffmpeg,sourceSettings.Ffprobe,sourceSettings.Mpv}.OfType<string>()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
  if(external.Length>0)panel.Children.Add(new TextBlock {Text=T("External locations remain linked:")+"\n"+string.Join("\n",external),TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,12,0,0)});
  var summary=new TextBlock {Margin=new Thickness(0,12,0,0),TextWrapping=TextWrapping.Wrap}; panel.Children.Add(summary);
  var start=new Button {Content=T("Apply and restart"),Margin=new Thickness(0,20,0,0),Padding=new Thickness(12),IsEnabled=false};
  start.Click+=(_,_)=>dialog.DialogResult=true;panel.Children.Add(start);
  dialog.Content=new ScrollViewer {Content=panel,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
  using var cancellation=new CancellationTokenSource(); int revision=0;
  async void Preview()
  {
   var version=++revision; start.IsEnabled=false;
   var selected=choices.Where(c=>c.Box.IsChecked==true).Select(c=>c.Path).ToArray(); var copying=copy.IsChecked==true;
   try
   {
    var estimate=await Task.Run(()=>copying?StorageTransferService.Measure(selected,sourceRoot,cancellation.Token):new StorageSize(0,0),cancellation.Token);
    if(version!=revision||cancellation.IsCancellationRequested) return;
    if(copying&&selected.Length==0) {summary.Text=T("Select at least one library.");return;}
    var free=StorageTransferService.Available(target.Root);
    summary.Text=T("Estimated copy size: {0}",MediaCard.SizeText(estimate.Total))+(free.HasValue?"\n"+T("Free space at destination: {0}",MediaCard.SizeText(free.Value)):"");
    StorageTransferService.CheckSpace(target,estimate);
    start.IsEnabled=true;
   }
   catch(OperationCanceledException) { }
   catch(Exception failure) when(failure is IOException or UnauthorizedAccessException) { if(version==revision) summary.Text=failure.Message; }
  }
  foreach(var choice in choices) {choice.Box.Checked+=(_,_)=>Preview();choice.Box.Unchecked+=(_,_)=>Preview();}
  copy.Checked+=(_,_)=>Preview(); useExisting.Checked+=(_,_)=>Preview(); dialog.Loaded+=(_,_)=>Preview();
  var accepted=dialog.ShowDialog()==true;cancellation.Cancel();if(!accepted)return;
  try
  {
   using(var check=AppStorage.ReserveTransfer(sourceRoot,target.Root,target.ApplicationDirectory)) { }
   foreach(var library in sourceSettings.Libraries)
    if(!LibraryCatalogs.SameDirectory(library.Directory,_settings.DataDirectory)&&LibraryInstance.IsOpen(library.Directory))throw new IOException(T("Close the other FlixyPix windows first."));
   _storageSelectedLibraries=choices.Where(c=>c.Box.IsChecked==true).Select(c=>c.Path).ToList();
   _storageSourceSettings=sourceSettings;_storageSourceRoot=sourceRoot;_initialProfileImport=initialImport;
   _storageChange=(mode,copy.IsChecked==true);Close();
  }
  catch(Exception failure) when(failure is IOException or UnauthorizedAccessException) {Message.Show(this,failure.Message,T("Data and storage"));}
 }
 private Task ApplyStorageChangeAsync(StorageMode mode, bool copy, AppStoragePaths? destinationOverride = null)
 {
  var target=destinationOverride ?? AppStoragePaths.Resolve(AppContext.BaseDirectory,Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"FlixyPix"),mode);
  var request=new StorageTransferRequest(_storageSourceSettings ?? _settings,_storageSourceRoot ?? AppStorage.Current.Root,
   target,copy,_initialProfileImport,_storageSelectedLibraries?.ToArray());
  var service=new StorageTransferService(path => _storageOpenCopy?.Invoke(path) ?? SecretDialogs.OpenLibrary(null,path),AppStorage.ReserveTransfer,_storageCheckpoint);
  return service.ApplyAsync(request,_operation?.Token ?? CancellationToken.None);
 }
}
