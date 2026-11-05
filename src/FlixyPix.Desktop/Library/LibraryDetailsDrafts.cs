using System.Windows;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

public partial class MainWindow
{
 private sealed record DetailsDraft(string Title, string Category, string Description, string ReleaseDate)
 { public MetadataValues? Original { get; init; } }
 private readonly System.Windows.Threading.DispatcherTimer _draftTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
 internal Action<MetadataRecoveryWindow>? RecoveryOpened { get; set; }
 private Action? _showStartupRecovery;
 private void InitializeDetailsDrafts(IReadOnlyList<MetadataDraft>? drafts = null)
 {
  foreach (var draft in drafts ?? _repository.GetMetadataDrafts())
   _detailsDrafts[draft.MediaId] = new(draft.Title, draft.Category, draft.Description, draft.ReleaseDate) { Original = draft.Original };
  _showStartupRecovery = () =>
  {
   if (_detailsDrafts.Count == 0) return;
   var recovery = new MetadataRecoveryWindow(this, _detailsDrafts.Select(pair =>
    new MetadataRecoveryRow(pair.Key, pair.Value.Title, _repository.GetMedia(pair.Key) != null)).ToArray());
   recovery.ContentRendered += (_, _) => RecoveryOpened?.Invoke(recovery);
   if (recovery.ShowDialog() == true)
   {
    try { ApplyDetailsRecovery(recovery.SelectedIds); }
    catch (Exception error) when (error is System.IO.IOException or Microsoft.Data.Sqlite.SqliteException)
    { Status.Text = error.Message; }
   }
  };
  _draftTimer.Tick += (_, _) =>
  {
   _draftTimer.Stop();
   try { RememberDetailsDraft(); }
   catch (Exception error) when (error is System.IO.IOException or Microsoft.Data.Sqlite.SqliteException) { Status.Text = error.Message; }
  };
  foreach (var box in new[] { EditTitle, EditCategory, EditDescription, EditReleaseDate })
   box.TextChanged += (_, _) => { if (!_showingDetails) { _draftTimer.Stop(); _draftTimer.Start(); UpdateDetailsDraftState(); } };
 }
 private bool HasCurrentDetailsChanges() => SelectedMedia.Count == 1 && _detailsShown is { } item &&
  (EditTitle.Text != item.Title || EditCategory.Text != (item.Category ?? "") || EditDescription.Text != (item.Description ?? "") || EditReleaseDate.Text != (ReleaseText(item.ReleaseDate) ?? ""));
 private void UpdateDetailsDraftState()
 {
  var changed = HasCurrentDetailsChanges();
  DetailsEditActions.Visibility = SelectedMedia.Count == 1 && _detailsShown != null ? Visibility.Visible : Visibility.Collapsed;
  DetailsSave.IsEnabled = DetailsDiscard.IsEnabled = changed;
  var conflict = _detailsShown is { } item && _metadataConflicts.Contains(item.Id);
  DetailsReload.Visibility = conflict ? Visibility.Visible : Visibility.Collapsed;
  DetailsDraftState.Text = conflict ? T("Conflict") + " · " + T("Unsaved changes") : changed ? T("Unsaved changes") : "";
 }
 private readonly HashSet<Guid> _metadataConflicts = [];
 private void ShowMetadataConflict(MetadataConflictException error)
 {
  _metadataConflicts.Add(error.MediaId);
  Status.Text = T("Conflict") + " · " + (error.Missing ? T("Missing") : T("Unsaved changes"));
  UpdateDetailsDraftState();
 }
 private void ReloadDetails(object sender, RoutedEventArgs e)
 {
  if (Message.Show(this, T("Reloading discards this draft and shows the saved values."), T("Reload saved values"),
   MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) == MessageBoxResult.OK)
   DiscardDetails(sender, e);
 }
 private void ApplyDetailsRecovery(IReadOnlyCollection<Guid> restoreIds)
 {
  var retained = _detailsDrafts.Where(pair => restoreIds.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value);
  // Persist the decision before changing memory; a disk error must leave all recovery data available.
  _repository.SaveMetadataDrafts(retained.Select(pair => new MetadataDraft(pair.Key, pair.Value.Title, pair.Value.Category,
   pair.Value.Description, pair.Value.ReleaseDate, pair.Value.Original!)).ToArray());
  _draftTimer.Stop(); _detailsDrafts.Clear();
  foreach (var pair in retained) _detailsDrafts.Add(pair.Key, pair.Value);
  _metadataConflicts.Clear(); _detailsShown = null;
  ShowDetails();
 }
 internal void CheckpointDetailsDrafts()
 {
  _draftTimer.Stop(); RememberDetailsDraft(); PersistDetailsDrafts();
 }
 private void DiscardDetails(object sender, RoutedEventArgs e)
 {
  if (Selected is not { } selected || SelectedMedia.Count != 1) return;
  _draftTimer.Stop();
  try { _repository.ApplyMetadataPatches([], clearDraftIds: [selected.Id]); }
  catch (Exception error) when (error is System.IO.IOException or Microsoft.Data.Sqlite.SqliteException)
  { Status.Text = error.Message; return; }
  _detailsDrafts.Remove(selected.Id);
  _metadataConflicts.Remove(selected.Id);
  // Clear the shown snapshot before refresh so it cannot capture the discarded fields again.
  _detailsShown = null;
  Refresh(selected.Id);
 }
 private void PersistDetailsDrafts() => _repository.SaveMetadataDrafts(_detailsDrafts.Select(pair => new MetadataDraft(pair.Key, pair.Value.Title, pair.Value.Category, pair.Value.Description, pair.Value.ReleaseDate, pair.Value.Original!)).ToArray());
 private readonly Dictionary<Guid, DetailsDraft> _detailsDrafts = [];
 private void RememberDetailsDraft()
 {
  if (_detailsShown is not { } item) return;
  var draft = new DetailsDraft(EditTitle.Text, EditCategory.Text, EditDescription.Text, EditReleaseDate.Text);
  var original = new DetailsDraft(item.Title, item.Category ?? "", item.Description ?? "", ReleaseText(item.ReleaseDate) ?? "");
  var before = _detailsDrafts.GetValueOrDefault(item.Id);
  if (draft == original) _detailsDrafts.Remove(item.Id);
  else _detailsDrafts[item.Id] = draft with { Original = _detailsDrafts.GetValueOrDefault(item.Id)?.Original ?? MetadataValues.From(item) };
  if (before != _detailsDrafts.GetValueOrDefault(item.Id))
  {
   try { PersistDetailsDrafts(); }
   catch (Exception error) when (error is System.IO.IOException or Microsoft.Data.Sqlite.SqliteException) { Status.Text = error.Message; }
  }
 }
 private void ApplyDetailsDraft(DetailsDraft draft)
 {
  EditTitle.Text = draft.Title; EditCategory.Text = draft.Category;
  EditDescription.Text = draft.Description; EditReleaseDate.Text = draft.ReleaseDate;
 }
 private bool ResolveDetailsDrafts(MessageBoxResult? decision = null)
 {
  RememberDetailsDraft();

  if (_detailsDrafts.Count == 0) return true;
  var answer = decision ?? Message.Show(this, T("Save changes"), T("Unsaved changes"), MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
  if (answer == MessageBoxResult.Cancel) return false;
  if (answer == MessageBoxResult.No) { _repository.SaveMetadataDrafts([]); _detailsDrafts.Clear(); return true; }
  // Validate every draft before saving any of them. Immediate tag/rating changes stay current.
  var changes = new List<MetadataPatch>();
  foreach (var (id, draft) in _detailsDrafts)
  {
   if (!TryDate(draft.ReleaseDate, out var released))
   {
    Status.Text = T("The release date cannot be read; please enter it like {0}.", DateTime.Today.ToString("d", System.Globalization.CultureInfo.CurrentCulture));
    return false;
   }
   if (_repository.GetMedia(id) is not { } current) { Status.Text = T("Unsaved changes"); return false; }
   changes.Add(new(id, draft.Original ?? MetadataValues.From(current),
    new(draft.Title.Trim(), string.IsNullOrWhiteSpace(draft.Category) ? null : draft.Category.Trim(), draft.Description, released)));
  }
  try { _repository.ApplyMetadataPatches(changes, clearDrafts: true); }
  catch (MetadataConflictException error) { ShowMetadataConflict(error); return false; }
  catch (Exception error) when (error is System.IO.IOException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
  { Status.Text = error.Message; return false; }
  _detailsDrafts.Clear(); return true;
 }
}
