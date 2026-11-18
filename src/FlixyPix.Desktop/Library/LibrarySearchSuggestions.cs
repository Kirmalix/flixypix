using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

/// <summary>Suggestions under the search field: typing part of a performer, studio or tag offers it as a filter. Arrow keys
/// choose, Enter takes the chosen one; without a choice Enter keeps searching the text (and its short forms).</summary>
public partial class MainWindow
{
    private sealed record Suggestion(string Kind, string Name, int Count, DirectoryEntry? Entry);
    private List<Suggestion> _suggestions = [];
    private int _suggestionIndex = -1;

    private void InitializeSearchSuggestions()
    {
        SearchBox.TextChanged += (_, _) => UpdateSuggestions();
        SearchBox.PreviewKeyDown += SuggestionKeyDown;
        SearchBox.LostKeyboardFocus += (_, _) => SuggestPopup.IsOpen = false;
        SuggestPopup.Closed += (_, _) => _suggestionIndex = -1;
    }

    private void UpdateSuggestions()
    {
        var text = SearchBox.Text.Trim();
        // Short forms (#tag, -#tag, @mark, ★4, type:video) stay with the search field's own Enter.
        if (_repository == null || !SearchBox.IsKeyboardFocused || !ShowsMediaGrid || IsRoleView || text.Length < 2 || text.IndexOfAny(['#', '@', '★', ':']) >= 0) { SuggestPopup.IsOpen = false; return; }
        var found = new List<(Suggestion Item, int Score)>();
        int Score(string name, IEnumerable<string> aliases)
        {
            if (name.StartsWith(text, StringComparison.CurrentCultureIgnoreCase)) return 3;
            if (name.Split(' ', '-', '_').Any(w => w.StartsWith(text, StringComparison.CurrentCultureIgnoreCase))) return 2;
            if (name.Contains(text, StringComparison.CurrentCultureIgnoreCase) || aliases.Any(a => a.Contains(text, StringComparison.CurrentCultureIgnoreCase))) return 1;
            return 0;
        }
        var roleNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in _roleNames.GroupBy(n => n.Tag, StringComparer.OrdinalIgnoreCase))
        {
            var first = group.First(); roleNames.Add(first.Tag);
            if (Score(first.Tag, group.SelectMany(n => n.Aliases)) is > 0 and var score && State(new DirectoryEntry(first.Tag, first.Category, first.Role, 0)) == 0)
                found.Add((new(first.Role == TagRole.Performer ? T("Performer") : T("Studio"), first.Tag, group.Max(n => n.Count), new DirectoryEntry(first.Tag, first.Category, first.Role, 0)), score));
        }
        foreach (var entry in _tagEntries.Where(e => e.Count > 0 && !roleNames.Contains(e.Tag)))
            if (Score(entry.Tag, []) is > 0 and var score && !_requiredTags.Contains(entry.Tag, StringComparer.OrdinalIgnoreCase) && !_excludedTags.Contains(entry.Tag, StringComparer.OrdinalIgnoreCase))
                found.Add((new(T("Tag"), entry.Tag, entry.Count, null), score));
        _suggestions = found.OrderByDescending(f => f.Score).ThenByDescending(f => f.Item.Count).ThenBy(f => f.Item.Name, StringComparer.CurrentCultureIgnoreCase).Take(7).Select(f => f.Item).ToList();
        _suggestionIndex = -1;
        if (_suggestions.Count == 0) { SuggestPopup.IsOpen = false; return; }
        RenderSearchSuggestions();
        SuggestFrame.Width = Math.Max(320, Math.Min(SearchFrame.ActualWidth, 520));
        SuggestPopup.IsOpen = true;
    }

    private void RenderSearchSuggestions()
    {
        SuggestList.Children.Clear();
        var heading = new TextBlock { Text = T("Filter by …"), FontSize = 11, Margin = new Thickness(8, 2, 0, 4) };
        heading.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        SuggestList.Children.Add(heading);
        for (var index = 0; index < _suggestions.Count; index++)
        {
            var item = _suggestions[index]; var at = index;
            var line = new DockPanel();
            var kind = new TextBlock { Text = item.Kind, FontSize = 11, Width = 72, VerticalAlignment = VerticalAlignment.Center };
            kind.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            var count = new TextBlock { Text = item.Count.ToString("N0", CultureInfo.CurrentCulture), FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 2, 0) };
            count.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            DockPanel.SetDock(kind, Dock.Left); DockPanel.SetDock(count, Dock.Right);
            line.Children.Add(kind); line.Children.Add(count);
            line.Children.Add(new TextBlock { Text = item.Name, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
            // Not focusable: the search field keeps the focus while the mouse or the arrow keys choose.
            var row = new Button { Content = line, Style = (Style)FindResource("FilterRow"), Focusable = false, Padding = new Thickness(8, 5, 8, 5) };
            if (index == _suggestionIndex) row.SetResourceReference(BackgroundProperty, "ItemHighlight");
            AutomationProperties.SetName(row, item.Kind + ": " + item.Name);
            row.Click += (_, _) => TakeSuggestion(at);
            SuggestList.Children.Add(row);
        }
        var hint = new TextBlock { Text = T("↑↓ choose · Enter filter · Esc close"), FontSize = 11, Margin = new Thickness(8, 4, 0, 2) };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "Subtle");
        SuggestList.Children.Add(hint);
    }

    private void SuggestionKeyDown(object sender, KeyEventArgs e)
    {
        if (!SuggestPopup.IsOpen || _suggestions.Count == 0) return;
        switch (e.Key)
        {
            case Key.Down: _suggestionIndex = Math.Min(_suggestionIndex + 1, _suggestions.Count - 1); RenderSearchSuggestions(); e.Handled = true; break;
            case Key.Up: _suggestionIndex = Math.Max(_suggestionIndex - 1, -1); RenderSearchSuggestions(); e.Handled = true; break;
            case Key.Enter when _suggestionIndex >= 0: TakeSuggestion(_suggestionIndex); e.Handled = true; break;
            case Key.Escape: SuggestPopup.IsOpen = false; e.Handled = true; break;
            case Key.Tab: SuggestPopup.IsOpen = false; break;
        }
    }

    private void TakeSuggestion(int index)
    {
        if (index < 0 || index >= _suggestions.Count) return;
        var item = _suggestions[index];
        SuggestPopup.IsOpen = false;
        SearchBox.Text = ""; _searchTimer.Stop();
        if (item.Entry is { } entry)
        {
            NoteFilterUse(entry.Role == TagRole.Performer ? "performers" : "studios", "v:" + UsageKind(entry.Role) + ":" + entry.Tag);
            SetFacetValue(entry, 1);
        }
        else
        {
            NoteFilterUse(TagSectionKey(item.Name), "v:tag:" + item.Name);
            SetTagValue(item.Name, 1);
        }
        Status.Text = T("“{0}” became a filter", item.Name);
    }
}
