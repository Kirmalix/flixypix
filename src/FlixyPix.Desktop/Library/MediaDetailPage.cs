using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

/// <summary>What the detail page of a medium shows.</summary>
internal sealed record MediaDetails(MediaItem Item, string Meta, IReadOnlyList<Scene> Scenes, IReadOnlyList<Bookmark> Bookmarks, IReadOnlyList<string> History, IReadOnlyList<Playlist> Collections, IReadOnlyList<MediaItem> Similar);

/// <summary>A page per medium: its picture with a timeline of marks, then scenes, bookmarks, history, collections and
/// similar media. Editing stays with the inspector beside it.</summary>
internal sealed class MediaDetailPage : ScrollViewer
{
    private readonly StackPanel content = new() { Margin = new Thickness(0, 0, 8, 16) };
    private readonly MediaCardContext cards;
    private CancellationTokenSource? picture;
    internal Action<MediaItem, double?>? Play { get; set; }
    internal Action<MediaItem>? OpenOther { get; set; }
    internal Action<MediaItem>? ShowFolder { get; set; }
    internal Action<MediaItem>? FindSimilar { get; set; }
    internal Action<Playlist>? OpenCollection { get; set; }
    internal Action<MediaItem, UIElement>? MenuRequested { get; set; }
    internal Guid? Shown { get; private set; }
    internal int SimilarCount { get; private set; }
    internal int MarkCount { get; private set; }

    internal MediaDetailPage(MediaCardContext context)
    {
        cards = context; Content = content;
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto; HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
    }

    internal void Show(MediaDetails details)
    {
        var item = details.Item;
        if (Shown != item.Id) ScrollToTop();
        Shown = item.Id; content.Children.Clear();
        content.Children.Add(Stage(item));
        var marks = details.Scenes.Select(s => (Seconds: s.StartSeconds, Title: s.Title, Scene: true)).Concat(details.Bookmarks.Select(b => (Seconds: b.PositionSeconds, Title: b.Title, Scene: false))).ToList();
        MarkCount = marks.Count;
        if (item.Kind != MediaKind.Image && item.DurationSeconds is > 0 and var duration) content.Children.Add(Timeline(item, duration, marks));
        content.Children.Add(Actions(item));
        var columns = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        if (details.Scenes.Count > 0) columns.Children.Add(Column(T("SCENES"), details.Scenes.Select(s => MarkRow(item, s.Title, s.StartSeconds))));
        if (details.Bookmarks.Count > 0) columns.Children.Add(Column(T("BOOKMARKS"), details.Bookmarks.Select(b => MarkRow(item, b.Title, b.PositionSeconds))));
        columns.Children.Add(Column(T("HISTORY"), details.History.Select(line => (UIElement)new TextBlock { Text = line, Foreground = SettingsUi.Muted, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 3) })));
        if (details.Collections.Count > 0) columns.Children.Add(Column(T("IN COLLECTIONS"), details.Collections.Select(CollectionButton)));
        columns.Children.Add(Column(T("FILE"), [new TextBlock { Text = item.Path, Foreground = SettingsUi.Muted, FontSize = 12, TextWrapping = TextWrapping.Wrap }]));
        content.Children.Add(columns);
        SimilarCount = details.Similar.Count;
        if (details.Similar.Count > 0)
        {
            content.Children.Add(Heading(T("SIMILAR ITEMS"), new Thickness(0, 18, 0, 8)));
            var row = new WrapPanel();
            foreach (var other in details.Similar) row.Children.Add(SimilarTile(other));
            content.Children.Add(row);
        }
    }

    internal void Release() { picture?.Cancel(); content.Children.Clear(); Shown = null; }

    private static TextBlock Heading(string text, Thickness margin)
    {
        var heading = new TextBlock { Text = text, Margin = margin };
        heading.SetResourceReference(StyleProperty, "Section");
        return heading;
    }

    /// <summary>The large picture; decoded off the UI thread so that opening the page stays quick.</summary>
    private Grid Stage(MediaItem item)
    {
        var stage = new Grid { MaxHeight = 460, Margin = new Thickness(0, 0, 0, 8) };
        stage.SetResourceReference(BackgroundProperty, "MediaBackground");
        RoundedClip.SetRadius(stage, 8); Privacy.SetBlur(stage, true);
        stage.Children.Add(new AspectBox { Ratio = .5625, Child = new Grid() });
        var image = new Image { Stretch = Stretch.Uniform };
        stage.Children.Add(image);
        picture?.Cancel(); picture = new();
        var token = picture.Token; var path = item.ThumbnailPath ?? (item.Kind == MediaKind.Image ? item.Path : null);
        _ = Dispatcher.InvokeAsync(async () =>
        {
            var source = await Task.Run(() => MediaCard.LoadBitmap(path, 1600));
            if (!token.IsCancellationRequested) image.Source = source;
        });
        return stage;
    }

    private Grid Timeline(MediaItem item, double duration, IReadOnlyList<(double Seconds, string Title, bool Scene)> marks)
    {
        var host = new Grid { Height = 16, Margin = new Thickness(0, 0, 0, 10) };
        var track = new Border { Height = 6, CornerRadius = new CornerRadius(4), VerticalAlignment = VerticalAlignment.Center };
        track.SetResourceReference(Border.BackgroundProperty, "TimelineTrack");
        host.Children.Add(track);
        var played = new Border { Height = 6, CornerRadius = new CornerRadius(4), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
        played.SetResourceReference(Border.BackgroundProperty, "Accent");
        host.Children.Add(played);
        var canvas = new Canvas(); host.Children.Add(canvas);
        var buttons = new List<(Button Button, double Share)>();
        foreach (var (seconds, title, scene) in marks)
        {
            var mark = new Button { Width = 4, Height = 16, Padding = new Thickness(0), Margin = new Thickness(0), ToolTip = (string.IsNullOrWhiteSpace(title) ? (scene ? T("Scene") : T("Bookmark")) : title) + " · " + JumpMarks.FormatTime(seconds) };
            mark.SetResourceReference(BackgroundProperty, scene ? "SceneMark" : "TimelineMark");
            System.Windows.Automation.AutomationProperties.SetName(mark, (string)mark.ToolTip);
            mark.Click += (_, _) => Play?.Invoke(item, seconds);
            canvas.Children.Add(mark); buttons.Add((mark, Math.Clamp(seconds / duration, 0, 1)));
        }
        host.SizeChanged += (_, e) =>
        {
            played.Width = e.NewSize.Width * Math.Clamp(item.PlaybackPositionSeconds / duration, 0, 1);
            foreach (var (button, share) in buttons) Canvas.SetLeft(button, Math.Max(0, e.NewSize.Width * share - 2));
        };
        return host;
    }

    private DockPanel Actions(MediaItem item)
    {
        var row = new DockPanel { Margin = new Thickness(0, 2, 0, 12) };
        var tools = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        tools.HorizontalAlignment = HorizontalAlignment.Right;
        Button Tool(string label, Action click, bool primary = false)
        {
            var button = new Button { Content = label, Margin = new Thickness(6, 0, 0, 4) };
            if (primary) button.Style = (Style)Application.Current.Resources["Primary"];
            button.Click += (_, _) => click();
            tools.Children.Add(button);
            return button;
        }
        Tool(T("Show in folder"), () => ShowFolder?.Invoke(item));
        if (item.Kind != MediaKind.Audio) Tool(T("Find similar"), () => FindSimilar?.Invoke(item));
        var more = Tool(T("More"), () => { });
        more.Click += (_, _) => MenuRequested?.Invoke(item, more);
        Tool(item.Kind == MediaKind.Image ? T("View") : T("Play"), () => Play?.Invoke(item, null), primary: true);
        row.Children.Add(tools);
        return row;
    }

    private static StackPanel Column(string title, IEnumerable<UIElement> rows)
    {
        var column = new StackPanel { Width = 300, Margin = new Thickness(0, 0, 24, 14) };
        column.Children.Add(Heading(title, new Thickness(0, 0, 0, 6)));
        foreach (var row in rows) column.Children.Add(row);
        return column;
    }

    private UIElement MarkRow(MediaItem item, string title, double seconds)
    {
        var row = new DockPanel { Width = 280 };
        var time = new TextBlock { Text = JumpMarks.FormatTime(seconds), Foreground = SettingsUi.Muted, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        DockPanel.SetDock(time, Dock.Right); row.Children.Add(time);
        row.Children.Add(new TextBlock { Text = string.IsNullOrWhiteSpace(title) ? JumpMarks.FormatTime(seconds) : title, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
        var button = new Button { Content = row, Style = (Style)Application.Current.Resources["Quiet"], Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(-6, 0, 0, 1) };
        button.SetResourceReference(ForegroundProperty, "Text");
        button.Click += (_, _) => Play?.Invoke(item, seconds);
        return button;
    }

    private UIElement CollectionButton(Playlist playlist)
    {
        var button = new Button { Content = MainWindow.CollectionName(playlist), Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 0, 0, 5), FontSize = 12, HorizontalAlignment = HorizontalAlignment.Left };
        button.Click += (_, _) => OpenCollection?.Invoke(playlist);
        return button;
    }

    private Button SimilarTile(MediaItem item)
    {
        var card = new MediaCard(item, context: cards);
        var stage = new Grid(); stage.SetResourceReference(BackgroundProperty, "MediaBackground");
        RoundedClip.SetRadius(stage, 6); Privacy.SetBlur(stage, true);
        var image = new Image { Stretch = Stretch.Uniform };
        image.SetBinding(Image.SourceProperty, new Binding(nameof(MediaCard.Thumbnail)) { Source = card });
        stage.Children.Add(image);
        var stack = new StackPanel { Width = 200 };
        stack.Children.Add(new AspectBox { Ratio = .5625, Child = stage });
        stack.Children.Add(new TextBlock { Text = card.Title, FontWeight = FontWeights.SemiBold, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(2, 6, 2, 2), ToolTip = item.Path });
        var tile = new Button { Content = stack, Padding = new Thickness(7), Margin = new Thickness(0, 0, 10, 10) };
        tile.SetResourceReference(BackgroundProperty, "CardBackground");
        System.Windows.Automation.AutomationProperties.SetName(tile, card.Title);
        tile.Click += (_, _) => OpenOther?.Invoke(item);
        _ = card.EnsureLoaded();
        return tile;
    }
}
