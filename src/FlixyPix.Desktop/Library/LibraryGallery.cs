using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

/// <summary>Performers and studios as galleries, with a page of its own per entry: the media of the entry under its picture.
/// The table for maintaining entries stays the directory (views "performers" and "studios").</summary>
public partial class MainWindow
{
    private readonly Dictionary<string, EntityGallery> _galleries = [];
    private readonly SemaphoreSlim _pictureSlots = new(4);
    private IReadOnlyDictionary<TagKey, Guid> _galleryPictures = new Dictionary<TagKey, Guid>();
    private (string Category, string Tag)? _entityShown;

    private bool IsGalleryView => _browse.Kind == LibraryViewKind.Gallery;
    private bool PeopleGallery => _view == "gallery-performers";
    internal EntityGallery? CurrentGallery => _galleries.GetValueOrDefault(_view);
    private bool CurrentGalleryVisible => _galleries.Values.Any(g => g.IsVisible);

    /// <summary>The performer or studio whose page this is: a tag of that role, opened on its own.</summary>
    private TagRole? EntityRole => _tag != null && _tagGroup != null && _playlist == null && _categoryRoles.TryGetValue(_tagGroup, out var role) && role is TagRole.Performer or TagRole.Studio ? role : null;

    private async Task RefreshGalleryAsync(BrowseRequest request, CancellationToken token)
    {
        var role = PeopleGallery ? TagRole.Performer : TagRole.Studio;
        var read = await Task.Run(() => (Entries: _repository.GetDirectory(token), Pictures: _repository.GetTagPictureIds()), token);
        if (_closing || !_browse.IsCurrent(request)) return;
        _galleryPictures = read.Pictures;
        var all = read.Entries.Where(e => e.Role == role).ToList();
        var text = SearchBox.Text.Trim();
        var shown = all.Where(e => e.MediaCount > 0 && (text.Length == 0 || e.Tag.Contains(text, StringComparison.CurrentCultureIgnoreCase) || e.AliasList.Any(a => a.Contains(text, StringComparison.CurrentCultureIgnoreCase))))
         .OrderByDescending(e => e.MediaCount).ThenBy(e => e.Tag, StringComparer.CurrentCultureIgnoreCase).ToList();
        var gallery = GalleryFor(_view);
        ShowPage(gallery);
        gallery.Show(shown, tile => LoadGalleryPictureAsync(tile, gallery));
        FinishPage(ViewName(_view), T("{0:N0} of {1:N0} entries", shown.Count, all.Count));
    }

    private EntityGallery GalleryFor(string view)
    {
        if (_galleries.TryGetValue(view, out var gallery)) return gallery;
        gallery = new EntityGallery(view == "gallery-performers") { Open = OpenEntity, MenuRequested = (entry, target) => Menus.Open(target, EntityMenu(entry), MenuFailed, System.Windows.Controls.Primitives.PlacementMode.MousePoint), Visibility = Visibility.Collapsed };
        return _galleries[view] = gallery;
    }

    private IEnumerable<MenuSpec> EntityMenu(DirectoryEntry entry) =>
    [
        MenuSpec.Command(T("Open"), () => OpenEntity(entry)),
        MenuSpec.Command(T("Edit …"), () => OpenInDirectory(entry.Role, entry.Category, entry.Tag)),
    ];

    internal void OpenEntity(DirectoryEntry entry)
    {
        // A page of its own starts without the selection of the page before.
        _offWindowSelection = []; MediaList.SelectedItems.Clear();
        ApplyViewState(new ViewState { View = "all", Tag = entry.Tag, TagCategory = entry.Category, SortOrder = 4, ViewMode = ViewChoice.SelectedIndex, TileWidth = (int)Math.Round(TileSize.Value) }, entry.Tag);
    }

    private void OpenInDirectory(TagRole role, string category, string tag) =>
        ApplyViewState(new ViewState { View = role == TagRole.Performer ? "performers" : "studios", Directory = new(new(category, tag)) });

    private void GalleryDirectoryClicked(object sender, RoutedEventArgs e) => ApplyViewState(new ViewState { View = PeopleGallery ? "performers" : "studios" });

    private void EntityEditClicked(object sender, RoutedEventArgs e)
    {
        if (EntityRole is { } role) OpenInDirectory(role, _tagGroup!, _tag!);
    }

    private async Task LoadGalleryPictureAsync(GalleryTile tile, EntityGallery gallery)
    {
        var picture = await EntityPictureAsync(tile.Entry.Category, tile.Entry.Tag, 224, _libraryTasks.Token);
        if (_closing || picture == null) return;
        tile.Picture = picture; gallery.Pictured(tile);
    }

    /// <summary>The picture of the entry; without one the thumbnail of its best rated medium stands in. Null when neither can be read.</summary>
    private async Task<Brush?> EntityPictureAsync(string category, string tag, int size, CancellationToken token)
    {
        var entered = false;
        try
        {
            await _pictureSlots.WaitAsync(token); entered = true;
            var key = new TagKey(category, tag);
            ImageSource? source = null;
            if (_galleryPictures.TryGetValue(key, out var id))
            {
                var cache = Path.Combine(_settings.DataDirectory, "tag-assets");
                var path = await Task.Run(() => _repository.GetTagAsset(id) is { IsOpaque: false } asset ? _repository.MaterializeTagAsset(asset, cache) : null, token);
                if (path != null) source = await DesktopImaging.LoadBitmapAsync(path, size, token);
            }
            source ??= await Task.Run(() =>
            {
                var cover = _repository.QueryPage(new MediaQuery { Expression = CatalogRepository.HasCategorizedTag(key), Sort = MediaSort.Rating, Descending = true, Limit = 1 }, token).Items.FirstOrDefault();
                return MediaCard.LoadBitmap(cover?.ThumbnailPath ?? (cover?.Kind == MediaKind.Image ? cover.Path : null), size);
            }, token);
            if (source == null) return null;
            var brush = new ImageBrush(source) { Stretch = Stretch.UniformToFill }; brush.Freeze();
            return brush;
        }
        catch (Exception error) when (error is OperationCanceledException or IOException or InvalidDataException or InvalidOperationException or ArgumentException or UnauthorizedAccessException or NotSupportedException or ObjectDisposedException) { return null; }
        finally { if (entered) _pictureSlots.Release(); }
    }

    /// <summary>The head of an entry's page: its picture beside the title and the way to its entry.</summary>
    private void RenderEntityHead()
    {
        var show = EntityRole != null && ShowsMediaGrid;
        EntityEditButton.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        EntityPortrait.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        GalleryDirectoryButton.Visibility = IsGalleryView ? Visibility.Visible : Visibility.Collapsed;
        if (!show) { _entityShown = null; return; }
        var current = (_tagGroup!, _tag!);
        if (_entityShown == current) return;
        _entityShown = current;
        EntityPortrait.Fill = Theme.Brush("ChipApplied");
        _ = _libraryTasks.Run(async token =>
        {
            _galleryPictures = await Task.Run(() => _repository.GetTagPictureIds(), token);
            var picture = await EntityPictureAsync(current.Item1, current.Item2, 128, token);
            if (picture != null && _entityShown == current) EntityPortrait.Fill = picture;
        });
    }
}
