using System.Windows;
using System.Windows.Controls;
using FlixyPix.Core;
namespace FlixyPix.Desktop;
public partial class MainWindow
{
 private DirectoryBrowser? _roleBrowser;
 private string? _roleBrowserView;
 private DirectoryViewState? _pendingDirectoryState;
 private readonly Dictionary<UIElement, Visibility> _roleCovered = [];
 private bool IsRoleView => _browse.Kind is LibraryViewKind.Performers or LibraryViewKind.Studios;
 private bool LeaveRoleView() => _roleBrowser?.IsVisible != true || _roleBrowser.ConfirmLeave();
 private void HideRoleView()
 {
  if (_roleBrowser == null) return;
  _roleBrowser.Visibility = Visibility.Collapsed;
  foreach (var (element, visibility) in _roleCovered) element.Visibility = visibility;
  _roleCovered.Clear();
 }
 private void RefreshRoleView(DirectorySnapshot? initial = null)
 {
  RememberDetailsDraft();
  if (_roleBrowserView != _view || _roleBrowser == null)
  {
   if (_roleBrowser != null) MediaPane.Children.Remove(_roleBrowser);
   _roleBrowser = new DirectoryBrowser(this, _repository, _view == "performers" ? TagRole.Performer : TagRole.Studio, initial: initial)
   { PictureCache = System.IO.Path.Combine(_settings.DataDirectory, "tag-assets"), SearchProviders = SearchProviders,
     Tools = ToolExists(_settings.Ffmpeg, "ffmpeg") ? _tools : null };
   _roleBrowser.UseMainSearch(); _roleBrowserView = _view;
   _roleBrowser.ShowMediaRequested += key => ApplyViewState(new ViewState { View = "all", Expression = key.Category.Length == 0 ? null : CatalogRepository.HasCategorizedTag(key), Tag = key.Category.Length == 0 ? key.Value : null }, key.Value);
   _roleBrowser.ShowHierarchyRequested+=expression=>ApplyViewState(new(){View="all",Expression=expression},T("Tag hierarchy"));
   _roleBrowser.HierarchyTagRequested+=OpenHierarchyEntry;
   Grid.SetRow(_roleBrowser, 1); Grid.SetRowSpan(_roleBrowser, 4); Panel.SetZIndex(_roleBrowser, 20); MediaPane.Children.Add(_roleBrowser);
  }
  foreach (UIElement element in MediaPane.Children)
   if (element != _roleBrowser && Grid.GetRow(element) >= 1) { _roleCovered.TryAdd(element, element.Visibility); element.Visibility = Visibility.Collapsed; }
  _home.Visibility = Visibility.Collapsed;
  _roleBrowser.Visibility = Visibility.Visible;
  _roleBrowser.SearchText = SearchBox.Text;
  if (SearchBox.Text != _roleBrowser.SearchText) { SearchBox.Text = _roleBrowser.SearchText; _searchTimer.Stop(); }
  if (_pendingDirectoryState != null) { _roleBrowser.RestoreDirectoryState(_pendingDirectoryState); _pendingDirectoryState = null; }
  AdaptToWidth(); UpdateNavigation(); ViewTitle.Text = ViewName(_view);
  CountLabel.Text = T("{0:N0} of {1:N0} entries", _roleBrowser.RowCount, _roleBrowser.TotalEntries);
  RecordView();
 }
 private void OpenHierarchyEntry(TagKey key)
 {
  var entry=_repository.GetDirectory().FirstOrDefault(e=>TagParentsWindow.KeyComparer.Instance.Equals(new(e.Category,e.Tag),key));
  if(entry==null){Status.Text=T("The tag is not in the directory.");return;}
  if(entry.Role is TagRole.Performer or TagRole.Studio)ApplyViewState(new(){View=entry.Role==TagRole.Performer?"performers":"studios",Directory=new(new(entry.Category,entry.Tag))});
  else ShowDirectory(selectKey:new(entry.Category,entry.Tag));
 }
}
