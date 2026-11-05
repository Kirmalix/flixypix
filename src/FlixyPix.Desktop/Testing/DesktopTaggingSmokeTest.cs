using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

public partial class MainWindow
{
 // Multi-selection tagging, partial chips, suggestions, combinations, autocomplete and combinable tag filters.
 private async Task RunTaggingSmokeTestAsync(string directory, Guid firstId)
 {
  static void Require(bool check, string message) { if (!check) throw new InvalidOperationException(message); }
  async Task Idle() => await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
  string[] Chips(Panel panel) => panel.Children.OfType<FrameworkElement>().Select(c => c is Border { Tag: string full } ? full + "  ×" : c is Button { Tag: string tag } ? "+ " + tag : (c as Button)?.Content as string ?? "").ToArray();
  await RefreshAsync(firstId);
  var cards = MediaList.Items.Cast<MediaCard>().ToArray();
  var first = cards.Single(c => c.Item.Id == firstId); var others = cards.Where(c => c.Item.Id != firstId).Take(3).ToArray();
  MediaList.SelectedItems.Clear(); MediaList.SelectedItems.Add(first); MediaList.SelectedItems.Add(others[0]); MediaList.SelectedItems.Add(others[1]);
  await Idle(); await _tagAssistReady;
  Require(SingleDetailsTop.Visibility == Visibility.Collapsed && SelectedName.Text == Tn(3, "1 item selected", "{0:N0} items selected"), "Multi-selection did not switch the details panel.");
  Require(Chips(TagChips).Contains("Natur · 1/3"), "Partial tag chip missing for multi-selection.");
  var ids = new[] { first.Item.Id, others[0].Item.Id, others[1].Item.Id };
  await ChangeTagsAsync(ids, ["Natur", "Ausflug"], []);
  Require(SelectedMedia.Count == 3 && SelectedMedia.All(i => i.Tags.Contains("Natur") && i.Tags.Contains("Ausflug")), "Bulk tagging lost the selection or tags.");
  Require(Chips(TagChips).Contains("Ausflug  ×"), "Full tag chip missing after bulk tagging.");
  // Without a tag filter the edit updates in place: the tiles stay, the tag list counts the new tag.
  Require(MediaList.Items.Cast<MediaCard>().Contains(first) && _tagEntries.Any(t => t.Tag == "Ausflug" && t.Count == 3), "A tag edit rebuilt the tiles or left the tag list stale.");
  await Idle(); await _tagAssistReady; await Idle();
  Capture(RootContent, Path.Combine(directory, "desktop-multiselect.png"));
  // Keyboard tagging: Ctrl+n toggles the n-th quick tag on the selection.
  var quick = QuickTags; Require(quick.Count > 0 && Tags.Items.OfType<TagRow>().Any(r => r.Shortcut == KeyLabel("Ctrl+1")), "Quick tags are not numbered in the tag list.");
  var hadQuick = SelectedMedia.Where(i => i.Tags.Contains(quick[0], StringComparer.OrdinalIgnoreCase)).Select(i => i.Id).ToArray();
  await ToggleQuickTagAsync(1); var onAll = SelectedMedia.All(i => i.Tags.Contains(quick[0], StringComparer.OrdinalIgnoreCase));
  await ToggleQuickTagAsync(1); var offAll = SelectedMedia.All(i => !i.Tags.Contains(quick[0], StringComparer.OrdinalIgnoreCase));
  Require(onAll && offAll, "Ctrl+1 did not add and then remove the first quick tag.");
  if (hadQuick.Length > 0) await ChangeTagsAsync(hadQuick, [quick[0]], []);
  await ChangeTagsAsync(ids, ["Natur", "Ausflug"], []);
  await ChangeTagsAsync([others[1].Item.Id], ["Erinnerungen"], []);
  await RefreshAsync(others[0].Item.Id); await _tagAssistReady;
  Require(Chips(TagSuggestionChips).Contains("+ Erinnerungen"), "Co-occurrence suggestion missing.");
  await RefreshAsync(others[2].Item.Id); await _tagAssistReady;
  Require(TagCombinationPanel.Visibility != Visibility.Visible, "Typical combinations showed outside tag mode.");
  // Tag mode: T starts it, a digit and a letter tag the selection, the list keeps its order, Esc ends it.
  await RefreshAsync(others[0].Item.Id); MediaList.Focus(); await Idle(); await _tagAssistReady;
  Require(TagWorkKey(System.Windows.Input.Key.T, System.Windows.Input.ModifierKeys.None, true) == null, "T in a text field started tag mode.");
  if (TagWorkKey(System.Windows.Input.Key.T, System.Windows.Input.ModifierKeys.None, false) is { } startWork) await startWork;
  await Idle(); await _tagAssistReady; await Idle();
  Require(TagWorkBanner.Visibility == Visibility.Visible, "T did not start tag mode.");
  var suggestionsBefore = _assistList.Select(s => s.Tag).ToArray();
  Require(suggestionsBefore.Length > 0, "Tag mode has no suggestion to work with.");
  var firstSuggestion = suggestionsBefore[0]; var work = TagWorkKey(System.Windows.Input.Key.Q, System.Windows.Input.ModifierKeys.None, false); Require(work != null, "Q did nothing in tag mode."); await work!; await _lastRefresh; await Idle(); await _tagAssistReady; await Idle();
  Require(SelectedMedia[0].Tags.Contains(firstSuggestion, StringComparer.OrdinalIgnoreCase), "Q did not assign the first suggestion.");
  Require(_assistList.Select(s => s.Tag).Take(suggestionsBefore.Length).SequenceEqual(suggestionsBefore), "The suggestions moved while tagging one medium.");
  work = TagWorkKey(System.Windows.Input.Key.Q, System.Windows.Input.ModifierKeys.None, false); await work!; await _lastRefresh; await Idle(); await _tagAssistReady; await Idle();
  Require(!SelectedMedia[0].Tags.Contains(firstSuggestion, StringComparer.OrdinalIgnoreCase), "A second Q did not take the suggestion back.");
  // Combinations appear in tag mode, for a medium that lacks part of one.
  await ChangeTagsAsync([others[2].Item.Id], ["Natur"], []); await RefreshAsync(others[2].Item.Id); await Idle(); await _tagAssistReady; await Idle();
  Require(TagCombinationPanel.Visibility == Visibility.Visible && TagCombinationButtons.Children.OfType<Button>().Any(b => (b.Tag as string ?? "").Contains("Ausflug")), "Typical combinations are missing in tag mode.");
  await ChangeTagsAsync([others[2].Item.Id], [], ["Natur"]); await _lastRefresh; await Idle();
  if (TagWorkKey(System.Windows.Input.Key.Escape, System.Windows.Input.ModifierKeys.None, false) is { } endWork) await endWork;
  Require(TagWorkBanner.Visibility == Visibility.Collapsed, "Esc did not end tag mode.");
  TagInput.Focus(); TagInput.Text = "ausf"; await Idle();
  Require(TagCompletions.Items.OfType<TagRow>().Any(row => row.Tag == "Ausflug"), "Tag autocompletion did not offer an existing tag.");
  TagInput.Text = ""; MediaList.Focus();
  ToggleTagFilter("Ausflug", false); ToggleTagFilter("Erinnerungen", true); await _lastRefresh;
  Require(MediaList.Items.Count == 1 && ActiveTagFilters.Children.Count == 3, "Combined include/exclude tag filters failed.");
  var expression = WithTagFilters(null);
  Require(_repository.QueryPage(new() { Expression = expression }).TotalCount == 1, "Tag filters did not translate into a saved-search expression.");
  TagFilterBox.Text = "ausf"; Require(Tags.Items.Count == 1, "Tag sidebar filter failed."); TagFilterBox.Text = "";
  Require(Tags.Items.Count < _tagEntries.Count || _tagEntries.All(t => t.Count > 0), "Unused vocabulary tags were not hidden.");
  await Idle(); Capture(RootContent, Path.Combine(directory, "desktop-tag-filters.png"));
  ClearTagFilters(); _anchor = 0; ApplyTagFilterChanges(); await _lastRefresh;
  // A row stays in place after a click and only sorts up once the pointer has left the list.
  var rowsBefore = Tags.Items.OfType<TagRow>().Select(r => r.Tag).ToArray(); var lastRow = rowsBefore[^1];
  _tagRowsOnTop = new(StringComparer.OrdinalIgnoreCase); ToggleTagFilter(lastRow, false); await _lastRefresh;
  Require(Tags.Items.OfType<TagRow>().Select(r => r.Tag).Last() == lastRow, "A clicked tag row moved under the pointer.");
  TagListLeft(Tags, null!); Require(Tags.Items.OfType<TagRow>().First().Tag == lastRow, "The tag list did not sort again after the pointer left.");
  ClearTagFilters(); _anchor = 0; ApplyTagFilterChanges(); await _lastRefresh;
  // The folder filter is visible as a chip, counts as a filter and can be removed.
  var folderPath = Path.GetDirectoryName(others[0].Item.Path)!; _folder = folderPath; await RefreshAsync();
  var folderChip = T("Folder: {0}", Path.GetFileName(folderPath)) + "  ×";
  Require(ActiveTagFilters.Children.OfType<Button>().Any(b => b.Content as string == folderChip) && FilterButtonText.Text == T("Filter · {0:N0}", 1), "The folder filter has no chip.");
  ClearFolderFilter(); await _lastRefresh;
  Require(_folder == null && !ActiveTagFilters.Children.OfType<Button>().Any(), "The folder chip did not end the filter.");
  // A multi-selection is stated at the grid and can be cleared there.
  MediaList.SelectedItems.Clear(); foreach (var card in MediaList.Items.Cast<MediaCard>().Take(2)) MediaList.SelectedItems.Add(card); await Idle();
  Require(SelectionBar.Visibility == Visibility.Visible, "The selection bar is missing for a multi-selection.");
  ClearSelectionClicked(this, new RoutedEventArgs()); await Idle();
  Require(SelectedMedia.Count == 0 && SelectionBar.Visibility == Visibility.Collapsed, "Clearing the selection left it active.");
  Require(ViewTitle.Text == T("All items") && ActiveTagFilters.Children.Count == 0, "Clearing tag filters did not reset the view.");
  await ChangeTagsAsync(ids, [], ["Ausflug"]); await ChangeTagsAsync([others[0].Item.Id, others[1].Item.Id], [], ["Natur", "Erinnerungen"]);
  Require(_repository.QueryPage(new() { RequiredTags = ["Natur"] }).TotalCount == 1, "Bulk tag removal failed.");
 }
}
