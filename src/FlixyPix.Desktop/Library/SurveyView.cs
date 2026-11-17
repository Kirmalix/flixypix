using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

/// <summary>The survey: the chosen images side by side in place of the tiles, each as large as the room allows. One of
/// them is selected and stands for the selection of the library; X takes it out of the survey, not out of the library,
/// and a double click or Enter shows it in the loupe.</summary>
internal sealed class SurveyView : ContentControl
{
 private sealed class Cell(MediaItem item)
 {
  public MediaItem Item { get; set; } = item;
  public Border Frame { get; } = new() { BorderThickness=new Thickness(2), CornerRadius=new CornerRadius(4), Margin=new Thickness(5), Background=Brushes.Transparent };
  public Image Picture { get; } = new() { Stretch=Stretch.Uniform, Margin=new Thickness(4) };
  public TextBlock Caption { get; } = new() { Foreground=Brushes.White, FontSize=12, TextTrimming=TextTrimming.CharacterEllipsis };
  public bool Loaded { get; set; }
 }

 private readonly List<Cell> _cells = [];
 private readonly UniformGrid _grid = new() { Margin=new Thickness(6) };
 private readonly TextBlock _heading = new() { VerticalAlignment=VerticalAlignment.Center, FontSize=13, Margin=new Thickness(8,0,12,0) };
 private readonly Func<MediaItem,CancellationToken,Task<string?>> _pathOf;
 private readonly CancellationTokenSource _closing = new();
 private int _selected;
 private bool _closed;
 private Size _laidOut;
 /// <summary>Another image was selected.</summary>
 internal event Action<MediaItem>? SelectedChanged;
 /// <summary>A double click or Enter: the image is to be shown in the loupe.</summary>
 internal event Action<MediaItem>? OpenRequested;
 internal event EventHandler? Closed;
 internal IReadOnlyList<MediaItem> Items => _cells.Select(c=>c.Item).ToArray();
 internal MediaItem? SelectedItem => _selected>=0 && _selected<_cells.Count ? _cells[_selected].Item : null;
 internal int Columns => _grid.Columns;
 /// <summary>All pictures read, or given up.</summary>
 internal Task ReadyTask { get; private set; } = Task.CompletedTask;
 internal int ShownPictures => _cells.Count(c=>c.Picture.Source!=null);

 /// <param name="pathOf">The file that shows an image, taken out of its archive when needed; null when it cannot be shown.</param>
 internal SurveyView(IReadOnlyList<MediaItem> items,Func<MediaItem,CancellationToken,Task<string?>> pathOf)
 {
  _pathOf=pathOf; Focusable=true; IsTabStop=true; FocusVisualStyle=null;
  HorizontalContentAlignment=HorizontalAlignment.Stretch; VerticalContentAlignment=VerticalAlignment.Stretch;
  var back=new Button { Content=((char)0xE72B).ToString(), Style=(Style)Application.Current.Resources["IconButton"], ToolTip=T("Back to the tiles (G)"), Margin=new Thickness(1,0,1,0) };
  AutomationProperties.SetName(back,T("Back to the tiles (G)")); back.Click+=(_,_)=>Close();
  var hint=new TextBlock { Text=T("Double-click or Enter shows an image in the loupe · X takes it out of the survey · 0–5 rate"), VerticalAlignment=VerticalAlignment.Center,
   HorizontalAlignment=HorizontalAlignment.Right, FontSize=12, Margin=new Thickness(12,0,12,0), TextTrimming=TextTrimming.CharacterEllipsis };
  hint.SetResourceReference(TextBlock.ForegroundProperty,"Muted");
  var toolbar=new DockPanel { Margin=new Thickness(8,6,8,6) };
  var left=new StackPanel { Orientation=Orientation.Horizontal, Children={ back, _heading } };
  DockPanel.SetDock(left,Dock.Left); toolbar.Children.Add(left); toolbar.Children.Add(hint);
  var stage=new Border { Background=Brushes.Black, Child=_grid };
  var layout=new DockPanel(); DockPanel.SetDock(toolbar,Dock.Top); layout.Children.Add(toolbar); layout.Children.Add(stage); Content=layout;
  foreach(var item in items) _cells.Add(Build(item));
  stage.SizeChanged+=(_,e)=>Arrange(e.NewSize);
  Loaded+=(_,_)=>{ if(_cells.Count>0) Select(0); };
  Show();
 }

 private Cell Build(MediaItem item)
 {
  var cell=new Cell(item); cell.Caption.Text=item.Title;
  var caption=new Border { Background=new SolidColorBrush(Color.FromArgb(170,10,12,16)), CornerRadius=new CornerRadius(4), Padding=new Thickness(8,3,8,4), Margin=new Thickness(8),
   HorizontalAlignment=HorizontalAlignment.Left, VerticalAlignment=VerticalAlignment.Bottom, IsHitTestVisible=false, Child=cell.Caption };
  var remove=new Button { Content=((char)0xE711).ToString(), Style=(Style)Application.Current.Resources["IconButton"], ToolTip=T("Take out of the survey (X)"),
   HorizontalAlignment=HorizontalAlignment.Right, VerticalAlignment=VerticalAlignment.Top, Margin=new Thickness(6), Background=new SolidColorBrush(Color.FromArgb(170,10,12,16)), Foreground=Brushes.White };
  AutomationProperties.SetName(remove,T("Take out of the survey (X)")); remove.Click+=(_,_)=>Remove(cell.Item.Id);
  cell.Frame.Child=new Grid { Children={ cell.Picture, caption, remove } };
  cell.Frame.MouseLeftButtonDown+=(_,e)=>
  {
   if(e.OriginalSource is DependencyObject source && IsInside<ButtonBase>(source)) return;
   e.Handled=true; Focus(); Select(_cells.IndexOf(cell));
   if(e.ClickCount==2) OpenRequested?.Invoke(cell.Item);
  };
  return cell;
 }
 private static bool IsInside<TElement>(DependencyObject? element) where TElement : DependencyObject
 {
  for(var current=element;current!=null;current=current is Visual?VisualTreeHelper.GetParent(current):LogicalTreeHelper.GetParent(current)) if(current is TElement) return true;
  return false;
 }

 /// <summary>Lays the cells out and reads what is not read yet.</summary>
 private void Show()
 {
  _grid.Children.Clear(); foreach(var cell in _cells) _grid.Children.Add(cell.Frame);
  _heading.Text=T("Survey · {0:N0}",_cells.Count);
  if(_laidOut.Width>0) Arrange(_laidOut);
  for(var index=0;index<_cells.Count;index++) Highlight(index);
 }
 /// <summary>The number of columns that gives each image the largest room, counted for pictures of 3:2.</summary>
 private void Arrange(Size size)
 {
  _laidOut=size; var count=Math.Max(1,_cells.Count); var best=1; var largest=0d;
  for(var columns=1;columns<=count;columns++)
  {
   var rows=(int)Math.Ceiling(count/(double)columns);
   var side=Math.Min(size.Width/columns,size.Height/rows*1.5);
   if(side>largest+.5) { largest=side; best=columns; }
  }
  _grid.Columns=best; _grid.Rows=(int)Math.Ceiling(count/(double)best);
  var bound=(int)Math.Clamp(Math.Max(size.Width/best,size.Height/_grid.Rows)*1.5,400,2400);
  ReadyTask=ReadAsync(bound);
 }

 private int _readBound;
 private async Task ReadAsync(int bound)
 {
  // Read once at the size of the first layout; a later resize keeps the pictures, they are scaled.
  if(_readBound>0) return; _readBound=bound;
  var token=_closing.Token;
  foreach(var cell in _cells.ToArray())
  {
   if(token.IsCancellationRequested) return;
   try
   {
    if(cell.Item.ThumbnailPath is { Length: >0 } thumbnail && await Task.Run(()=>MediaCard.LoadBitmap(thumbnail,0),token) is { } small && cell.Picture.Source==null) cell.Picture.Source=small;
    if(await _pathOf(cell.Item,token) is not { } path || token.IsCancellationRequested) continue;
    BitmapSource? picture=(await FastImageDecoder.DecodeAsync(path,bound,token))?.Bitmap;
    if(picture==null && !token.IsCancellationRequested) picture=DesktopImaging.Bitmap((await DesktopImaging.Service.DecodeAsync(path,0,bound,bound,token)).PngBytes);
    if(!token.IsCancellationRequested && picture!=null) { cell.Picture.Source=picture; cell.Loaded=true; }
   }
   catch(OperationCanceledException) { return; }
   catch(Exception error) when(error is IOException or InvalidDataException or ArgumentException or InvalidOperationException or UnauthorizedAccessException or NotSupportedException) { }
  }
 }

 private void Highlight(int index)
 {
  var frame=_cells[index].Frame;
  if(index==_selected) frame.SetResourceReference(Border.BorderBrushProperty,"Accent"); else frame.BorderBrush=Brushes.Transparent;
 }
 internal void Select(int index)
 {
  if(_cells.Count==0) return;
  index=Math.Clamp(index,0,_cells.Count-1); var before=_selected; _selected=index;
  if(before>=0 && before<_cells.Count) Highlight(before); Highlight(index);
  SelectedChanged?.Invoke(_cells[index].Item);
 }

 /// <summary>Takes an image out of the survey; the library keeps it. The survey ends with its last image.</summary>
 internal void Remove(Guid id)
 {
  var index=_cells.FindIndex(c=>c.Item.Id==id); if(index<0) return;
  _cells.RemoveAt(index);
  if(_cells.Count==0) { Close(); return; }
  if(_selected>=_cells.Count || _selected>index) _selected=Math.Max(0,_selected-1);
  Show(); Select(_selected);
 }
 /// <summary>After a change of the library: images that are gone leave, the others take their new state.</summary>
 internal void Refresh(Func<Guid,MediaItem?> current)
 {
  var changed=false;
  foreach(var cell in _cells.ToArray())
  {
   if(current(cell.Item.Id) is { } item) { cell.Item=item; cell.Caption.Text=item.Title; }
   else { _cells.Remove(cell); changed=true; }
  }
  if(_cells.Count==0) { Close(); return; }
  if(changed) { _selected=Math.Min(_selected,_cells.Count-1); Show(); Select(_selected); }
 }

 /// <summary>The keys of the survey; false for those it leaves to the library (rating, favorite, removing).</summary>
 internal bool HandleKey(KeyEventArgs e)
 {
  if(_closed || Keyboard.Modifiers!=ModifierKeys.None) return false;
  switch(e.Key)
  {
   case Key.Left: Select(_selected-1); return true; case Key.Right: Select(_selected+1); return true;
   case Key.Up: Select(_selected-_grid.Columns); return true; case Key.Down: Select(_selected+_grid.Columns); return true;
   case Key.Home: Select(0); return true; case Key.End: Select(_cells.Count-1); return true;
   case Key.X: if(SelectedItem is { } item) Remove(item.Id); return true;
   case Key.Enter: case Key.Space: if(SelectedItem is { } shown) OpenRequested?.Invoke(shown); return true;
   case Key.Escape: case Key.G: case Key.N: Close(); return true;
  }
  return false;
 }

 internal void Close()
 {
  if(_closed) return; _closed=true; _closing.Cancel();
  Closed?.Invoke(this,EventArgs.Empty);
 }
}
