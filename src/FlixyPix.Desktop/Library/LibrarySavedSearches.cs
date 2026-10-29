using System.Globalization;
using System.Windows;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

/// <summary>Smart collections: saved searches with their symbol, hit count and media new since they were last opened.
/// They stand in the collection tree (UX plan U7).</summary>
public partial class MainWindow
{
 private CancellationTokenSource? _searchCounts;

 internal static string SmartGlyph(SavedSearch search) => search.Glyph ?? CatalogRepository.StateOf(search) switch
 {
  { View: "Video" } => "", { View: "Image" } => "", { View: "Audio" } => "", { View: "favorites" } => "", { View: "watchlater" } => "",
  var state => state.Filter.Include.Any(f => f.Role == TagRole.Performer) ? "" : "",
 };
 private static readonly (string Glyph, string Label)[] SmartGlyphs =
  [("", T("Magnifier")), ("", T("Star")), ("", T("Heart")), ("", T("Person")), ("", "Video"), ("", "Bild"), ("", "Tag"), ("", T("Calendar")), ("", T("Flag"))];

 /// <summary>Counts the hits (and new media) of the smart collections in the background.</summary>
 private void CountSmartCollections(IEnumerable<CollectionNode> nodes)
 {
  var rows = nodes.Where(n => n.Search != null).ToList();
  _searchCounts?.Cancel(); var cancel = _searchCounts = new CancellationTokenSource();
  _ = Task.Run(async () =>
  {
   foreach (var row in rows)
   {
    if (cancel.IsCancellationRequested) return;
    try
    {
     var (total, fresh) = _repository.CountSavedSearch(row.Search!, DateTimeOffset.Now, cancel.Token);
     await Dispatcher.InvokeAsync(() => { row.CountLabel = total.ToString("N0", CultureInfo.CurrentCulture); row.NewLabel = fresh > 0 ? $"+{fresh:N0}" : ""; });
    }
    catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException or ArgumentException or Microsoft.Data.Sqlite.SqliteException) { if (cancel.IsCancellationRequested) return; }
   }
  });
 }

 /// <summary>Saves the whole view as a smart collection: view, search text, tag filters, filter bar, advanced search, rating,
 /// sort and display.</summary>
 private void SaveSearch() => SaveSearch(null);
 private void SaveSearch(Guid? parent)
 {
  var state = CaptureViewState() with { Position = 0, AnchorId = null, Selection = [] };
  var input = Dialogs.Fields(this, T("New smart collection"), (T("Name (keeps view, search text, all filters and the sorting)"), ViewTitle.Text));
  if (input == null || string.IsNullOrWhiteSpace(input[0])) return;
  var order = (_repository.GetSavedSearches().Select(s => s.Order).DefaultIfEmpty(0).Max()) + 1;
  _repository.UpsertSavedSearch(new SavedSearch { Name = input[0].Trim(), State = state, Order = order, LastOpenedAt = DateTimeOffset.Now, ParentId = parent });
  Status.Text = T("Smart collection saved · ") + input[0].Trim();
  ShowToast(T("Smart collection “{0}” saved", input[0].Trim()));
 }

 private void ApplySavedSearch(SavedSearch search)
 {
  var state = _repository.Sanitize(CatalogRepository.StateOf(search), out var dropped);
  ApplyViewState(state with { Position = 0, AnchorId = null, Selection = [] }, search.Name);
  if (dropped.Count > 0) ShowToast(T("No longer present: ") + string.Join(", ", dropped), MessageBoxImage.Warning);
  _repository.MarkSavedSearchOpened(search.Id, DateTimeOffset.Now);
  if (CollectionNodes().FirstOrDefault(n => n.Search?.Id == search.Id) is { } row) row.NewLabel = "";
 }

 private void SaveSearchClicked(object sender, RoutedEventArgs e) { SaveSearch(); Refresh(); }

 private IEnumerable<MenuSpec> SmartCollectionMenu(CollectionNode node)
 {
  var search = node.Search!;
  void Save(SavedSearch changed) { _repository.UpsertSavedSearch(changed); Refresh(); }
  yield return MenuSpec.Command(T("Open"), () => ApplySavedSearch(search), "", T("Enter"));
  yield return MenuSpec.Separator;
  yield return MenuSpec.Command(T("Rename …"), () => RenameCollectionNode(node), "", "F2");
  yield return MenuSpec.Command(T("Overwrite with current view"), () =>
  {
   if (Message.Show(this, T("Replace “{0}” with the current view and its filters?", search.Name), T("Overwrite smart collection"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
   Save(search with { State = CaptureViewState() with { Position = 0, AnchorId = null, Selection = [] }, Expression = null, Text = null, Tag = null, TagCategory = null, Category = null, MinimumRating = 0, IsFavorite = null, WatchLater = null });
  }, "");
  yield return MenuSpec.Command(T("Duplicate"), () => Save(search with { Id = Guid.NewGuid(), Name = T("{0} (copy)", search.Name), Order = search.Order + 1 }), "");
  yield return MenuSpec.Submenu(T("Change icon"), SmartGlyphs.Select(g => (MenuSpec)new MenuCommand(g.Label, () => Save(search with { Glyph = g.Glyph })) { Icon = g.Glyph }));
  yield return MenuSpec.Submenu(T("Move to set"), MoveEntries(node), "");
  yield return MenuSpec.Separator;
  yield return MenuSpec.Command(T("Delete …"), () => DeleteCollectionNode(node), "", T("Del"));
 }

 /// <summary>Ctrl+Alt+1–9 opens the first nine smart collections.</summary>
 private bool OpenSavedSearchByNumber(int number)
 {
  var searches = _repository.GetSavedSearches();
  if (number < 1 || number > searches.Count) return false;
  ApplySavedSearch(searches[number - 1]); return true;
 }
}
