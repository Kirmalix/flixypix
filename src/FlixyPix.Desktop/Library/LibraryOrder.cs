using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

/// <summary>"Ordnen": what is left to do as queues to work through one medium at a time, and the tools for maintaining the
/// library. A queue is an ordinary list: what is done stays in it marked as done, so the progress can be seen.</summary>
public partial class MainWindow
{
    private ScrollViewer? _orderPage;
    private WrapPanel? _queueCards;
    internal IReadOnlyDictionary<string, long> QueueCounts { get; private set; } = new Dictionary<string, long>();

    private sealed record WorkQueue(string Key, string Label, string Hint, int Glyph, Func<ViewState>? List, bool TagMode, Action? Open = null);

    /// <summary>A queue is a list in the chosen media kind; the kind stays what the queues show until it is changed.</summary>
    private ViewState QueueView(MediaFilter filter, IReadOnlyList<ReviewState>? review = null) => new() { View = QueueKindView, Filter = filter, ReviewStates = [.. review ?? []], SortOrder = Math.Max(0, SortChoice.SelectedIndex), ViewMode = ViewChoice.SelectedIndex, TileWidth = (int)Math.Round(TileSize.Value) };

    private IReadOnlyList<WorkQueue> WorkQueues() =>
    [
        new("inbox", T("Review inbox"), T("New items to look through and approve."), 0xE9D5, () => QueueView(new(), [ReviewState.Unreviewed, ReviewState.NeedsAttention]), true),
        new("untagged", T("Without tags"), T("Tag them one by one in tag mode."), 0xE8EC, () => QueueView(new() { UntaggedOnly = true }), true),
        new("unrated", T("Unrated"), T("Rate with 1–5; the next item follows by itself."), 0xE734, () => QueueView(new() { UnratedOnly = true }), false),
        .. QueueKindView == "Image" ? Array.Empty<WorkQueue>() : [new WorkQueue("thumbnails", T("Without thumbnail"), T("Create the missing thumbnails."), 0xE91B, () => QueueView(new() { HasThumbnail = false }), false)],
        new("duplicates", T("Find duplicates"), T("Compare pairs and decide what stays."), 0xE8C8, null, false, () => ShowDuplicates()),
        .. QueueKindView is "Video" or "Audio" ? Array.Empty<WorkQueue>() : [new WorkQueue("sets", T("Suggest image sets"), T("Confirm images that belong together as a set."), 0xE8B9, null, false, () => _ = SuggestImageSetsAsync())],
    ];

    /// <summary>How much each queue holds, counted off the UI thread; the queues that need no counting have no entry.</summary>
    private Task<Dictionary<string, long>> ReadQueueCountsAsync(CancellationToken token)
    {
        var lists = WorkQueues().Where(queue => queue.List != null).Select(queue => (queue.Key, Query: queue.List!().ToQuery(DateTimeOffset.Now) with { Limit = 1 })).ToList();
        return Task.Run(() => lists.ToDictionary(list => list.Key, list => _repository.QueryPage(list.Query, token).TotalCount), token);
    }

    private string QueueKindView => _settings.QueueKind is "Video" or "Image" or "Audio" ? _settings.QueueKind : "all";
    private readonly List<RadioButton> _queueKindChoices = [];

    /// <summary>The media kind as a row of choices, on the page and above a running queue.</summary>
    private void FillQueueKinds(Panel panel, string group)
    {
        foreach (var kind in new[] { "all", "Video", "Image", "Audio" })
        {
            var choice = new RadioButton { Content = ViewName(kind), Tag = kind, GroupName = group, Style = (Style)FindResource("Segment"), Padding = new Thickness(10, 4, 10, 4), IsChecked = QueueKindView == kind };
            choice.Click += (_, _) => ChooseQueueKind(kind);
            panel.Children.Add(choice); _queueKindChoices.Add(choice);
        }
    }

    private void ChooseQueueKind(string kind)
    {
        _settings = _settings with { QueueKind = kind };
        SaveViewPreferences();
        SyncQueueKinds();
        // In a running queue the list follows; the page counts again.
        if (_view == "order") Refresh(); else ShowKind(kind);
    }

    /// <summary>The choices say what is shown: above a list its view, on the page the kind the queues use.</summary>
    private void SyncQueueKinds()
    {
        var shown = _view is "Video" or "Image" or "Audio" or "all" ? _view : QueueKindView;
        var audio = _repository.GetNavigationCounts().Audio > 0;
        foreach (var choice in _queueKindChoices)
        {
            choice.IsChecked = (string)choice.Tag == (choice.GroupName == "queueKindPage" ? QueueKindView : shown);
            if ((string)choice.Tag == "Audio") choice.Visibility = audio ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private ScrollViewer OrderPage()
    {
        if (_orderPage != null) return _orderPage;
        var body = new StackPanel { Margin = new Thickness(0, 0, 8, 16) };
        var kinds = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 14) };
        FillQueueKinds(kinds, "queueKindPage");
        body.Children.Add(kinds);
        TextBlock Heading(string text) { var heading = new TextBlock { Text = text, FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) }; return heading; }
        body.Children.Add(Heading(T("Queues")));
        _queueCards = new WrapPanel { Margin = new Thickness(0, 0, 0, 22) };
        body.Children.Add(_queueCards);
        body.Children.Add(Heading(T("Maintenance")));
        var tools = new WrapPanel();
        void Tool(string label, Action click) { var button = new Button { Content = label, Margin = new Thickness(0, 0, 8, 8) }; button.Click += (_, _) => click(); tools.Children.Add(button); }
        Tool(T("Performers"), () => ApplyViewState(new ViewState { View = "performers" }));
        Tool(T("Studios"), () => ApplyViewState(new ViewState { View = "studios" }));
        Tool(T("Directory: tags, performers, studios …"), () => ShowDirectory());
        Tool(T("Extend tag vocabulary …"), AddVocabularyTag);
        Tool(T("Apply tags from rules"), ApplyTagRules);
        Tool(T("Create file name parser …"), NewFilenameParser);
        Tool(T("Advanced search …"), AdvancedSearch);
        Tool(T("Read technical data"), () => ReadTechnicalData());
        Tool(T("Statistics"), () => ShowStatistics());
        var all = new Button { Content = T("All tools"), Margin = new Thickness(0, 0, 8, 8) };
        all.Click += (sender, e) => OrganizeMenu(sender, e);
        tools.Children.Add(all);
        body.Children.Add(tools);
        return _orderPage = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }

    private async Task RefreshOrderAsync(BrowseRequest request, CancellationToken token)
    {
        ShowPage(OrderPage());
        FinishPage(ViewName(_view));
        var counts = await ReadQueueCountsAsync(token);
        if (_closing || !_browse.IsCurrent(request)) return;
        QueueCounts = counts;
        SyncQueueKinds();
        _queueCards!.Children.Clear();
        foreach (var queue in WorkQueues()) _queueCards.Children.Add(QueueCard(queue));
        var open = counts.Values.Sum();
        CountLabel.Text = Tn(open, "1 item to work through", "{0:N0} items to work through");
    }

    private Button QueueCard(WorkQueue queue)
    {
        var body = new DockPanel { Width = 264 };
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        DockPanel.SetDock(head, Dock.Top);
        var glyph = new TextBlock { Text = ((char)queue.Glyph).ToString(), FontFamily = SettingsUi.Icons, FontSize = 16, Foreground = SettingsUi.Muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        DockPanel.SetDock(glyph, Dock.Left); head.Children.Add(glyph);
        if (QueueCounts.TryGetValue(queue.Key, out var count))
        {
            var number = new TextBlock { Text = count.ToString("N0", CultureInfo.CurrentCulture), FontSize = 16, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), Typography = { NumeralAlignment = FontNumeralAlignment.Tabular } };
            DockPanel.SetDock(number, Dock.Right); head.Children.Add(number);
        }
        head.Children.Add(new TextBlock { Text = queue.Label, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
        body.Children.Add(head);
        body.Children.Add(new TextBlock { Text = queue.Hint, Foreground = SettingsUi.Muted, FontSize = 12, TextWrapping = TextWrapping.Wrap, MinHeight = 34 });
        var card = new Button { Content = body, Padding = new Thickness(14, 12, 14, 12), Margin = new Thickness(0, 0, 10, 10), Tag = queue.Key };
        card.SetResourceReference(BackgroundProperty, "CardBackground");
        System.Windows.Automation.AutomationProperties.SetName(card, queue.Label);
        card.Click += (_, _) => _ = StartQueueAsync(queue);
        return card;
    }

    /// <summary>Opens the list at its first medium; queues for tagging go straight into tag mode.</summary>
    private async Task StartQueueAsync(WorkQueue queue)
    {
        if (queue.Open is { } open) { open(); return; }
        ApplyViewState(queue.List!());
        await _lastRefresh;
        if (_closing || _total == 0) return;
        await SelectIndexAsync(0);
        if (queue.TagMode) SetTagWork(true);
    }

    /// <summary>What is left to do, as a short strip on the start page; empty queues stay away.</summary>
    private async Task RefreshHomeTasksAsync(CancellationToken token)
    {
        var counts = await ReadQueueCountsAsync(token);
        if (_closing || _view != "home") return;
        QueueCounts = counts;
        _home.ShowTasks(WorkQueues().Where(q => counts.GetValueOrDefault(q.Key) > 0 && q.Key != "thumbnails").Select(q => (q.Label, counts[q.Key], (Action)(() => _ = NavigateAreaAsync("order")))).ToList());
    }
}
