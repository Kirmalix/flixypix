using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FlixyPix.Core;
using Microsoft.Win32;

namespace FlixyPix.Desktop;

internal sealed class MediaArtworkWindow : Window
{
 private readonly CatalogRepository repository;
 private readonly MediaItem media;
 private readonly string cache;
 private readonly ListBox list = new() { DisplayMemberPath = "Label", MinWidth = 220 };
 private readonly Image preview = new() { Stretch = System.Windows.Media.Stretch.Uniform };
 private readonly TextBlock details = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,12,0,12) };
 private readonly Func<double, Task> seek;
 private CancellationTokenSource? previewLoad;
 private MediaAsset? Selected => list.SelectedItem is MediaAssetSummary summary?repository.GetMediaAsset(summary.Id):null;
 internal int AssetCount => list.Items.Count;
 internal bool HasPreview => preview.Source != null;
 internal Task ReadyTask { get; private set; } = Task.CompletedTask;

 internal MediaArtworkWindow(Window owner, CatalogRepository repository, MediaItem media, string cache, Func<double,Task> seek)
 {
  Owner = owner; this.repository = repository; this.media = media; this.cache = cache; this.seek = seek;
  Title = media.Title + T(" · Media artwork"); Width = 1000; Height = 700; MinWidth = 720; MinHeight = 460; WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
  var root = new DockPanel();
  var actions = new WrapPanel(); AddAsync(actions, T("Add image …"), AddImageAsync); AddAsync(actions, T("Use as thumbnail"), SetThumbnailAsync); Add(actions, T("Export original bytes …"), Export);
  AddAsync(actions, T("Jump to the point in time"), async () => { if (Selected?.PositionSeconds is double seconds) await seek(seconds); }); Add(actions, T("Remove image"), Remove);
  actions.Margin = new Thickness(0, 8, 0, 0); DockPanel.SetDock(actions, Dock.Bottom); root.Children.Add(actions);
  list.SelectionChanged += (_, _) => ReadyTask=ShowSelectedAsync(); DockPanel.SetDock(list, Dock.Left); root.Children.Add(list);
  var content = new DockPanel { Margin = new Thickness(20,0,0,0) }; DockPanel.SetDock(details, Dock.Bottom); content.Children.Add(details); content.Children.Add(preview); root.Children.Add(content);
  var frame = new WindowFrame(T("Media artwork · {0}", media.Title), T("Cover, scene images and thumbnails of this item. The original bytes stay unchanged in the catalog."), root, "", scroll: false);
  frame.Add(T("Close"), Close, primary: true, cancel: true); Content = frame;
  Closed+=(_,_)=>{ previewLoad?.Cancel(); previewLoad?.Dispose(); }; Reload();
 }
 private void AddAsync(Panel panel,string text,Func<Task> action)
 {
  var button=new Button { Content=text }; button.Click+=async (_,_)=>{ try { button.IsEnabled=false; await action(); } catch(Exception error) { Message.Show(this,error.Message,text,MessageBoxButton.OK,MessageBoxImage.Warning); } finally { button.IsEnabled=true; } }; panel.Children.Add(button);
 }
 private void Add(Panel panel, string text, Action action)
 {
  var button = new Button { Content = text }; button.Click += (_, _) => { try { action(); } catch (Exception error) { Message.Show(this,error.Message,text,MessageBoxButton.OK,MessageBoxImage.Warning); } }; panel.Children.Add(button);
 }
 private void Reload(Guid? selected = null)
 {
  var assets = repository.GetMediaAssetSummaries(media.Id); list.ItemsSource = assets;
  list.SelectedItem = assets.FirstOrDefault(a => a.Id == selected) ?? assets.FirstOrDefault();
 }
 private async Task ShowSelectedAsync()
 {
  previewLoad?.Cancel(); previewLoad?.Dispose(); previewLoad=new(); var token=previewLoad.Token;
  preview.Source = null;
  if (Selected is not { } asset) { details.Text = T("No media artwork stored yet."); return; }
  details.Text = T("{0}\n{1} · {2:N0} bytes", asset.Label, asset.Kind, asset.Data.Length) + (asset.PositionSeconds is double seconds ? T(" · {0:0.###} seconds", seconds) : "");
  try
  {
   var bitmap=await DesktopImaging.LoadBitmapAsync(repository.MaterializeMediaAsset(asset, cache),1200,token);
   if(!token.IsCancellationRequested) preview.Source=bitmap;
  }
  catch(OperationCanceledException) { }
  catch (Exception error) when(error is IOException or InvalidDataException or ArgumentException or InvalidOperationException or
   UnauthorizedAccessException) { if(!token.IsCancellationRequested) details.Text += "\n" + error.Message
   + T("\nThe original bytes stay stored and can be exported."); }
 }
 private async Task AddImageAsync()
 {
  var picker = new OpenFileDialog { AddToRecent = Privacy.AddToRecent, Title = T("Add artwork to the item"), Filter = MediaFormats.ImageDialogFilter };
  if (picker.ShowDialog(this) != true) return;
  if (new FileInfo(picker.FileName).Length > CatalogRepository.MaximumAssetBytes) throw new IOException(T("The image file exceeds 32 MiB."));
  await DesktopImaging.Service.GetInfoAsync(picker.FileName);
  var data = await File.ReadAllBytesAsync(picker.FileName);
  var asset = repository.UpsertMediaAsset(new() { MediaId = media.Id, Data = data, Label = Path.GetFileNameWithoutExtension(picker.FileName), Position = list.Items.Count, Kind = MediaAssetKind.Artwork, IsCustom = true });
  Reload(asset.Id);
 }
 private async Task SetThumbnailAsync()
 {
  if (Selected is not { } asset) return;
  var path = await DesktopImaging.Service.GetPreviewPathAsync(repository.MaterializeMediaAsset(asset, cache),390,390);
  var current = repository.GetMedia(media.Id) ?? throw new InvalidOperationException(T("The item is no longer in the catalog."));
  repository.Upsert(current with { ThumbnailPath = path, Extra = new(current.Extra) { ["thumbnail.assetId"] = asset.Id.ToString() } });
  details.Text += T("\nSaved as thumbnail.");
 }
 private void Export()
 {
  if (Selected is not { } asset) return;
  var picker = new SaveFileDialog { AddToRecent = Privacy.AddToRecent, FileName = T("Media artwork") + (CatalogRepository.ImageExtension(asset.Data) ?? ".bin"), Filter = T("All files|*.*") };
  if (picker.ShowDialog(this) != true) return;
  FileAccessPolicy.Current.EnsureWritable(picker.FileName);
  using var output = new FileStream(picker.FileName,FileMode.CreateNew,FileAccess.Write,FileShare.None); output.Write(asset.Data);
 }
 private void Remove()
 {
  if (Selected is not { } asset) return;
  if (Message.Show(this,T("Remove this stored media artwork? The original media file is kept."),T("Remove media artwork"),MessageBoxButton.YesNo,MessageBoxImage.Question,MessageBoxResult.No) != MessageBoxResult.Yes) return;
  repository.DeleteMediaAsset(asset.Id); Reload();
 }
}
