using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace FlixyPix.Desktop;

/// <summary>Pages on top of endless scrolling: a page is one screenful (visible rows × columns), so going to a page puts
/// its first medium at the top. The bar under the tiles shows where one is and jumps to any page (click the number or Ctrl+G).</summary>
public partial class MainWindow
{
 private (int Page, int Pages, int Size) _pager = (-1, -1, -1);

 /// <summary>Media per page: whole rows that fit into the view, at least one row.</summary>
 internal int PageSize
 {
  get
  {
   if (Tiles is not { } panel || panel.ViewportHeight <= 0) return Math.Max(1, _visibleLast - _visibleFirst + 1);
   var rowHeight = panel.ExtentHeight > 0 && _total > 0 ? panel.ExtentHeight / Math.Ceiling(_total / (double)panel.Columns) : 1;
   return Math.Max(1, (int)(panel.ViewportHeight / Math.Max(1, rowHeight))) * panel.Columns;
  }
 }

 /// <summary>At the end of the result the last page cannot reach the top; once the last medium is in view, it is the current page.</summary>
 internal int CurrentPage => _total == 0 ? 0 : _visibleLast >= _total - 1 ? PageCount : Math.Max(0, _visibleFirst) / PageSize + 1;
 internal int PageCount => _total == 0 ? 0 : (_total + PageSize - 1) / PageSize;

 internal async Task GoToPageAsync(int page)
 {
  if (_total == 0) return;
  page = Math.Clamp(page, 1, PageCount);
  await ScrollToAsync((page - 1) * PageSize);
  UpdatePager();
 }

 /// <summary>Called after layout and loading; rebuilds the buttons only when page, page count or page size changed.</summary>
 private void UpdatePager()
 {
  var state = (CurrentPage, PageCount, PageSize);
  PagerBar.Visibility = state.Item2 > 1 && MediaCard.Display.ShowPager ? Visibility.Visible : Visibility.Collapsed;
  if (state == _pager || state.Item2 <= 1) { _pager = state; return; }
  _pager = state;
  var (page, pages, size) = state;
  var first = (page - 1) * size + 1; var last = Math.Min(_total, page * size);
  PageLabel.Text = string.Format(CultureInfo.CurrentCulture, T("{0:N0}–{1:N0} of {2:N0}"), first, last, _total);
  PagerButtons.Children.Clear();
  PagerButtons.Children.Add(PagerButton("", page - 1, page > 1, T("Previous page (Page↑)"), icon: true));
  foreach (var number in PageNumbers(page, pages))
  {
   if (number == 0) { PagerButtons.Children.Add(new TextBlock { Text = "…", Margin = new Thickness(4, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = Theme.Brush("Muted") }); continue; }
   PagerButtons.Children.Add(number == page ? CurrentPageButton(page, pages) : PagerButton(number.ToString("N0", CultureInfo.CurrentCulture), number, true, T("Page {0:N0}", number)));
  }
  PagerButtons.Children.Add(PagerButton("", page + 1, page < pages, T("Next page (Page↓)"), icon: true));
 }

 /// <summary>First, last and the neighbours of the current page; 0 stands for a gap.</summary>
 internal static IReadOnlyList<int> PageNumbers(int page, int pages)
 {
  var shown = new SortedSet<int> { 1, pages };
  for (var number = page - 1; number <= page + 1; number++) if (number >= 1 && number <= pages) shown.Add(number);
  var result = new List<int>();
  foreach (var number in shown)
  {
   if (result.Count > 0 && number - result[^1] == 2) result.Add(number - 1);
   else if (result.Count > 0 && number - result[^1] > 2) result.Add(0);
   result.Add(number);
  }
  return result;
 }

 private Button PagerButton(string text, int target, bool enabled, string tip, bool icon = false)
 {
  var button = new Button { Content = text, IsEnabled = enabled, ToolTip = tip, MinWidth = 30, Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(2, 0, 2, 0),
   Style = (Style)FindResource("IconButton"), FontSize = icon ? 11 : 12 };
  if (!icon) button.FontFamily = FontFamily;
  System.Windows.Automation.AutomationProperties.SetName(button, tip);
  button.Click += async (_, _) => await GoToPageAsync(target);
  return button;
 }

 /// <summary>The current page as a field: typing a number and Enter jumps there.</summary>
 private TextBox CurrentPageButton(int page, int pages)
 {
  var box = new TextBox { Text = page.ToString(CultureInfo.CurrentCulture), Width = Math.Max(44, 12 + 9 * pages.ToString(CultureInfo.CurrentCulture).Length), Margin = new Thickness(2, 0, 2, 0),
   HorizontalContentAlignment = HorizontalAlignment.Center, ToolTip = T("Page {0:N0} of {1:N0} · type a number and press Enter (Ctrl+G)", page, pages) };
  box.Name = "PageInput";
  System.Windows.Automation.AutomationProperties.SetName(box, T("Page"));
  box.GotKeyboardFocus += (_, _) => box.SelectAll();
  box.PreviewKeyDown += async (_, e) =>
  {
   if (e.Key == Key.Escape) { e.Handled = true; _pager = (-1, -1, -1); UpdatePager(); MediaList.Focus(); }
   if (e.Key != Key.Enter) return;
   e.Handled = true;
   if (int.TryParse(box.Text.Replace(".", "", StringComparison.Ordinal), NumberStyles.Integer, CultureInfo.CurrentCulture, out var target)) await GoToPageAsync(target);
   MediaList.Focus();
  };
  return box;
 }

 /// <summary>Ctrl+G: put the cursor into the page field.</summary>
 private void FocusPageInput()
 {
  if (PagerBar.Visibility != Visibility.Visible) return;
  if (PagerButtons.Children.OfType<TextBox>().FirstOrDefault() is { } box) { box.Focus(); box.SelectAll(); }
 }
}
