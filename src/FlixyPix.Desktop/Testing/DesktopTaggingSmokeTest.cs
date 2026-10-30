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
  string[] Chips(Panel panel) => panel.Children.OfType<Button>().Select(b => b.Content as string ?? "").ToArray();
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
  Require(TagCombinationButtons.Children.OfType<Button>().Any(b => (b.Content as string ?? "").Contains("Ausflug")), "Typical combination missing for an untagged medium.");
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
  Require(ViewTitle.Text == T("All items") && ActiveTagFilters.Children.Count == 0, "Clearing tag filters did not reset the view.");
  await ChangeTagsAsync(ids, [], ["Ausflug"]); await ChangeTagsAsync([others[0].Item.Id, others[1].Item.Id], [], ["Natur", "Erinnerungen"]);
  Require(_repository.QueryPage(new() { RequiredTags = ["Natur"] }).TotalCount == 1, "Bulk tag removal failed.");
 }
}
