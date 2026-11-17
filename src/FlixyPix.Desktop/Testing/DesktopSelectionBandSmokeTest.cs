using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace FlixyPix.Desktop;

public partial class MainWindow
{
 /// <summary>A rectangle drawn from the middle of one tile to the middle of the next selects both and nothing else; with Ctrl
 /// a further rectangle adds to the selection; a rectangle in the gap between tiles takes none.</summary>
 private async Task RunSelectionBandSmokeTestAsync()
 {
  static void Require(bool check, string message) { if (!check) throw new InvalidOperationException(message); }
  async Task Idle() => await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
  var panel = Tiles ?? throw new InvalidOperationException("The tile panel is missing.");
  Require(MediaList.Items.Count >= 3, "The rectangle test needs three tiles.");
  MediaList.ScrollIntoView(MediaList.Items[0]); await Idle();
  Point Center(int index)
  {
   var container = (ListBoxItem)MediaList.ItemContainerGenerator.ContainerFromIndex(index);
   return container.TransformToAncestor(panel).Transform(new Point(container.ActualWidth / 2, container.ActualHeight / 2));
  }
  Point Content(Point point) => new(point.X, point.Y + panel.VerticalOffset);
  Guid Id(int index) => ((MediaCard)MediaList.Items[index]).Item.Id;
  async Task Draw(Point from, Point to, bool add)
  {
   Require(StartBand(Content(from), add, follow: false) && UpdateBand(to), "The rectangle did not start.");
   await EndBandAsync(); await Idle();
  }

  MediaList.SelectedItems.Clear(); await Idle();
  await Draw(Center(0), Center(1), add: false);
  var chosen = SelectedMedia.Select(item => item.Id).ToHashSet();
  Require(chosen.SetEquals([Id(0), Id(1)]), $"The rectangle from the first to the second tile selected {chosen.Count} items.");
  Require(AdornerLayer() is not { } layer || layer.GetAdorners(panel) is null, "The frame stayed on screen.");

  await Draw(Center(2), Center(2) + new Vector(8, 8), add: true);
  chosen = SelectedMedia.Select(item => item.Id).ToHashSet();
  Require(chosen.SetEquals([Id(0), Id(1), Id(2)]), "Ctrl and a rectangle on the third tile did not add it to the selection.");

  // Between the first two tiles of a row, a short frame touches neither.
  if (panel.Columns > 1)
  {
   var gap = new Point(panel.TileWidth, Center(0).Y);
   await Draw(gap - new Vector(2, 0), gap + new Vector(2, 4), add: false);
   Require(SelectedMedia.Count == 0, "A rectangle in the gap between two tiles selected a tile.");
  }
  MediaList.SelectedItems.Clear(); await Idle();
  System.Windows.Documents.AdornerLayer? AdornerLayer() => System.Windows.Documents.AdornerLayer.GetAdornerLayer(panel);
 }
}
