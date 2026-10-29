using System.IO;
using System.Windows;
using System.Windows.Threading;
using FlixyPix.Core;
using ImageMagick;

namespace FlixyPix.Desktop;

public partial class MainWindow
{
 /// <summary>Profiles: a performer is assigned in the details through an alias, a new studio lands in the studio category,
 /// the directory shows portrait and media of the performer, and a deletion is taken back.</summary>
 private async Task RunProfileSmokeTestAsync(string directory)
 {
  static void Require(bool check, string message) { if (!check) throw new InvalidOperationException(message); }
  async Task Idle() => await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
  await RefreshAsync();
  var medium = _repository.Search().First();
  UpdateRoleEditors([medium]);
  Require(RoleCompletionsForTest(TagRole.Performer, "J. D").Any(c => c.StartsWith("Jane Doe · J. Doe", StringComparison.Ordinal)), "The performer input does not complete aliases.");
  // After each change the details follow the main window's selection again, so the medium is shown before each step.
  await AddRoleNameForTestAsync(TagRole.Performer, "J. Doe");
  UpdateRoleEditors([_repository.GetMedia(medium.Id)!]);
  await AddRoleNameForTestAsync(TagRole.Studio, "Neues Studio");
  var tagged = _repository.GetMedia(medium.Id)!;
  Require(tagged.TagCategories.GetValueOrDefault("Darsteller")?.Contains("Jane Doe") == true && tagged.TagCategories.GetValueOrDefault("Studios")?.Contains("Neues Studio") == true,
   "Performer or studio from the details did not land in their categories.");
  UpdateRoleEditors([tagged]);
  Require(RoleChips(TagRole.Performer).Contains("Jane Doe  ×") && RoleChips(TagRole.Studio).Contains("Neues Studio  ×"), "The details do not show performer and studio.");

  var portraitPath = Path.Combine(directory, "jane-doe.png");
  using (var picture = new MagickImage(new MagickColor("orange"), 120, 120)) picture.Write(portraitPath, MagickFormat.Png);
  var window = new DirectoryWindow(this, _repository, TagRole.Performer, "Jane Doe") { PictureCache = Path.Combine(directory, "directory-pictures"),
   Tools = ToolExists(_settings.Ffmpeg, "ffmpeg") ? _tools : null };
  window.Show(); await Idle();
  try
  {
   window.SetPicture(window.Current!, portraitPath); await Idle(); await window.ProfileReady;
   Require(window.Current?.Tag == "Jane Doe" && window.HasPortrait && window.MediaTileCount >= 1, "The profile does not show portrait and media of the performer.");
   Require(window.CategoryText == "Darsteller", "The editable category field does not show the category.");
   for (var round = 0; round < 30 && !window.RowHasPicture("Jane Doe"); round++) { await Task.Delay(100); await Idle(); }
   Require(window.RowHasPicture("Jane Doe"), "The list row did not load its portrait.");
   // Other sources of the picture: the clipboard (stubbed) and a still of a video; each replaces the shown one.
   var janeKey = new TagKey("Darsteller", "Jane Doe"); var portraitId = _repository.GetTagPictureIds()[janeKey];
   var clipboardBefore = DirectoryWindow.ClipboardImage;
   try
   {
    DirectoryWindow.ClipboardImage = () => null; Require(!window.PastePicture(), "An empty clipboard was taken as a picture.");
    DirectoryWindow.ClipboardImage = () => { var pixels = new System.Windows.Media.Imaging.RenderTargetBitmap(24, 24, 96, 96, System.Windows.Media.PixelFormats.Pbgra32); pixels.Freeze(); return pixels; };
    Require(window.PastePicture() && _repository.GetTagPictureIds()[janeKey] != portraitId && _repository.GetTagAsset(portraitId)!.Tag == null,
     "The clipboard picture did not replace the portrait.");
   }
   finally { DirectoryWindow.ClipboardImage = clipboardBefore; }
   if (window.Tools != null)
   {
    // The synthetic video of the main test is gone by now; a one-second clip of its own is enough for a still.
    var video = new MediaItem { Path = Path.Combine(directory, "jane-doe-clip.mp4"), Title = "Jane Doe Clip", Kind = MediaKind.Video, DurationSeconds = 1 };
    var start = new System.Diagnostics.ProcessStartInfo(_settings.Ffmpeg ?? Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg.exe"))
    { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
    foreach (var argument in new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-n", "-f", "lavfi", "-i", "testsrc=size=160x90:rate=10", "-t", "1", "-c:v", "libx264",
     "-pix_fmt", "yuv420p", video.Path }) start.ArgumentList.Add(argument);
    using (var encoder = System.Diagnostics.Process.Start(start)!)
    {
     var error = encoder.StandardError.ReadToEndAsync(); var output = encoder.StandardOutput.ReadToEndAsync();
     await encoder.WaitForExitAsync(); Require(encoder.ExitCode == 0, await error); await output;
    }
    var beforeStill = _repository.GetTagPictureIds()[janeKey];
    await window.SetStillAsync(window.Current!, video, .3);
    Require(_repository.GetTagPictureIds()[janeKey] != beforeStill && _repository.GetTagAsset(_repository.GetTagPictureIds()[janeKey])!.Provenance["import.method"] == "video-still",
     "A still of the video did not become the portrait.");
    for (var round = 0; round < 30 && !(window.HasPortrait && window.RowHasPicture("Jane Doe")); round++) { await window.ProfileReady; await Task.Delay(100); await Idle(); }
    Require(window.HasPortrait && window.RowHasPicture("Jane Doe"), "The still was stored but not shown.");
    Capture((FrameworkElement)window.Content, Path.Combine(directory, "directory-still.png"));
   }
   // From the image viewer: an image becomes the picture of a tag and the thumbnail of another medium.
   if (_repository.Search().FirstOrDefault(i => i.Kind == MediaKind.Image && File.Exists(i.Path)) is { } image)
   {
    ApplyTagPicture(image, window.Current!);
    Require(_repository.GetTagAsset(_repository.GetTagPictureIds()[janeKey])!.SourcePaths.Contains(image.Path), "The viewer image did not become the tag picture.");
    var target = _repository.Search().First(i => i.Id != image.Id);
    await ApplyThumbnailFrom(image, target);
    Require(_repository.GetMedia(target.Id)!.Extra.ContainsKey("thumbnail.assetId") && File.Exists(_repository.GetMedia(target.Id)!.ThumbnailPath), "The viewer image did not become the thumbnail.");
   }
   Capture((FrameworkElement)window.Content, Path.Combine(directory, "directory-profile.png"));
   // Properties of the performer: entered and saved in the profile, then found through the filter bar.
   Require(window.PerformerFieldsVisible, "The profile of a performer shows no properties.");
   window.EnterPerformer("1990-03-15", "Blond", "165", PerformerGender.Female); await Idle();
   Require(window.AgeText.Length > 0, "The profile does not show the age from the date of birth.");
   window.SaveForTest(); await Idle(); await window.ProfileReady;
   var properties = _repository.GetDirectory().Single(e => e.Tag == "Jane Doe" && e.Category == "Darsteller").Profile;
   Require(properties is { HairColor: "Blond", HeightCm: 165, Gender: PerformerGender.Female } && properties.BirthDate == new DateOnly(1990, 3, 15),
    "The properties of the performer were not saved.");
   Capture((FrameworkElement)window.Content, Path.Combine(directory, "directory-properties.png"));
   ApplyFilter(new MediaFilter { Profiles = [CatalogRepository.HasProfile(ProfileProperty.HairColor, SearchComparison.Equals, "blond").Condition!] });
   await _lastRefresh; await Idle();
   var found = MediaList.Items.Cast<MediaCard>().Select(c => c.Item.Id).ToList();
   Require(found.SequenceEqual([medium.Id]) && FilterPills[T("Properties")].Text.Text.EndsWith(": 1", StringComparison.Ordinal),
    "The filter by a property of performers did not find exactly the medium of the performer.");
   Capture(this, Path.Combine(directory, "filter-properties.png"));
   ApplyFilter(new MediaFilter()); await _lastRefresh;
   window.DeleteWithoutAsking(["Neues Studio"]);
   Require(window.CanUndo && _repository.GetDirectory().All(e => e.Tag != "Neues Studio"), "Deleting did not offer an undo.");
   await Idle(); Capture((FrameworkElement)window.Content, Path.Combine(directory, "directory-undo.png"));
   window.Undo();
   Require(!window.CanUndo && _repository.GetMedia(medium.Id)!.TagCategories.GetValueOrDefault("Studios")?.Contains("Neues Studio") == true, "The deletion was not taken back.");
  }
  finally
  {
   window.Close();
   _repository.UpdateCategoryTags([medium.Id], "Darsteller", [], ["Jane Doe"]);
   _repository.UpdateCategoryTags([medium.Id], "Studios", [], ["Neues Studio"]);
  }
 }
}
