using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Rectangle = System.Windows.Shapes.Rectangle;
using Shape = System.Windows.Shapes.Shape;
using FlixyPix.Core;
using FlixyPix.MediaTools;
using Microsoft.Win32;

namespace FlixyPix.Desktop;

public partial class MainWindow
{
 /// <summary>Opens the directory of tags, performers and studios; "Medien zeigen" there filters the main window by a tag.</summary>
 /// <param name="start">Runs once the window is shown, such as importing or exporting a tag package from the database management.</param>
 internal void ShowDirectory(TagRole? role = null, Action<DirectoryWindow>? start = null,TagKey? selectKey=null)
 {
  if(!LeaveRoleView())return;
  var window = new DirectoryWindow(this, _repository, role, selectKey?.Value??CurrentTag) { SearchProviders = SearchProviders, PictureCache = Path.Combine(_settings.DataDirectory, "tag-assets"),
   Tools = ToolExists(_settings.Ffmpeg, "ffmpeg") ? _tools : null };
  if(selectKey!=null)window.ContentRendered+=(_,_)=>window.Browser.NavigateHierarchyTag(selectKey);
  if (start != null) window.ContentRendered += (_, _) => start(window);
  window.ShowDialog();
  if(_roleBrowser?.IsVisible==true)_roleBrowser.RefreshDirectory();
  if (window.Changed) Refresh();
  if(window.ShowHierarchyExpression is {} expression)ApplyViewState(new(){View="all",Expression=expression},T("Tag hierarchy"));
  else if (window.ShowMediaKey is { } key) ApplyViewState(new ViewState { View = "all",
   Expression = key.Category.Length == 0 ? null : CatalogRepository.HasCategorizedTag(key), Tag = key.Category.Length == 0 ? key.Value : null }, key.Value);
 }
 private void DirectoryClicked(object sender, RoutedEventArgs e) => ShowDirectory();
}

/// <summary>A row of the directory list; its picture (portrait or logo) is loaded when the row is first shown.</summary>
internal sealed class DirectoryRow(DirectoryEntry entry, Func<DirectoryRow, Task>? loadPicture = null) : INotifyPropertyChanged
{
 private Brush? picture; private bool requested;
 public DirectoryEntry Entry { get; } = entry;
 /// <summary>Portraits of performers are round, logos and tag pictures have rounded corners.</summary>
 public double Radius => Entry.Role == TagRole.Performer ? 15 : 5;
 public string Initials => string.Concat(Entry.Tag.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(w => char.ToUpper(w[0], CultureInfo.CurrentCulture)));
 public Brush? Picture
 {
  get { if (!requested && loadPicture != null) { requested = true; _ = loadPicture(this); } return picture; }
  set { picture = value; PropertyChanged?.Invoke(this, new(nameof(Picture))); }
 }
 internal void ReleasePicture() { requested = false; picture = null; PropertyChanged?.Invoke(this, new(nameof(Picture))); }
 public event PropertyChangedEventHandler? PropertyChanged;
 public string Tag => Entry.Tag;
 public string Category => Entry.Category.Length == 0 ? "–" : Entry.Category;
 public string Role => TagPackages.RoleLabel(Entry.Role);
 public string Count => Entry.MediaCount.ToString("N0", CultureInfo.CurrentCulture);
 public string Aliases => string.Join(", ", Entry.AliasList);
 public string Favorite => Entry.IsFavorite ? "★" : "";
}

/// <summary>"Verzeichnis": every tag with category, role and media count. One entry is edited on the right (name, category,
/// role of the category, aliases, favourite, description); several are merged or deleted together with a preview of what
/// changes. Tag packages (CSV) are imported with a preview and exported from the list.</summary>
internal sealed class DirectoryWindow : Window
{
 internal DirectoryBrowser Browser { get; }
 internal DirectoryWindow(Window owner, CatalogRepository repository, TagRole? role = null, string? select = null)
 {
  Owner = owner; Title = T("Directory \u00b7 FlixyPix"); Width = 1120; Height = 740; MinWidth = 820; MinHeight = 520;
  WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
  Browser = new DirectoryBrowser(this, repository, role, select);
  Browser.ShowMediaRequested += _ => Close();
  Browser.ShowHierarchyRequested += _=>Close();
  Closing += (_, e) => { if (!Browser.ConfirmLeave()) e.Cancel = true; };
  var close = new Button { Content = T("Close"), IsCancel = true, IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(16) };
  close.Click += (_, _) => Close();
  var layout = new DockPanel(); DockPanel.SetDock(close, Dock.Bottom); layout.Children.Add(close); layout.Children.Add(Browser); Content = layout;
 }
 internal string? PictureCache { get => Browser.PictureCache; init => Browser.PictureCache = value; }
 internal SearchExpression? ShowHierarchyExpression=>Browser.ShowHierarchyExpression;
 internal IReadOnlyList<WebSearchProvider> SearchProviders { get => Browser.SearchProviders; init => Browser.SearchProviders = value; }
 internal MediaToolService? Tools { get => Browser.Tools; init => Browser.Tools = value; }
 internal static Func<BitmapSource?> ClipboardImage { get => DirectoryBrowser.ClipboardImage; set => DirectoryBrowser.ClipboardImage = value; }
 internal bool CanUndo => Browser.CanUndo;
 internal bool HasPortrait => Browser.HasPortrait;
 internal int MediaTileCount => Browser.MediaTileCount;
 internal Task ProfileReady => Browser.ProfileReady;
 internal string CategoryText => Browser.CategoryText;
 internal bool Changed => Browser.Changed;
 internal string? ShowMediaOf => Browser.ShowMediaOf;
 internal TagKey? ShowMediaKey => Browser.ShowMediaKey;
 internal int RowCount => Browser.RowCount;
 internal DirectoryEntry? Current => Browser.Current;
 internal TagProfile? ShownProfile => Browser.ShownProfile;
 internal bool PerformerFieldsVisible => Browser.PerformerFieldsVisible;
 internal string AgeText => Browser.AgeText;
 internal bool RowHasPicture(string tag) => Browser.RowHasPicture(tag);
 internal void DeleteWithoutAsking(IReadOnlyList<string> tags) => Browser.DeleteWithoutAsking(tags);
 internal void Undo() => Browser.Undo();
 internal void SetPicture(DirectoryEntry entry, string path) => Browser.SetPicture(entry, path);
 internal bool PastePicture() => Browser.PastePicture();
 internal Task SetStillAsync(DirectoryEntry entry, MediaItem video, double seconds) => Browser.SetStillAsync(entry, video, seconds);
 internal void ImportPackage() => Browser.ImportPackage();
 internal int ImportPackage(string path, string? defaultCategory, bool review) => Browser.ImportPackage(path, defaultCategory, review);
 internal void ExportPackage() => Browser.ExportPackage();
 internal void EnterPerformer(string? birthDate, string? hairColor, string? heightCm, PerformerGender? sex) => Browser.EnterPerformer(birthDate, hairColor, heightCm, sex);
 internal void EnterParentStudio(string studio) => Browser.EnterParentStudio(studio);
 internal void SaveForTest() => Browser.SaveForTest();
}

/// <summary>One row of the package review with its checkbox.</summary>
internal sealed class PackageRowView(TagPackageItem item) : INotifyPropertyChanged
{
 private bool apply = item.CanApply;
 public TagPackageItem Item { get; } = item;
 public bool Apply { get => apply; set { if (apply == value || !Item.CanApply) return; apply = value; PropertyChanged?.Invoke(this, new(nameof(Apply))); } }
 public bool Enabled => Item.CanApply;
 public int Line => Item.Row.Line;
 public string Name => Item.Row.Name;
 public string Category => Item.Category;
 public string Role => TagPackages.RoleLabel(Item.Role);
 public string Status => Item.Status switch { TagPackageStatus.New => T("New"), TagPackageStatus.Exists => T("Present"), TagPackageStatus.Alias => T("Alias"), TagPackageStatus.Conflict => T("Conflict"), _ => T("Invalid") };
 public string Note => Item.Note + (Item.Row.Aliases.Count > 0 ? T(" · Aliases: ") + string.Join(", ", Item.Row.Aliases) : "");
 public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>Review of a tag package before anything is stored: each row with what it would do; conflicts and invalid rows
/// cannot be chosen.</summary>
internal sealed class TagPackageWindow : Window
{
 private readonly List<PackageRowView> rows;
 internal IReadOnlyCollection<int> ChosenLines => rows.Where(r => r.Apply).Select(r => r.Line).ToList();

 internal TagPackageWindow(Window owner, string file, TagPackagePlan plan, IReadOnlyList<string> warnings)
 {
  Owner = owner; Title = T("Check tag package · FlixyPix"); Width = 980; Height = 640; MinWidth = 700; MinHeight = 420; WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
  rows = plan.Items.Select(i => new PackageRowView(i)).ToList();
  var list = MigrationViews.List(); list.ItemContainerStyle = TagAssetsWindow.ArtworkRowStyle(); list.ItemsSource = rows;
  var view = new GridView();
  var check = new FrameworkElementFactory(typeof(CheckBox));
  check.SetBinding(ToggleButton.IsCheckedProperty, new Binding(nameof(PackageRowView.Apply)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
  check.SetBinding(IsEnabledProperty, new Binding(nameof(PackageRowView.Enabled)));
  view.Columns.Add(new GridViewColumn { Header = "", Width = 34, CellTemplate = new DataTemplate { VisualTree = check } });
  foreach (var (label, property, width) in new[] { (T("Row"), nameof(PackageRowView.Line), 56.0), (T("Name"), nameof(PackageRowView.Name), 200.0), (T("Category"), nameof(PackageRowView.Category), 130.0),
   (T("Role"), nameof(PackageRowView.Role), 120.0), (T("Result"), nameof(PackageRowView.Status), 100.0), (T("Note"), nameof(PackageRowView.Note), 320.0) })
   view.Columns.Add(new GridViewColumn { Header = label, DisplayMemberBinding = new Binding(property), Width = width });
  list.View = view;
  var body = new DockPanel();
  if (warnings.Count > 0)
  {
   var notes = SettingsUi.Hint(string.Join("\n", warnings.Take(8)) + (warnings.Count > 8 ? Tn(warnings.Count - 8, "\n… and 1 more note", "\n… and {0:N0} more notes") : ""));
   notes.Foreground = Theme.Brush("Error"); DockPanel.SetDock(notes, Dock.Top); body.Children.Add(notes);
  }
  body.Children.Add(list);
  string Count(TagPackageStatus status) => rows.Count(r => r.Item.Status == status).ToString("N0", CultureInfo.CurrentCulture);
  var frame = new WindowFrame(T("Check tag package"), T("“{0}”: {1} new, {2} present, {3} recognized by alias, ", file, Count(TagPackageStatus.New), Count(TagPackageStatus.Exists), Count(TagPackageStatus.Alias)) +
   Tn(rows.Count(r => r.Item.Status == TagPackageStatus.Conflict),
    "1 conflict, {1} invalid. Only directory entries are imported; media items remain unchanged.",
    "{0} conflicts, {1} invalid. Only directory entries are imported; media items remain unchanged.", Count(TagPackageStatus.Invalid)), body, "",
    scroll: false);
  frame.Add(T("Cancel"), () => DialogResult = false, cancel: true);
  frame.Add(T("Apply"), () => DialogResult = true, primary: true);
  Content = frame;
 }
}
