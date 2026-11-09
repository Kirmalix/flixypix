using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

// The tag area of the details: chips of the applied tags, suggestions that say why, pinned tags that show their state, and the
// tag mode. In tag mode T, digits and letters tag the selection with one hand while the arrow keys keep moving through the grid.
public partial class MainWindow
{
    private const string SuggestionKeys = "QWERASDF";
    private static readonly Key[] SuggestionKeyCodes = [Key.Q, Key.W, Key.E, Key.R, Key.A, Key.S, Key.D, Key.F];
    private bool _tagWork, _assistExpanded, _assistEngaged, _assistHooked, _feedbackDirty;
    private string _assistKey = "";
    private readonly List<TagSuggestion> _assistList = [];
    private HashSet<string> _assistOnAll = new(StringComparer.OrdinalIgnoreCase);
    private Guid[] _assistIds = [];
    private int _assistShown;
    private Dictionary<string, TagSuggestionFeedback>? _suggestionFeedback;

    private static IEnumerable<string> AppliedTags(MediaItem item) => item.Tags.Concat(item.TagCategories.Values.SelectMany(tags => tags));

    private static Border KeyCap(string key)
    {
        var cap = new Border { CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1), Padding = new Thickness(4, 0, 4, 0), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        cap.SetResourceReference(Border.BorderBrushProperty, "Muted");
        var text = new TextBlock { Text = key, FontSize = 11 };
        text.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        cap.Child = text;
        return cap;
    }

    /// <summary>An applied tag: only its × removes it, so a slip on the name loses nothing; the right mouse button offers the tag's actions.</summary>
    private Border FullTagChip(string tag, Guid[] ids)
    {
        var chip = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(9, 2, 3, 2), Margin = new Thickness(0, 0, 6, 6), Tag = tag };
        chip.SetResourceReference(Border.BackgroundProperty, "ChipApplied");
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var name = new TextBlock { Text = tag, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        name.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        var remove = new Button { Content = "×", Style = (Style)FindResource("Quiet"), Padding = new Thickness(5, 0, 5, 1), Focusable = false, ToolTip = T("Remove tag") };
        System.Windows.Automation.AutomationProperties.SetName(remove, T("Remove tag") + ": " + tag);
        remove.Click += async (_, _) => await ChangeTagsAsync(ids, [], [tag]);
        row.Children.Add(name); row.Children.Add(remove);
        chip.Child = row;
        chip.MouseRightButtonUp += (_, args) => { args.Handled = true; OpenTagMenu(new TagRow(tag, tag, "", TagBrush), chip); };
        return chip;
    }

    /// <summary>Pinned tags keep their number; each shows whether the selection has it.</summary>
    private void RenderPinnedTags(IReadOnlyList<MediaItem> items)
    {
        QuickTagButtons.Children.Clear();
        foreach (var slot in QuickTags.Select((tag, index) => (tag, index)).Where(p => p.tag.Length > 0))
        {
            var holders = items.Count(i => AppliedTags(i).Contains(slot.tag, StringComparer.OrdinalIgnoreCase));
            var on = items.Count > 0 && holders == items.Count;
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            if (_tagWork) content.Children.Add(KeyCap((slot.index + 1).ToString()));
            content.Children.Add(new TextBlock { Text = (on ? "✓ " : "") + slot.tag, VerticalAlignment = VerticalAlignment.Center });
            var pin = new Button { Content = content, Tag = slot.tag, Padding = new Thickness(8, 3, 8, 3), FontSize = 12, Focusable = false, IsEnabled = items.Count > 0, ToolTip = T("Pinned tag · click: assign or remove · right-click: more · {0}", KeyLabel("Ctrl+" + (slot.index + 1))) };
            if (on) pin.Background = Theme.Brush("ChipFull"); else if (holders > 0) pin.Background = PartialChipBrush;
            pin.Click += async (_, _) => await ToggleQuickTagAsync(slot.index + 1);
            pin.MouseRightButtonUp += (_, args) => { args.Handled = true; OpenTagMenu(new TagRow(slot.tag, slot.tag, "", TagBrush), pin); };
            QuickTagButtons.Children.Add(pin);
        }
        PinnedPanel.Visibility = QuickTagButtons.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private Task LoadTagAssistAsync(IReadOnlyList<MediaItem> items)
     => _libraryTasks.Run(async libraryToken =>
    {
        var version = ++_tagAssistVersion;
        if (!_assistHooked) { _assistHooked = true; DetailsScroll.SizeChanged += (_, args) => { if (args.HeightChanged && SuggestionCapacity(args.PreviousSize.Height) != SuggestionCapacity(args.NewSize.Height)) RenderSuggestions(); }; }
        var ids = items.Select(i => i.Id).ToArray();
        var key = string.Join(",", ids.OrderBy(id => id));
        // The list keeps its order while the selection stays: keys and rows must not move during tagging.
        if (key != _assistKey) { RecordSuggestionFeedback(); _assistKey = key; _assistList.Clear(); _assistExpanded = false; _assistEngaged = false; }
        _assistIds = ids;
        _assistOnAll = new(StringComparer.OrdinalIgnoreCase);
        if (items.Count > 0) { _assistOnAll.UnionWith(AppliedTags(items[0])); foreach (var item in items.Skip(1)) _assistOnAll.IntersectWith(AppliedTags(item)); }
        TagCombinationButtons.Children.Clear(); TagCombinationPanel.Visibility = Visibility.Collapsed;
        RenderSuggestions();
        if (items.Count == 0) return;
        var present = items.SelectMany(i => i.Tags).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var feedback = (_suggestionFeedback ??= new(_repository.GetPreferences().TagSuggestionFeedback ?? [], StringComparer.OrdinalIgnoreCase)).ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        var wantCombinations = _tagWork && present.Length > 0;
        (IReadOnlyList<TagSuggestion> Suggestions, IReadOnlyList<TagCombination> Combinations) assist;
        // Suggestions read every path, beside the shared connection so nothing waits for it. During a start after the lists, which read them too.
        if (_browsingFilled is { Task.IsCompleted: false } filling) { await filling.Task; if (version != _tagAssistVersion || _closing) return; }
        try { await Task.Run(() => _repository.PrepareTagSuggestions(libraryToken), libraryToken); }
        catch (ObjectDisposedException) { return; }
        if (version != _tagAssistVersion || _closing) return;
        try { assist = await Task.Run(() => (_repository.SuggestTags(ids, 10, feedback), wantCombinations ? _repository.GetTagCombinations(present, 4) : [])); }
        catch (ObjectDisposedException) { return; }
        if (version != _tagAssistVersion || _closing) return;
        foreach (var suggestion in assist.Suggestions)
            if (!_assistList.Any(s => s.Tag.Equals(suggestion.Tag, StringComparison.OrdinalIgnoreCase))) _assistList.Add(suggestion);
        RenderSuggestions();
        foreach (var combination in assist.Combinations)
        {
            var missing = combination.Tags.Where(t => !_assistOnAll.Contains(t)).ToArray(); if (missing.Length == 0) continue;
            var button = new Button
            {
                Content = new TextBlock { Text = string.Join(" · ", combination.Tags) + $"   {combination.MediaCount:N0}×", TextTrimming = TextTrimming.CharacterEllipsis },
                Tag = string.Join(",", missing),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(10, 5, 10, 5),
                FontSize = 12,
                Focusable = false,
                ToolTip = Tn(combination.MediaCount, "Shared by 1 item · click adds: {1}", "Shared by {0:N0} items · click adds: {1}", string.Join(", ", missing))
            };
            button.Click += async (_, _) => await ChangeTagsAsync(ids, missing, []);
            TagCombinationButtons.Children.Add(button);
        }
        TagCombinationPanel.Visibility = TagCombinationButtons.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    });

    /// <summary>How many suggestions fit without pushing the rest out of reach; the others wait behind "more".</summary>
    private int SuggestionCapacity(double height) => _tagWork || _assistExpanded ? SuggestionKeys.Length + 2 : height < 320 ? 3 : height < 520 ? 5 : SuggestionKeys.Length;

    private string SuggestionReason(TagSuggestion suggestion)
    {
        var reason = suggestion.Source switch
        {
            TagSuggestionSource.CoOccurrence when suggestion.BasedOn is { } tag => T("often with “{0}”", tag),
            TagSuggestionSource.Folder => T("in this folder"),
            _ => T("in the name")
        };
        return suggestion.Role switch { TagRole.Performer => T("performer") + " · " + reason, TagRole.Studio => T("studio") + " · " + reason, _ => reason };
    }

    private void RenderSuggestions()
    {
        TagSuggestionChips.Children.Clear();
        var capacity = SuggestionCapacity(DetailsScroll.ActualHeight);
        _assistShown = Math.Min(capacity, _assistList.Count);
        for (var index = 0; index < _assistShown; index++)
        {
            var suggestion = _assistList[index]; var applied = _assistOnAll.Contains(suggestion.Tag);
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            if (_tagWork && index < SuggestionKeys.Length) content.Children.Add(KeyCap(SuggestionKeys[index].ToString()));
            content.Children.Add(new TextBlock { Text = (applied ? "✓ " : "+ ") + suggestion.Tag, VerticalAlignment = VerticalAlignment.Center });
            var why = new TextBlock { Text = SuggestionReason(suggestion), FontSize = 11, Margin = new Thickness(7, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            why.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            content.Children.Add(why);
            var position = index;
            var chip = new Button { Content = content, Tag = suggestion.Tag, Background = applied ? Theme.Brush("ChipFull") : SuggestionChipBrush, Padding = new Thickness(9, 4, 9, 4), FontSize = 12, Focusable = false, ToolTip = suggestion.Reason };
            chip.Click += async (_, _) => await ToggleSuggestionAsync(position);
            chip.MouseRightButtonUp += (_, args) => { args.Handled = true; OpenTagMenu(new TagRow(suggestion.Tag, suggestion.Tag, "", TagBrush), chip); };
            TagSuggestionChips.Children.Add(chip);
        }
        var more = _assistList.Count - _assistShown;
        if (more > 0)
        {
            var expand = new Button { Content = Tn(more, "1 more", "{0:N0} more"), Style = (Style)FindResource("Quiet"), Focusable = false };
            expand.Click += (_, _) => { _assistExpanded = true; RenderSuggestions(); };
            TagSuggestionChips.Children.Add(expand);
        }
        TagSuggestionPanel.Visibility = _assistList.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task ToggleSuggestionAsync(int index)
    {
        if (index < 0 || index >= _assistList.Count || _assistIds.Length == 0) return;
        var suggestion = _assistList[index]; var applied = _assistOnAll.Contains(suggestion.Tag);
        string[] add = applied ? [] : [suggestion.Tag], remove = applied ? [suggestion.Tag] : [];
        if (suggestion.Role != TagRole.Tag && suggestion.Category is { } category) { _assistEngaged = true; await ChangeCategoryTagsAsync(_assistIds, category, add, remove); }
        else await ChangeTagsAsync(_assistIds, add, remove);
    }

    /// <summary>A medium whose tags were worked on tells which suggestions helped; merely browsing past one says nothing.</summary>
    private void RecordSuggestionFeedback()
    {
        if (!_assistEngaged || _suggestionFeedback == null) return;
        foreach (var suggestion in _assistList.Take(_assistShown))
        {
            var known = _suggestionFeedback.GetValueOrDefault(suggestion.Tag) ?? new TagSuggestionFeedback(0, 0);
            _suggestionFeedback[suggestion.Tag] = _assistOnAll.Contains(suggestion.Tag) ? known with { Taken = known.Taken + 1 } : known with { Ignored = known.Ignored + 1 };
        }
        _feedbackDirty = true; _assistEngaged = false;
    }

    /// <summary>Written when the library closes: saving preferences during tagging would discard the tag statistics each time.</summary>
    private void SaveSuggestionFeedback()
    {
        RecordSuggestionFeedback();
        if (!_feedbackDirty || _suggestionFeedback == null) return;
        _repository.SavePreferences(_repository.GetPreferences() with { TagSuggestionFeedback = new(_suggestionFeedback) });
        _feedbackDirty = false;
    }

    // ----- Tag mode -----

    private void TagWorkClicked(object sender, RoutedEventArgs e) => SetTagWork(!_tagWork);

    internal void SetTagWork(bool on)
    {
        if (on && SelectedMedia.Count == 0) { Status.Text = T("Please select items first."); return; }
        _tagWork = on;
        TagWorkBanner.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        TagWorkButton.SetResourceReference(BackgroundProperty, on ? "ChipFull" : "ButtonBackground");
        UpdateTagEditor(SelectedMedia);
        if (!on) return;
        OpenDetailsAt("tags");
        // The grid keeps the keyboard, so the arrow keys go on to the next medium.
        Dispatcher.BeginInvoke(() =>
        {
            if (MediaList.IsKeyboardFocusWithin) return;
            if (MediaList.SelectedItem is { } item && MediaList.ItemContainerGenerator.ContainerFromItem(item) is ListBoxItem row) row.Focus(); else MediaList.Focus();
        }, DispatcherPriority.Loaded);
    }

    /// <summary>The work of a key in tag mode, or null when the key is not for tag mode.</summary>
    internal Task? TagWorkKey(Key key, ModifierKeys modifiers, bool textEntry)
    {
        if (textEntry || modifiers != ModifierKeys.None || !_startupReady || !MediaList.IsVisible || FilterFlyout.IsOpen || ViewPopup.IsOpen) return null;
        if (!_tagWork)
        {
            if (key != Key.T || SelectedMedia.Count == 0) return null;
            SetTagWork(true); return Task.CompletedTask;
        }
        if (key is Key.Escape or Key.T) { SetTagWork(false); return Task.CompletedTask; }
        var pin = key is >= Key.D1 and <= Key.D9 ? key - Key.D0 : key is >= Key.NumPad1 and <= Key.NumPad9 ? key - Key.NumPad0 : 0;
        if (pin > 0) return ToggleQuickTagAsync(pin);
        var letter = Array.IndexOf(SuggestionKeyCodes, key);
        return letter >= 0 ? ToggleSuggestionAsync(letter) : null;
    }
}
