using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

/// <summary>A performer or studio in a gallery; the picture loads when its tile comes into view.</summary>
internal sealed class GalleryTile(DirectoryEntry entry, Func<GalleryTile, Task> loadPicture) : INotifyPropertyChanged
{
    private Brush? picture; private bool requested;
    public DirectoryEntry Entry { get; } = entry;
    public string Name => Entry.Tag;
    public string Initials => string.Concat(Entry.Tag.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(word => char.ToUpper(word[0], CultureInfo.CurrentCulture)));
    public string CountLabel => Tn(Entry.MediaCount, "1 item", "{0:N0} items");
    public Brush? Picture
    {
        get { if (!requested) { requested = true; _ = loadPicture(this); } return picture; }
        set { picture = value; PropertyChanged?.Invoke(this, new(nameof(Picture))); }
    }
    internal void ReleasePicture() { requested = false; picture = null; PropertyChanged?.Invoke(this, new(nameof(Picture))); }
    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>Portrait tiles of performers or wide tiles of studios, most frequent first. Only tiles in view exist, so large
/// directories stay quick. The list is kept while the page is away, so coming back finds the same place.</summary>
internal sealed class EntityGallery : Grid
{
    private const string Namespaces = "xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:local=\"clr-namespace:FlixyPix.Desktop;assembly=FlixyPix\"";
    private const string PersonTile = "<DataTemplate " + Namespaces + "><Border Background=\"{DynamicResource CardBackground}\" CornerRadius=\"10\" Padding=\"8,12\"><StackPanel>"
     + "<Grid Width=\"112\" Height=\"112\" HorizontalAlignment=\"Center\" Margin=\"0,0,0,8\" local:Privacy.Blur=\"True\"><Ellipse Fill=\"{DynamicResource ChipApplied}\"/>"
     + "<TextBlock Text=\"{Binding Initials}\" FontSize=\"30\" FontWeight=\"SemiBold\" Foreground=\"{DynamicResource TextSoft}\" HorizontalAlignment=\"Center\" VerticalAlignment=\"Center\"/><Ellipse Fill=\"{Binding Picture}\"/></Grid>"
     + "<TextBlock Text=\"{Binding Name}\" FontWeight=\"SemiBold\" TextTrimming=\"CharacterEllipsis\" HorizontalAlignment=\"Center\" ToolTip=\"{Binding Name}\"/>"
     + "<TextBlock Text=\"{Binding CountLabel}\" FontSize=\"12\" Foreground=\"{DynamicResource Muted}\" HorizontalAlignment=\"Center\"/></StackPanel></Border></DataTemplate>";
    private const string StudioTile = "<DataTemplate " + Namespaces + "><Border Background=\"{DynamicResource CardBackground}\" CornerRadius=\"10\" Padding=\"8\"><StackPanel>"
     + "<local:AspectBox Ratio=\"0.5625\"><Grid Background=\"{DynamicResource MediaBackground}\" local:RoundedClip.Radius=\"6\" local:Privacy.Blur=\"True\">"
     + "<TextBlock Text=\"{Binding Initials}\" FontSize=\"30\" FontWeight=\"SemiBold\" Foreground=\"{DynamicResource TextSoft}\" HorizontalAlignment=\"Center\" VerticalAlignment=\"Center\"/><Rectangle Fill=\"{Binding Picture}\"/></Grid></local:AspectBox>"
     + "<TextBlock Text=\"{Binding Name}\" FontWeight=\"SemiBold\" TextTrimming=\"CharacterEllipsis\" Margin=\"2,8,2,0\" ToolTip=\"{Binding Name}\"/>"
     + "<TextBlock Text=\"{Binding CountLabel}\" FontSize=\"12\" Foreground=\"{DynamicResource Muted}\" Margin=\"2,0\"/></StackPanel></Border></DataTemplate>";

    private readonly ListBox list = new();
    private readonly TextBlock none = new() { Text = T("Nothing found."), Foreground = SettingsUi.Muted, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
    private readonly Queue<GalleryTile> pictured = new();
    private string shown = "\0";
    internal Action<DirectoryEntry>? Open { get; set; }
    internal Action<DirectoryEntry, UIElement>? MenuRequested { get; set; }
    internal IReadOnlyList<GalleryTile> Tiles => list.ItemsSource as IReadOnlyList<GalleryTile> ?? [];

    internal EntityGallery(bool people)
    {
        list.ItemTemplate = (DataTemplate)XamlReader.Parse(people ? PersonTile : StudioTile);
        list.ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(VirtualizingTilePanel)));
        VirtualizingTilePanel.SetTargetTileWidth(list, people ? 156 : 248);
        System.Windows.Automation.AutomationProperties.SetName(list, people ? T("Performers") : T("Studios"));
        list.ItemContainerStyle = new Style(typeof(ListBoxItem), (Style)Application.Current.FindResource(typeof(ListBoxItem)));
        list.ItemContainerStyle.Setters.Add(new Setter(Motion.HoverLiftProperty, true));
        list.ItemContainerStyle.Setters.Add(new Setter(CursorProperty, Cursors.Hand));
        list.PreviewMouseLeftButtonUp += (_, e) => { if (TileAt(e.OriginalSource) is { } tile) { e.Handled = true; Open?.Invoke(tile.Entry); } };
        list.MouseRightButtonUp += (_, e) => { if (TileAt(e.OriginalSource) is { } tile) { e.Handled = true; MenuRequested?.Invoke(tile.Entry, list); } };
        list.KeyDown += (_, e) => { if (e.Key == Key.Enter && list.SelectedItem is GalleryTile tile) { e.Handled = true; Open?.Invoke(tile.Entry); } };
        Children.Add(list); Children.Add(none);
    }

    private static GalleryTile? TileAt(object source)
    {
        for (var element = source as DependencyObject; element != null; element = VisualTreeHelper.GetParent(element))
            if (element is ListBoxItem { DataContext: GalleryTile tile }) return tile;
        return null;
    }

    /// <summary>Unchanged content keeps its tiles, pictures and scroll place.</summary>
    internal void Show(IReadOnlyList<DirectoryEntry> entries, Func<GalleryTile, Task> loadPicture)
    {
        var content = string.Join('\n', entries.Select(e => e.Category + "\t" + e.Tag + "\t" + e.MediaCount));
        if (content == shown) return;
        shown = content; pictured.Clear();
        var tiles = entries.Select(e => new GalleryTile(e, loadPicture)).ToList();
        VirtualizingTilePanel.SetTotalCount(list, tiles.Count);
        list.ItemsSource = tiles;
        none.Visibility = tiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Called when a picture arrived; old pictures leave again, so a long way through the gallery stays light.</summary>
    internal void Pictured(GalleryTile tile)
    {
        pictured.Enqueue(tile);
        while (pictured.Count > 256) pictured.Dequeue().ReleasePicture();
    }

    /// <summary>A changed picture shows on the next visit.</summary>
    internal void Forget() => shown = "\0";
}
