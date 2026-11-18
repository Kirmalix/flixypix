using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

/// <summary>The head of the media list: the title with its count, the actions of the page and the view controls
/// (appearance, sorting, the view menu). When the room runs short the controls give way in steps instead of all at once.</summary>
public partial class MainWindow
{
    // ----- Fitting the head into its width -----

    /// <summary>The title keeps at least this much room (at 100 %) before the controls give way.</summary>
    private const double TitleRoom = 240;
    private const int HeaderSteps = 4;
    private bool _fittingHeader, _headerFitPending;
    private int _headerStep = -1;

    private void InitializeHeader()
    {
        // Any action that comes or goes changes the room; their visibility is set in many places.
        var visibility = DependencyPropertyDescriptor.FromProperty(VisibilityProperty, typeof(UIElement));
        foreach (UIElement action in PageActions.Children) visibility.AddValueChanged(action, (_, _) => RequestHeaderFit());
        visibility.AddValueChanged(ViewTools, (_, _) => RequestHeaderFit());
        visibility.AddValueChanged(EntityPortrait, (_, _) => RequestHeaderFit());
        var text = DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock));
        text.AddValueChanged(ViewTitle, (_, _) => RequestHeaderFit());
        text.AddValueChanged(HeaderCount, (_, _) => RequestHeaderFit());
        SortChoice.SelectionChanged += (_, _) => UpdateSortButton();
        ViewButtonText.Text = Tc("library", "View");
        System.Windows.Automation.AutomationProperties.SetName(ViewButton, ViewButtonText.Text);
        foreach (var (item, key) in ViewChoice.Items.OfType<ListBoxItem>().Select((item, index) => (item, index + 1)))
            item.ToolTip = $"{item.ToolTip} · Ctrl+{key}";
        UpdateSortButton();
        ApplyHeaderStep(0);
    }

    private void MediaHeaderSizeChanged(object sender, SizeChangedEventArgs e) { if (e.WidthChanged) FitHeader(); }

    private void RequestHeaderFit()
    {
        if (_fittingHeader || _headerFitPending) return;
        _headerFitPending = true;
        Dispatcher.BeginInvoke(() => { _headerFitPending = false; FitHeader(); }, DispatcherPriority.Loaded);
    }

    /// <summary>Takes the first step whose controls fit beside the title: 0 everything, 1 the sort button without its
    /// label, 2 the actions of the page in a "…" menu, 3 sorting and 4 the appearance in the view menu.</summary>
    private void FitHeader()
    {
        var width = MediaHeader.ActualWidth;
        if (_fittingHeader || width <= 0) return;
        _fittingHeader = true;
        try
        {
            TitleRow.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var title = Math.Min(TitleRow.DesiredSize.Width, TitleRoom) + (EntityPortrait.Visibility == Visibility.Visible ? EntityPortrait.Width + 14 : 0);
            var room = width - title - 16;
            var step = 0;
            for (; step < HeaderSteps; step++)
            {
                ApplyHeaderStep(step);
                HeaderTools.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                if (HeaderTools.DesiredSize.Width <= room) break;
            }
            if (step == HeaderSteps) ApplyHeaderStep(step);
        }
        finally { _fittingHeader = false; }
    }

    private bool PageActionsShown => PageActions.Children.OfType<UIElement>().Any(action => action.Visibility == Visibility.Visible && action is not ProgressBar);

    private void ApplyHeaderStep(int step)
    {
        _headerStep = step;
        SortButtonText.Visibility = step >= 1 ? Visibility.Collapsed : Visibility.Visible;
        var fold = step >= 2 && PageActionsShown;
        PageActions.Visibility = fold ? Visibility.Collapsed : Visibility.Visible;
        PageActionsMore.Visibility = fold ? Visibility.Visible : Visibility.Collapsed;
        Place(SortTools, step >= 3 ? PopupSortHost : SortHost);
        Place(ViewChoice, step >= 4 ? PopupViewHost : ViewChoiceHost);
        SortHost.Visibility = step >= 3 ? Visibility.Collapsed : Visibility.Visible;
        ViewChoiceHost.Visibility = step >= 4 ? Visibility.Collapsed : Visibility.Visible;
        PopupSortSection.Visibility = step >= 3 ? Visibility.Visible : Visibility.Collapsed;
        PopupViewSection.Visibility = step >= 4 ? Visibility.Visible : Visibility.Collapsed;
        // Once the menu holds more than size and spacing, it says what it is.
        ViewButtonText.Visibility = step >= 3 ? Visibility.Visible : Visibility.Collapsed;
        ViewButton.ToolTip = step >= 3 ? T("Appearance, tile size and sorting") : T("Tile size, spacing and thumbnails");
        ViewToolsDivider.Visibility = fold || PageActionsShown ? Visibility.Visible : Visibility.Collapsed;
        UpdateSortButton();
    }

    /// <summary>Moves a control between its place in the head and its place in the view menu; it stays one control,
    /// so its state and handlers never need to be kept in step.</summary>
    private static void Place(FrameworkElement control, Border host)
    {
        if (control.Parent == host) return;
        var hadFocus = control.IsKeyboardFocusWithin;
        if (control.Parent is Border previous) previous.Child = null;
        host.Child = control;
        if (hadFocus) control.Focus();
    }

    /// <summary>The actions of the page as a menu while the head has no room for them.</summary>
    private void PageActionsMoreClicked(object sender, RoutedEventArgs e)
    {
        var items = new List<MenuSpec>();
        foreach (var action in PageActions.Children.OfType<FrameworkElement>().Where(action => action.Visibility == Visibility.Visible))
        {
            switch (action)
            {
                case CheckBox box:
                    items.Add(new MenuCommand(LabelOf(box), () => { box.IsChecked = box.IsChecked != true; box.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); }) { Checked = box.IsChecked == true });
                    break;
                case Button button when button == CollectionActions:
                    // It opens a menu of its own, which then hangs below the "…" button.
                    items.Add(new MenuCommand(LabelOf(button), () => Dispatcher.BeginInvoke(() => CollectionMenu(PageActionsMore, new RoutedEventArgs()), DispatcherPriority.Input)));
                    break;
                case Button button:
                    items.Add(new MenuCommand(LabelOf(button), () => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent))));
                    break;
                case Panel group:
                    if (items.Count > 0) items.Add(MenuSpec.Separator);
                    foreach (var choice in group.Children.OfType<ToggleButton>())
                        items.Add(new MenuCommand(LabelOf(choice), () => { choice.IsChecked = true; choice.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); }) { Checked = choice.IsChecked == true });
                    items.Add(MenuSpec.Separator);
                    break;
            }
        }
        MenuBelow(sender, items);
    }

    /// <summary>The words of a button, without its icon glyphs.</summary>
    private static string LabelOf(ContentControl control) => control.Content switch
    {
        string text => text,
        TextBlock block => block.Text,
        DependencyObject content => string.Join(" ", Logicals<TextBlock>(content).Select(block => block.Text).Where(text => text.Length > 0 && text[0] is < '' or > '')),
        _ => "",
    };

    // ----- Sorting: a field and its direction -----

    /// <summary>One sort field with the stored sort choices of its two directions (see <see cref="ViewState.SortFor"/>);
    /// the first direction is the one a field starts with.</summary>
    private sealed record SortField(string Label, int First, int Reverse);

    private SortField[] SortFields() =>
    [
        new(T("Title"), 0, 1), new(T("Date added"), 2, 11), new(T("File date"), 7, 6), new(T("Duration"), 3, 12),
        new(T("Rating"), 4, 13), new(T("O counter"), 8, 14), new(T("Resolution"), 9, 15), new(T("Bit rate"), 10, 16),
        new(T("Collection order"), 5, 5),
    ];

    private SortField CurrentSortField(out bool reversed)
    {
        var choice = Math.Max(0, SortChoice.SelectedIndex);
        var field = SortFields().FirstOrDefault(f => f.First == choice || f.Reverse == choice) ?? SortFields()[0];
        reversed = field.First != choice;
        return field;
    }

    private void UpdateSortButton()
    {
        var field = CurrentSortField(out _);
        var (_, descending) = ViewState.SortFor(Math.Max(0, SortChoice.SelectedIndex));
        var ordered = field.First != field.Reverse;
        SortButtonText.Text = field.Label;
        SortButton.ToolTip = T("Sorting: {0}", field.Label);
        SortDirection.Content = descending ? "" : "";
        SortDirection.Visibility = ordered ? Visibility.Visible : Visibility.Collapsed;
        var direction = descending ? T("Descending") : T("Ascending");
        SortDirection.ToolTip = direction + " · " + T("Click to reverse the order");
        System.Windows.Automation.AutomationProperties.SetName(SortDirection, direction);
    }

    private void SortButtonClicked(object sender, RoutedEventArgs e)
    {
        var current = CurrentSortField(out _);
        // In the view menu the sort list would close the menu under it; it opens below the menu's button instead.
        var below = ViewPopup.IsOpen ? (UIElement)ViewButton : SortButton;
        if (ViewPopup.IsOpen) ViewPopup.IsOpen = false;
        var collection = _playlist != null || current.First == 5;
        var items = SortFields().Where(field => field.First != 5 || collection)
            .Select(field => (MenuSpec)new MenuCommand(field.Label, () => SortChoice.SelectedIndex = field == current ? SortChoice.SelectedIndex : field.First) { Checked = field == current });
        MenuBelow(below, items);
    }

    private void SortDirectionClicked(object sender, RoutedEventArgs e)
    {
        var field = CurrentSortField(out var reversed);
        SortChoice.SelectedIndex = reversed ? field.First : field.Reverse;
    }

    // ----- The count beside the title -----

    private int _countVersion;
    private ViewState? _countState;
    private Playlist? _countPlaylist;
    private long _countShown;
    private int _countFinished;
    private string _countDetails = "";
    private (string Key, long Count)? _countWhole;
    private (string Key, MediaTotals Totals)? _countTotals;

    /// <summary>A page that is not a media list: its line below the title, no count beside it.</summary>
    private void SetHeaderLine(string line)
    {
        _countVersion++; _countState = null;
        HeaderCount.Text = ""; HeaderCountDetails.Text = ""; CountLabel.Text = line;
    }

    /// <summary>A list of entries with a count beside the title.</summary>
    private void SetHeaderCount(long shown, long all)
    {
        _countVersion++; _countState = null;
        HeaderCount.Text = shown == all ? all.ToString("N0") : T("{0:N0} of {1:N0}", shown, all);
        HeaderCountDetails.Text = T("{0:N0} of {1:N0} entries", shown, all);
        CountLabel.Text = "";
    }

    /// <summary>The count of a media list: with filters or a search "shown of all" of the same view, the filters
    /// themselves in the tooltip (the filter bar shows them already), sizes and playing time when the tooltip opens.</summary>
    private void ShowMediaCount(ViewState state, long shown, int finished, string details, Playlist? playlist)
    {
        var version = ++_countVersion;
        _countState = state; _countPlaylist = playlist; _countShown = shown; _countFinished = finished; _countDetails = details;
        CountLabel.Text = "";
        var whole = Narrowed(state) ? WholeView(state) : null;
        var key = whole is null ? null : CountKey(whole, playlist);
        RenderMediaCount(key is not null && _countWhole is { } known && known.Key == key ? known.Count : null);
        RenderCountDetails(null);
        if (whole is null || key is null || _repository is not { } repository) return;
        _ = _libraryTasks.Run(async token =>
        {
            var query = whole.ToQuery(DateTimeOffset.Now) with { Offset = 0, Limit = 1 };
            var count = await Task.Run(() => playlist != null ? repository.QueryPlaylistPage(playlist, query, false).Page.TotalCount : repository.CountMedia(query, token), token);
            if (_closing) return;
            _countWhole = (key, count);
            if (version == _countVersion) RenderMediaCount(count);
        });
    }

    private void RenderMediaCount(long? whole)
    {
        HeaderCount.Text = (whole is { } all && all != _countShown ? T("{0:N0} of {1:N0}", _countShown, all) : _countShown.ToString("N0"))
            + (_countFinished > 0 ? T(" · {0:N0} done", _countFinished) : "");
    }

    private void RenderCountDetails(MediaTotals? totals)
    {
        var lines = new List<string> { _countDetails.Length > 0 ? _countDetails : Tn(_countShown, "1 item", "{0:N0} items") };
        if (totals is { Count: > 0 } sum)
        {
            lines.Add(T("Size: {0}", MediaCard.SizeText(sum.Bytes)));
            if (sum.Seconds >= 1) lines.Add(T("Playing time: {0}", PlayingTime(sum.Seconds)));
        }
        HeaderCountDetails.Text = string.Join("\n", lines);
    }

    private static string PlayingTime(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Round(seconds));
        return time.TotalHours >= 1 ? T("{0:N0} h {1} min", Math.Floor(time.TotalHours), time.Minutes) : T("{0} min", Math.Max(1, (int)Math.Round(time.TotalMinutes)));
    }

    /// <summary>Sizes and playing time cost a pass over the hits, so they are only read when someone looks.</summary>
    private void HeaderCountToolTipOpening(object sender, ToolTipEventArgs e)
    {
        if (HeaderCountDetails.Text.Length == 0) { e.Handled = true; return; }
        if (_countState is not { } state || _countPlaylist != null || _repository is not { } repository) return;
        var key = CountKey(state, null);
        if (_countTotals is { } known && known.Key == key) RenderCountDetails(known.Totals);
        var version = _countVersion;
        _ = _libraryTasks.Run(async token =>
        {
            var totals = await Task.Run(() => repository.TotalsOf(state.ToQuery(DateTimeOffset.Now), token), token);
            if (_closing) return;
            _countTotals = (key, totals);
            if (version == _countVersion) RenderCountDetails(totals);
        });
    }

    /// <summary>Filters, search and tag filters narrow a view; the view itself (kind, tag page, folder, collection) stays.</summary>
    private static bool Narrowed(ViewState state) => !state.Filter.IsEmpty || !string.IsNullOrWhiteSpace(state.SearchText) || state.RequiredTags.Count > 0
        || state.ExcludedTags.Count > 0 || state.MinimumStars > 0 || state.Category != null;

    private static ViewState WholeView(ViewState state) =>
        state with { Filter = new(), SearchText = null, RequiredTags = [], ExcludedTags = [], TagsMatchAny = false, MinimumStars = 0, Category = null };

    private static string CountKey(ViewState state, Playlist? playlist) => FilterKey(state) + "|" + playlist?.Id;

    // ----- Keys -----

    /// <summary>Ctrl+1, 2, 3 switch between tiles, list and video wall.</summary>
    private bool ViewModeKey(System.Windows.Input.KeyEventArgs e)
    {
        if (System.Windows.Input.Keyboard.Modifiers != System.Windows.Input.ModifierKeys.Control || !ShowsMediaGrid || _loupe != null || _survey != null || TextEntry(e.OriginalSource)) return false;
        var mode = e.Key switch
        {
            System.Windows.Input.Key.D1 or System.Windows.Input.Key.NumPad1 => 0,
            System.Windows.Input.Key.D2 or System.Windows.Input.Key.NumPad2 => 1,
            System.Windows.Input.Key.D3 or System.Windows.Input.Key.NumPad3 => 2,
            _ => -1,
        };
        if (mode < 0) return false;
        ViewChoice.SelectedIndex = mode;
        return true;
    }
}
