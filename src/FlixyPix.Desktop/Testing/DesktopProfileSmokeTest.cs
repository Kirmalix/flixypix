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
  var reviewFixture = _repository.Upsert(new MediaItem { Path = Path.Combine(directory, "review-ui.mp4"), Title = "Review UI fixture", Kind = MediaKind.Video, ReviewState = ReviewState.NeedsAttention });
  try
  {
   ApplyViewState(new ViewState { View = "inbox" }); await _lastRefresh; await Idle();
   Require(MediaList.Items.Cast<MediaCard>().Any(c => c.Item.Id == reviewFixture.Id), "Review inbox did not show needs-attention medium.");
   var reviewedUndo = _repository.ApplyImmediateEdit([reviewFixture.Id], ImmediateEditFields.ReviewState, m => m with { ReviewState = ReviewState.Reviewed });
   await RefreshAsync(); await Idle();
   Require(!MediaList.Items.Cast<MediaCard>().Any(c => c.Item.Id == reviewFixture.Id), "Reviewed medium remained in default inbox.");
   Require(_repository.UndoImmediateEdit(reviewedUndo), "Review UI change could not be undone.");
   await RefreshAsync(); await Idle();
   Require(MediaList.Items.Cast<MediaCard>().Any(c => c.Item.Id == reviewFixture.Id), "Undone review did not return to inbox.");
   await RefreshAsync(reviewFixture.Id); await Idle();
   Require(_reviewEditor?.SelectedIndex == (int)ReviewState.NeedsAttention, "Inspector did not show review state.");
   _reviewEditor!.SelectedIndex = (int)ReviewState.Reviewed; await _lastRefresh; await Idle();
   Require(_repository.GetMedia(reviewFixture.Id)!.ReviewState == ReviewState.Reviewed, "Inspector status selection did not save.");
   ApplyViewState(new ViewState { View = "inbox", ReviewStates = [ReviewState.Reviewed] }); await _lastRefresh; await Idle();
   Require(MediaList.Items.Cast<MediaCard>().Any(c => c.Item.Id == reviewFixture.Id), "Explicit reviewed filter did not override inbox defaults.");
   var reviewPanel = new System.Windows.Controls.StackPanel(); AddReviewFilters(reviewPanel);
   var reviewBoxes = reviewPanel.Children.OfType<System.Windows.Controls.CheckBox>().ToArray();
   var attentionBox = reviewBoxes.Single(b => Equals(b.Content, T("Needs attention")));
   attentionBox.IsChecked = true; attentionBox.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); await _lastRefresh;
   Require(_reviewStates.Contains(ReviewState.Reviewed) && _reviewStates.Contains(ReviewState.NeedsAttention), "Status checkboxes did not combine choices.");
   var allBox = reviewBoxes.Single(b => Equals(b.Content, T("All")));
   allBox.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); await _lastRefresh;
   Require(_reviewStates.Count == 4, "All review states did not clear inbox restriction.");
   var secondReview = _repository.Upsert(new MediaItem { Path = Path.Combine(directory, "review-ui-second.mp4"), Kind = MediaKind.Video, ReviewState = ReviewState.Unreviewed });
   try
   {
    var menu = Menus.Create(MediaMenu([_repository.GetMedia(reviewFixture.Id)!, secondReview]), (_, error) => throw error);
    var statusMenu = menu.Items.OfType<System.Windows.Controls.MenuItem>().Single(m => Equals(m.Header, T("Review status")));
    var command = statusMenu.Items.OfType<System.Windows.Controls.MenuItem>().Single(m => Equals(m.Header, T("Needs attention")));
    command.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent)); await _lastRefresh;
    Require(_repository.GetMedia(reviewFixture.Id)!.ReviewState == ReviewState.NeedsAttention && _repository.GetMedia(secondReview.Id)!.ReviewState == ReviewState.NeedsAttention, "Actual menu action did not update both media.");
    UndoImmediate(); await _lastRefresh;
    Require(_repository.GetMedia(reviewFixture.Id)!.ReviewState == ReviewState.Reviewed && _repository.GetMedia(secondReview.Id)!.ReviewState == ReviewState.Unreviewed, "Menu batch undo did not restore distinct prior states.");
   }
   finally { _repository.Delete(secondReview.Id); }
   var advancedReview = new AdvancedSearchWindow(this, SearchExpression.Where(new() { Field = SearchField.ReviewState, Comparison = SearchComparison.Equals, Text = "Reviewed" }));
   advancedReview.Show(); await Idle();
   try
   {
    Require(advancedReview.RootGroup.Read()?.Children.Single().Condition is { Field: SearchField.ReviewState, Text: "Reviewed" }, "Advanced search lost review-status selection.");
    Capture((FrameworkElement)advancedReview.Content, Path.Combine(directory, "review-advanced-search.png"));
   }
   finally { advancedReview.Close(); }
   Capture(this, Path.Combine(directory, "review-inbox.png"));
  }
  finally { _repository.Delete(reviewFixture.Id); ApplyViewState(new ViewState { View = "all" }); await _lastRefresh; }
  var parserBefore = _repository.Search().First();
  var parserPreview = new HistoricalParserPreview(Guid.NewGuid(), "synthetic-display-only", [parserBefore.Id],
   [new(parserBefore.Id, "synthetic", parserBefore, parserBefore with { Title = "Parser title", Tags = [..parserBefore.Tags, "Parser addition"] })]);
  var parserReview = new ParserChangesWindow(this, parserPreview, _ => throw new InvalidOperationException("Synthetic stale parser preview")); parserReview.Show(); await Idle();
  try
  {
   Require(parserReview.Selected.Count == 2, "Parser preview did not select proposed title/tag changes.");
   var boxes = Descendants<System.Windows.Controls.CheckBox>((FrameworkElement)parserReview.Content).ToArray();
   boxes[0].IsChecked = false;
   Require(parserReview.Selected.Count == 1, "Parser preview checkbox did not change selection.");
   parserReview.ApplyButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent)); await parserReview.ApplyTask;
   Require(parserReview.IsVisible && parserReview.Selected.Count == 1 && parserReview.FailureText.Contains("Synthetic stale"), "Failed parser apply closed or discarded the selection.");
   Capture((FrameworkElement)parserReview.Content, Path.Combine(directory, "parser-selective-preview.png"));
  }
  finally { parserReview.Close(); }
  await RunParserApplySmokeAsync(directory);
  await RunImageSetProposalsSmokeAsync(directory);
  await RunPublicationPickerSmokeAsync(directory);
  await RunPublicationGroupsSmokeAsync(directory);
  await RunTagHierarchySmokeAsync(directory);
  var setImages = new List<MediaItem>(); ImageSet? smokeSet = null;
  try
  {
   for (var i = 0; i < 2; i++)
   {
    var path = Path.Combine(directory, "set-ui-" + i + ".png"); using var image = new MagickImage(new MagickColor(i == 0 ? "blue" : "orange"), 64, 64); image.Write(path, MagickFormat.Png);
    setImages.Add(_repository.Upsert(new MediaItem { Path = path, Kind = MediaKind.Image, Title = "Set image " + i }));
   }
   smokeSet = _repository.UpsertImageSet(new() { Name = "Synthetic image set", MediaIds = [setImages[1].Id, setImages[0].Id], CoverId = setImages[1].Id });
   ApplyViewState(new ViewState { View = "image-sets", ImageSetId = smokeSet.Id }); await _lastRefresh; await Idle();
   Require(_setPage!.IsVisible && !MediaList.IsVisible && SelectedImageSet?.Id == smokeSet.Id, "Image-set route not shown.");
   Require(_setMembers!.Items.Cast<MediaItem>().Select(i => i.Id).SequenceEqual(smokeSet.MediaIds), "Image-set UI order differs from saved order.");
   _setMembers.SelectedIndex = 1; MoveSetMember(-1);
   Require(SelectedImageSet!.MediaIds.SequenceEqual(setImages.Select(i => i.Id)), "Image-set reorder action failed.");
   Require((_setMembers.SelectedItem as MediaItem)?.Id == setImages[0].Id && !_setActions["Move up"].IsEnabled, "Set member selection or first boundary action was lost.");
   _setActions["Set as cover"].RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
   await _setCoverTask;
   Require(_repository.GetImageSet(smokeSet.Id)!.CoverId == setImages[0].Id && _setCover!.Source != null && _setCoverCaption!.Text.Contains(setImages[0].Title), "Actual cover action did not persist or display preview.");
   Require((_setMembers.SelectedItem as MediaItem)?.Id == setImages[0].Id, "Cover refresh changed member selection.");
   // A context menu can outlive a reorder/cover edit: its old object must not overwrite either.
   AddImagesToSet(smokeSet, [setImages[0]]);
   Require(_repository.GetImageSet(smokeSet.Id)!.MediaIds.SequenceEqual(setImages.Select(i => i.Id)) && _repository.GetImageSet(smokeSet.Id)!.CoverId == setImages[0].Id, "Stale add menu overwrote current set data.");
   _setMembers.SelectedIndex = 1; await OpenSetImageAsync();
   Require(_viewer is ImageViewerWindow { CurrentIndex: 1 }, "Image set viewer did not use set order.");
   _viewer!.Close(); _viewer = null;
   RefreshImageSets(); await _setCoverTask;
   Require((_setMembers.SelectedItem as MediaItem)?.Id == setImages[1].Id && !_setActions["Move down"].IsEnabled, "Set refresh lost last member selection/boundary.");
   Require(CaptureViewState().ImageSetId == smokeSet.Id, "Image-set selection not captured in history.");
   Capture(this, Path.Combine(directory, "image-sets-main.png"));
   var populatedSet = _repository.GetImageSet(smokeSet.Id)!;
   _repository.UpsertImageSet(populatedSet with { MediaIds = [], CoverId = null }); RefreshImageSets(); await _setCoverTask;
   Require(!_setActions["Open"].IsEnabled && !_setActions["Set as cover"].IsEnabled && _setCover!.Source == null, "Empty set retained actions or previous cover.");
   _repository.UpsertImageSet(populatedSet); RefreshImageSets(); await _setCoverTask;
   ApplyViewState(new ViewState { View = "all" }); await _lastRefresh; await Idle();
   Require(MediaList.IsVisible && !_setPage.IsVisible, "Media view did not return after image sets.");
   Require(_setCover!.Source == null, "Leaving sets retained the decoded cover.");
  }
  finally
  {
   if (smokeSet != null) _repository.DeleteImageSet(smokeSet.Id);
   foreach (var image in setImages) _repository.Delete(image.Id);
   ApplyViewState(new ViewState { View = "all" }); await _lastRefresh;
  }
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
  // The same control also works in a host that has no directory-window lifecycle.
  var browser = new DirectoryBrowser(this, _repository, TagRole.Performer, "Jane Doe");
  TagKey? requested = null; browser.ShowMediaRequested += key => requested = key;
  var host = new Window { Owner = this, Width = 1120, Height = 740, Content = browser, ShowInTaskbar = false };
  host.Show(); await Idle();
  try
  {
   await browser.ProfileReady;
   browser.EnterPerformer("1990-03-15", "Guard", "invalid", PerformerGender.Female);
   browser.LeaveDecision = () => MessageBoxResult.Cancel;
   Require(browser.HasUnsavedProfile && !browser.ConfirmLeave(), "Cancelling did not retain dirty profile fields.");
   var directorySearch = Descendants<System.Windows.Controls.TextBox>(browser).Single(b => System.Windows.Automation.AutomationProperties.GetName(b) == T("Search the directory"));
   directorySearch.Text = "no-such-performer";
   Require(directorySearch.Text == "" && browser.Current?.Tag == "Jane Doe" && browser.HasUnsavedProfile,
    "Cancelling a search change did not restore the previous search/profile.");
   Descendants<System.Windows.Controls.ListView>(browser).Single().SelectedItems.Clear();
   Require(browser.Current?.Tag == "Jane Doe" && browser.HasUnsavedProfile,
    "Cancelling selection change lost the previous profile.");

   browser.LeaveDecision = () => MessageBoxResult.Yes;
   Require(!browser.ConfirmLeave() && browser.HasUnsavedProfile, "Invalid profile fields were discarded after failed save.");
   browser.EnterPerformer("1990-03-15", "Guard", "165", PerformerGender.Female);
   Require(browser.ConfirmLeave() && !browser.HasUnsavedProfile && _repository.GetDirectory().Single(e => e.Tag == "Jane Doe").Profile?.HairColor == "Guard",
    "Save-before-leave did not persist the profile.");
   browser.EnterPerformer("1990-03-15", "Discard", "165", PerformerGender.Female);
   browser.LeaveDecision = () => MessageBoxResult.No;
   Require(browser.ConfirmLeave() && !browser.HasUnsavedProfile && _repository.GetDirectory().Single(e => e.Tag == "Jane Doe").Profile?.HairColor == "Guard",
    "Discard-before-leave wrote the modified fields.");

   var showItems = Descendants<System.Windows.Controls.Button>(browser).Single(b => Equals(b.Content, T("Show items")));
   showItems.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
   Require(requested == new TagKey("Darsteller", "Jane Doe") && host.IsVisible,
    "Embedded directory did not emit the categorized navigation request or closed its host.");
   Capture(browser, Path.Combine(directory, "directory-embedded.png"));
  }
  finally { host.Close(); }
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
  ApplyViewState(new ViewState { View = "performers", SearchText = "Jane", Directory = new(new("Darsteller", "Jane Doe"), 0, 120) });
  await _lastRefresh; await Idle(); await _roleBrowser!.ProfileReady;
  Require(_roleBrowser.IsVisible && !MediaList.IsVisible && !DetailsPane.IsVisible && _roleBrowser.Current?.Tag == "Jane Doe", "Main-window performer view not shown.");
  SaveViewNow(); Require(_repository.GetViewState() is { View: "performers", SearchText: "Jane", Directory.Selected.Value: "Jane Doe" }, "Role state was not persisted.");
  Capture(this, Path.Combine(directory, "main-performers.png"));
  _roleBrowser.EnterPerformer("1990-03-15", "Unsaved", "165", PerformerGender.Female);
  _roleBrowser.LeaveDecision = () => MessageBoxResult.Cancel;
  ApplyViewState(new ViewState { View = "studios" });
  Require(_view == "performers" && _roleBrowser.HasUnsavedProfile, "Cancelled role navigation lost the profile.");
  _roleBrowser.LeaveDecision = () => MessageBoxResult.No;
  ApplyViewState(new ViewState { View = "studios" }); await _lastRefresh; await Idle();
  Require(_roleBrowserView == "studios" && _roleBrowser!.IsVisible, "Studio view not shown.");
  await WalkHistoryAsync(-1); await Idle();
  Require(_view == "performers" && SearchBox.Text == "Jane" && _roleBrowser!.Current?.Tag == "Jane Doe", "Back did not restore role search/selection.");
  await WalkHistoryAsync(1); await Idle();
  Require(_view == "studios" && _roleBrowserView == "studios", "Forward did not restore the studio route.");
  await WalkHistoryAsync(-1); await Idle();
  var roleQueryItem = _repository.Upsert(new MediaItem { Path = Path.Combine(directory, "role-category-exact.mp4"), Kind = MediaKind.Video,
   Title = "Role category exact", Tags = ["Jane Doe"], TagCategories = new() { ["Darsteller"] = ["Jane Doe"] } });
  var wrongRoleItem = _repository.Upsert(new MediaItem { Path = Path.Combine(directory, "role-category-other.mp4"), Kind = MediaKind.Video,
   Title = "Role category other", Tags = ["Jane Doe"], TagCategories = new() { ["Other category"] = ["Jane Doe"] } });
  try
  {
   var showRoleMedia = Descendants<System.Windows.Controls.Button>(_roleBrowser!).Single(b => Equals(b.Content, T("Show items")));
   showRoleMedia.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
   await _lastRefresh; await Idle();
   Require(_view == "all" && MediaList.Items.Cast<MediaCard>().Any(c => c.Item.Id == roleQueryItem.Id)
    && !MediaList.Items.Cast<MediaCard>().Any(c => c.Item.Id == wrongRoleItem.Id), "Role Show-items action mixed same-name categories.");
   await WalkHistoryAsync(-1); await Idle();
   Require(_view == "performers" && SearchBox.Text == "Jane" && _roleBrowser!.Current?.Tag == "Jane Doe", "Back from role media did not restore the profile.");
  }
  finally { _repository.Delete(roleQueryItem.Id); _repository.Delete(wrongRoleItem.Id); }
  ApplyViewState(new ViewState { View = "all" }); await _lastRefresh; await Idle();
  Require(MediaList.IsVisible && !_roleBrowser!.IsVisible, "Media grid did not return after role view.");

 }
}
