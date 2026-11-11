using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FlixyPix.Core;
using FlixyPix.Imaging;

namespace FlixyPix.Desktop;

public partial class MainWindow
{
 /// <summary>Camera RAW files on small DNG files written for the test: a picture and its RAW file arrive as one item that
 /// shows the picture, a RAW file alone is shown by the camera's preview and developed on request, one without a preview
 /// is developed at once; tile, details and viewer say so; the RAW file leaves its picture and joins it again; the folder
 /// check offers a late RAW file to its picture.</summary>
 private async Task RunRawSmokeTestAsync(string directory)
 {
  static void Require(bool check, string message) { if (!check) throw new InvalidOperationException(message); }
  async Task Settled() { await _lastRefresh; await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); }
  async Task Prepared() { for (var wait = 0; wait < 400 && (MediaPreparation.Running || _operation != null); wait++) await Task.Delay(50); await Settled(); }
  static void Picture(string path, Color color)
  {
   var visual = new DrawingVisual();
   using (var drawing = visual.RenderOpen()) drawing.DrawRectangle(new SolidColorBrush(color), null, new Rect(0, 0, 1200, 800));
   var bitmap = new RenderTargetBitmap(1200, 800, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
   var encoder = new JpegBitmapEncoder { QualityLevel = 90 }; encoder.Frames.Add(BitmapFrame.Create(bitmap));
   using var stream = File.Create(path); encoder.Save(stream);
  }
  async Task<ImageViewer> Open(MediaItem item)
  {
   ShowImage(item); await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
   var opened = ImageView ?? throw new InvalidOperationException("The image viewer did not open.");
   await opened.ReadyTask; Require(opened.HasImage, "The viewer did not show " + Path.GetFileName(item.Path) + ".");
   return opened;
  }

  var folder = Path.Combine(directory, "raw"); Directory.CreateDirectory(folder);
  string pairPicture = Path.Combine(folder, "IMG_0042.JPG"), pairRaw = Path.Combine(folder, "IMG_0042.dng"), alone = Path.Combine(folder, "IMG_0043.dng"), bare = Path.Combine(folder, "IMG_0044.dng");
  Picture(pairPicture, Color.FromRgb(196, 120, 60));
  SyntheticDng.Write(pairRaw, previewWidth: 1600); SyntheticDng.Write(alone, previewWidth: 1600); SyntheticDng.Write(bare, previewWidth: 0);
  var known = _repository.Search().Select(item => item.Id).ToHashSet();
  var checkedFolder = Path.Combine(directory, "raw-folder"); Directory.CreateDirectory(checkedFolder);
  var badgesBefore = _cardContext.Display;
  try
  {
   await ImportPaths([folder]); await Prepared();
   var added = _repository.Search().Where(item => !known.Contains(item.Id)).ToList();
   Require(added.Count == 3 && added.All(item => item.Kind == MediaKind.Image), $"Four files of three shots became {added.Count} items.");
   var pair = _repository.GetMediaByPath(pairPicture) ?? throw new InvalidOperationException("The picture of the pair is no item.");
   Require(pair.Files.Count == 1 && string.Equals(pair.Files[0].Path, pairRaw, StringComparison.OrdinalIgnoreCase) && pair.Width == 1200, "The RAW file did not join its picture.");
   Require(pair.Files[0].Width == 96 && pair.Files[0].Height == 64, $"The RAW file of the pair was not measured ({pair.Files[0].Width} × {pair.Files[0].Height}).");
   var single = _repository.GetMediaByPath(alone) ?? throw new InvalidOperationException("The RAW file alone is no item.");
   Require(single.Width == 96 && single.Height == 64 && single.Extra.GetValueOrDefault(MediaCard.CameraKey) == "FlixyPix Test Camera", $"Size or camera of the RAW file were not read ({single.Width} × {single.Height}, {single.Extra.GetValueOrDefault(MediaCard.CameraKey)}).");
   var plain = _repository.GetMediaByPath(bare) ?? throw new InvalidOperationException("The RAW file without a preview is no item.");

   // Tiles: one badge says RAW, and a pair does not count its two files besides.
   _cardContext.Display = badgesBefore with { TileBadges = [TileBadge.Versions, TileBadge.Raw] };
   var pairCard = new MediaCard(pair, false, context: _cardContext); var singleCard = new MediaCard(single, false, context: _cardContext);
   Require(pairCard.Badges.SequenceEqual(["RAW+JPG"]) && singleCard.Badges.SequenceEqual(["RAW"]), "The RAW badges are wrong: " + string.Join(", ", pairCard.Badges) + " / " + string.Join(", ", singleCard.Badges));
   Require(pairCard.HoverDetails.Contains(T("RAW file: {0}", "IMG_0042.dng"), StringComparison.Ordinal) && singleCard.HoverDetails.Contains(T("Camera") + ": FlixyPix Test Camera", StringComparison.Ordinal),
    "The details under the pointer lack RAW file or camera.");
   _cardContext.Display = badgesBefore with { TileBadges = [TileBadge.Versions] };
   Require(new MediaCard(pair, false, context: _cardContext).Badges.SequenceEqual([Tn(2, "1 file", "{0} files")]), "Without the RAW badge the pair does not count its files.");
   _cardContext.Display = badgesBefore;

   // The fast way of the viewer and of the tiles: the camera's preview, white in its corner; none without a preview.
   var fast = await FastImageDecoder.DecodeAsync(alone, 0, CancellationToken.None) ?? throw new InvalidOperationException("The preview of the RAW file was not decoded.");
   var corner = new byte[4]; new FormatConvertedBitmap(fast.Bitmap, PixelFormats.Bgr32, null, 0).CopyPixels(new Int32Rect(10, 10, 1, 1), corner, 4, 0);
   Require(fast.Info is { IsRaw: true, PreviewWidth: 1600, Width: 96 } && fast.Bitmap.PixelWidth == 1600 && corner[0] > 230 && corner[1] > 230 && corner[2] > 230, "The fast way did not show the camera's preview.");
   Require(await FastImageDecoder.DecodeAsync(bare, 0, CancellationToken.None) == null, "A RAW file without a preview went the fast way.");

   // Details: both files of the shot, the picture as the one shown.
   await RefreshAsync(pair.Id); await Settled();
   Require(VersionsSection.Visibility == Visibility.Visible && VersionRows.Children.Count == 2, "The details do not list picture and RAW file.");
   VersionsSection.BringIntoView(); await Settled();
   Capture(RootContent, Path.Combine(directory, "raw-details.png"));

   // Viewer: the pair shows its picture and develops the RAW file on request.
   var viewer = await Open(pair);
   Require(viewer.RawSwitchShown && viewer.RawSwitchText == T("JPEG + RAW") && viewer.ShownPath == pairPicture && !viewer.Developed, "The pair does not show its picture with the RAW switch: " + viewer.RawSwitchText);
   viewer.SetInfo(true);
   Require(viewer.InfoText.Contains(T("RAW file: {0}", "IMG_0042.dng"), StringComparison.Ordinal), "The details over the picture do not name the RAW file.");
   Capture(viewer, Path.Combine(directory, "raw-viewer-pair.png"));
   viewer.ToggleDevelop(); await viewer.ReadyTask;
   Require(viewer.Developed && viewer.RawSwitchText == T("RAW · developed") && viewer.InfoText.Contains(T("RAW, developed from the sensor data"), StringComparison.Ordinal)
    && viewer.InfoText.Contains("96 × 64", StringComparison.Ordinal) && viewer.InfoText.Contains(pair.Files[0].Path, StringComparison.Ordinal), "Developing the RAW file of the pair failed: " + viewer.RawSwitchText);
   Capture(viewer, Path.Combine(directory, "raw-viewer-developed.png"));
   viewer.ToggleDevelop(); await viewer.ReadyTask;
   Require(!viewer.Developed && viewer.RawSwitchText == T("JPEG + RAW"), "The way back to the finished picture failed.");

   // A RAW file alone: the preview is the picture; D develops.
   viewer = await Open(single); viewer.SetInfo(true);
   Require(viewer.RawSwitchShown && viewer.RawSwitchText == "RAW" && !viewer.Developed && viewer.ShownPath == alone && viewer.InfoText.Contains("1600", StringComparison.Ordinal), "The RAW file is not shown by its preview: " + viewer.RawSwitchText + " · " + viewer.InfoText);
   Capture(viewer, Path.Combine(directory, "raw-viewer-preview.png"));
   for (var wait = 0; viewer.CaptureLine.Length == 0 && wait < 40; wait++) await Task.Delay(50);
   Require(viewer.CaptureLine.Contains("FlixyPix Test Camera", StringComparison.Ordinal) && viewer.CaptureLine.Contains("ISO 200", StringComparison.Ordinal), "The viewer does not tell camera and exposure of the RAW file: " + viewer.CaptureLine);
   viewer.ToggleDevelop(); await viewer.ReadyTask;
   Require(viewer.Developed && viewer.HasImage, "Developing the RAW file failed.");
   // The RAW switch lasts. On: a RAW file never developed is developed by itself when it is looked at.
   Require(viewer.DevelopRaw && _repository.GetPreferences().General.DevelopRaw, "The RAW switch was not kept.");
   var large = Path.Combine(folder, "IMG_0045.dng"); SyntheticDng.Write(large, width: 2400, height: 1600, previewWidth: 1200);
   var late = Path.Combine(folder, "IMG_0046.dng"); SyntheticDng.Write(late, previewWidth: 1600);
   await Task.Run(() => new MediaScanner(_repository).ScanAsync([large, late], null, CancellationToken.None)); await AfterImportAsync(CancellationToken.None); await Prepared();
   var lateItem = _repository.GetMediaByPath(late) ?? throw new InvalidOperationException("The late RAW file is no item.");
   viewer = await Open(lateItem);
   Require(viewer.DevelopRaw && viewer.Developed && viewer.RawSwitchText == T("RAW · developed"), "With the RAW switch on the RAW file was not developed by itself: " + viewer.RawSwitchText);
   // Off: the camera's picture again, and nothing is developed by itself any more.
   viewer.ToggleDevelop(); await viewer.ReadyTask;
   Require(!viewer.Developed && !viewer.DevelopRaw && !_repository.GetPreferences().General.DevelopRaw, "Switching off did not bring the camera's picture back or was not kept.");
   // A preview smaller than the sensor image: fitted it is the picture; zooming past its pixels develops the sensor
   // data and keeps the size on screen; the original size is that of the sensor image; going back by hand holds.
   viewer = await Open(_repository.GetMediaByPath(large) ?? throw new InvalidOperationException("The large RAW file is no item."));
   Require(!viewer.Developed && viewer.ShownSize == (1200, 800), $"The fitted RAW file is not shown by its preview ({viewer.ShownSize}).");
   for (var step = 0; step < 12 && !viewer.Developed; step++) viewer.ChangeZoom(1.25);
   var onScreen = viewer.ZoomFactor * 1200; await viewer.ReadyTask;
   Require(viewer.Developed && viewer.ShownSize == (2400, 1600) && Math.Abs(viewer.ZoomFactor * 2400 - onScreen) < 2, $"Zooming past the preview did not develop the sensor image at the same size ({viewer.ShownSize}, {viewer.ZoomFactor:0.###}).");
   viewer.ToggleDevelop(); await viewer.ReadyTask;
   Require(!viewer.Developed && viewer.ShownSize == (1200, 800) && Math.Abs(viewer.ZoomFactor * 1200 - onScreen) < 2, "D did not bring the camera's picture back at the same size.");
   viewer.ChangeZoom(1.25); await viewer.ReadyTask;
   Require(!viewer.Developed, "Zooming developed again although the camera's picture was chosen by hand.");
   viewer.Fit(); viewer.Navigate(0); await viewer.ReadyTask;
   viewer.OriginalSize(); await viewer.ReadyTask;
   Require(viewer.Developed && viewer.ShownSize == (2400, 1600) && viewer.ZoomFactor == 1, $"The original size is not the sensor image ({viewer.ShownSize}, {viewer.ZoomFactor:0.###}).");
   // Without a preview there is nothing to switch.
   // With the switch off a RAW file developed before still shows its development, at once.
   viewer = await Open(lateItem);
   Require(!viewer.DevelopRaw && viewer.Developed && viewer.RawSwitchText == T("RAW · developed"), "A RAW file developed before does not show its development with the switch off: " + viewer.RawSwitchText);
   viewer = await Open(plain);
   Require(viewer.RawSwitchShown && viewer.Developed && viewer.RawSwitchText == T("RAW · developed"), "A RAW file without a preview was not developed: " + viewer.RawSwitchText);
   viewer.ToggleDevelop(); await viewer.ReadyTask;
   Require(viewer.Developed, "A RAW file without a preview offered a picture it does not have.");

   // The picture is gone: the item shows its RAW file.
   var away = pairPicture + ".away"; File.Move(pairPicture, away);
   try
   {
    viewer = await Open(pair);
    // The RAW file of the pair was developed above, so that development is what shows.
    Require(viewer.ShownPath == pairRaw && viewer.Developed, "Without its picture the pair does not show the RAW file: " + viewer.ShownPath + " · " + viewer.RawSwitchText);
   }
   finally { File.Move(away, pairPicture); }
   viewer.Close();

   // The RAW file leaves its picture and joins it again.
   var split = await SplitVersionAsync(pair, pair.Files[0]) ?? throw new InvalidOperationException("Splitting off the RAW file failed.");
   await Prepared();
   pair = _repository.GetMedia(pair.Id)!; split = _repository.GetMedia(split.Id)!;
   Require(pair.Files.Count == 0 && split.Path == pairRaw && split.Width == 96, "The RAW file is no item of its own after splitting off.");
   Require(RawPairPicture([split, pair])?.Id == pair.Id && RawPairPicture([split, single]) == null, "Picture and RAW file are not recognized as a pair to combine.");
   Require(Menus.Labels(Menus.Create(MediaMenu([pair, split]))).Any(label => label.EndsWith(T("Combine picture and RAW file"), StringComparison.Ordinal)), "The menu does not offer to combine picture and RAW file.");
   var joined = CombineAsVersions(pair, [pair, split]) ?? throw new InvalidOperationException("Combining picture and RAW file failed.");
   Require(joined.Id == pair.Id && joined.Files.Count == 1 && _repository.GetMedia(split.Id) == null, "Picture and RAW file did not become one item again.");

   // Folder check: a RAW file that arrives later joins its picture; a new pair arrives as one item.
   string firstPicture = Path.Combine(checkedFolder, "DSC_0001.jpg"), firstRaw = Path.Combine(checkedFolder, "DSC_0001.dng"), secondPicture = Path.Combine(checkedFolder, "DSC_0002.jpg"), secondRaw = Path.Combine(checkedFolder, "DSC_0002.dng");
   Picture(firstPicture, Color.FromRgb(70, 130, 90));
   _repository.SaveLibraryFolders([new LibraryFolder { Path = checkedFolder, Watch = false }]);
   var plan = await BuildFolderPlanAsync(CancellationToken.None);
   Require((await ApplyFolderSyncAsync(plan, plan.Actionable, false)).Imported == 1, "The picture was not taken over from the folder.");
   await Prepared();
   SyntheticDng.Write(firstRaw, previewWidth: 1600); Picture(secondPicture, Color.FromRgb(90, 70, 130)); SyntheticDng.Write(secondRaw, previewWidth: 1600);
   plan = await BuildFolderPlanAsync(CancellationToken.None);
   Require(plan.Changes.Count == 3 && plan.Changes.Single(change => change.Path == firstRaw).PairWith == firstPicture && plan.Changes.Single(change => change.Path == secondRaw).PairWith == secondPicture
    && plan.Changes.Single(change => change.Path == secondPicture).PairWith == null, "The folder check did not find the pictures of the new RAW files.");
   var window = new LibraryFoldersWindow(this, _repository, plan, RestartFolderWatcher, ApplyFolderSyncAsync, _libraryTasks); window.Show();
   try
   {
    await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    Require(window.Rows.Single(row => row.Name == "DSC_0001.dng").Hint == T("Joins “{0}” as its RAW file instead of becoming an entry of its own.", "DSC_0001.jpg"), "The folder check does not say that the RAW file joins its picture.");
    Capture((FrameworkElement)window.Content, Path.Combine(directory, "raw-folder-check.png"));
   }
   finally { window.Close(); }
   var result = await ApplyFolderSyncAsync(plan, plan.Actionable, false); await Prepared();
   Require(result.Imported == 1 && result.Paired == 2, $"Taking over imported {result.Imported} and paired {result.Paired}.");
   Require(_repository.GetMediaByPath(firstPicture) is { Files.Count: 1 } && _repository.GetMediaByPath(secondPicture) is { Files.Count: 1 }, "The RAW files from the folder check did not join their pictures.");
   Require(LibraryFoldersWindow.Result(result).Contains(Tn(2, "1 RAW file joined its picture", "{0:N0} RAW files joined their pictures"), StringComparison.Ordinal), "The result does not count the joined RAW files.");

   // The grid with the badges, for the eye.
   await RefreshAsync(pair.Id); await Settled();
   Capture(RootContent, Path.Combine(directory, "raw-grid.png"));

   // Files of real cameras, where a folder of them is named: each becomes an image with its size and is shown.
   if (Environment.GetEnvironmentVariable("FLIXYPIX_TEST_RAW_SAMPLES") is { Length: > 0 } samples && Directory.Exists(samples))
   {
    // Not through ImportPaths: a file the import warns about would open its report, which nobody closes here.
    await Task.Run(() => new MediaScanner(_repository).ScanAsync([samples], null, CancellationToken.None)); await AfterImportAsync(CancellationToken.None); await Prepared();
    var report = new List<string>(); var failed = 0;
    foreach (var path in Directory.GetFiles(samples).Where(MediaFormats.IsRaw).Order(StringComparer.OrdinalIgnoreCase))
    {
     var line = Path.GetFileName(path) + ": ";
     try
     {
      var item = _repository.GetMediaByPath(path) ?? throw new InvalidOperationException("not imported");
      var info = await DesktopImaging.Service.GetInfoAsync(path);
      if (item.Width != info.Width || item.Height != info.Height || info.Width < 1000) throw new InvalidOperationException($"size {item.Width} × {item.Height}, read {info.Width} × {info.Height}");
      var watch = System.Diagnostics.Stopwatch.StartNew();
      var shown = await FastImageDecoder.DecodeAsync(path, 1600, CancellationToken.None); var way = "preview";
      if (shown == null) { way = "developed"; var decoded = await DesktopImaging.Service.DecodeAsync(path, 0, 1600, 1600); if (decoded.Width < 1000 && decoded.Height < 1000) throw new InvalidOperationException("developed too small"); }
      else if (Math.Max(shown.Bitmap.PixelWidth, shown.Bitmap.PixelHeight) < 1000) throw new InvalidOperationException("preview too small");
      line += $"{info.Width} × {info.Height}, {item.Extra.GetValueOrDefault(MediaCard.CameraKey) ?? "no camera"}, preview {info.PreviewWidth} × {info.PreviewHeight}, shown by {way} in {watch.ElapsedMilliseconds} ms";
      watch.Restart();
      try { await DesktopImaging.Service.DecodeAsync(path, 0, 1600, 1600, develop: true); line += $", developed on request in {watch.ElapsedMilliseconds} ms"; }
      catch (Exception error) when (error is InvalidDataException or IOException) { line += ", developing on request fails: " + error.Message; }
     }
     catch (Exception error) when (error is InvalidOperationException or InvalidDataException or IOException) { failed++; line += "FAILED: " + error.Message; }
     report.Add(line);
    }
    File.WriteAllLines(Path.Combine(directory, "raw-samples-result.txt"), report);
    Require(report.Count > 0 && failed == 0, $"{failed} of {report.Count} camera files were not imported or shown; see raw-samples-result.txt.");
    ApplyFilter(new MediaFilter()); await RefreshAsync(); await Settled();
    Capture(RootContent, Path.Combine(directory, "raw-samples-grid.png"));
    if (_repository.GetMediaByPath(Directory.GetFiles(samples).Where(MediaFormats.IsRaw).Order(StringComparer.OrdinalIgnoreCase).First()) is { } sample)
    {
     viewer = await Open(sample); viewer.SetInfo(true);
     Capture(viewer, Path.Combine(directory, "raw-samples-viewer.png"));
     viewer.Close();
    }
   }
  }
  finally
  {
   _cardContext.Display = badgesBefore;
   CloseImageViewers();
   if (_repository.GetPreferences().General.DevelopRaw) _repository.SaveGeneralPreferences(_repository.GetPreferences().General with { DevelopRaw = false });
   _repository.SaveLibraryFolders([]);
   foreach (var item in _repository.Search().Where(item => !known.Contains(item.Id))) _repository.Delete(item.Id);
   await Prepared(); await RefreshAsync();
  }
 }
}
