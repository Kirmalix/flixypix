using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

public partial class MainWindow
{
 /// <summary>Opens the statistics; a click on a bar shows its media in the main window.</summary>
 internal StatisticsWindow ShowStatistics()
 {
  var window = new StatisticsWindow(this, _repository, OpenStatistic);
  window.Show();
  return window;
 }

 /// <summary>A bar of the statistics as a view: its media kind or search, or the one medium selected.</summary>
 internal void OpenStatistic(StatisticsBar bar)
 {
  if (bar.MediaId is { } id) { ApplyViewState(new ViewState { View = "all" }); Refresh(id); }
  else ApplyViewState(new ViewState { View = bar.View ?? "all", Expression = bar.Expression }, bar.View == null ? bar.Label : null);
  Activate();
 }
}

/// <summary>Statistics of the library (feature plan P4): key figures, kinds, resolutions, file types, imports per month,
/// performers, studios, tags, most watched and playbacks per week. Everything comes from SQL; a click on a bar opens its
/// media in the main window.</summary>
internal sealed class StatisticsWindow : Window
{
 private readonly CatalogRepository repository;
 private readonly Action<StatisticsBar> open;
 private readonly WindowFrame frame;
 private readonly StackPanel body = new() { Margin = new Thickness(24, 0, 24, 12) };
 internal LibraryStatistics? Statistics { get; private set; }
 internal Task Loading { get; private set; } = Task.CompletedTask;
 /// <summary>Bars that lead somewhere, with their label (tests).</summary>
 internal List<(StatisticsBar Bar, Button Button)> Bars { get; } = [];

 internal StatisticsWindow(Window owner, CatalogRepository repository, Action<StatisticsBar> open)
 {
  Owner = owner; this.repository = repository; this.open = open;
  Title = T("Statistics · FlixyPix"); Width = 1120; Height = 820; MinWidth = 720; MinHeight = 480; WindowStartupLocation = WindowStartupLocation.CenterOwner;
  frame = new WindowFrame(T("Statistics"), T("Size and use of this library, counted on this computer. A click on a bar shows its media."), body, "");
  frame.Add(T("Recalculate"), () => Loading = LoadAsync());
  frame.Add(T("Close"), Close, primary: true, cancel: true);
  Content = frame;
  Loaded += (_, _) => Loading = LoadAsync();
 }

 private async Task LoadAsync()
 {
  frame.Status.Text = T("Counting …");
  try
  {
   var now = DateTimeOffset.Now;
   var statistics = await Task.Run(() => repository.BuildStatistics(now));
   Statistics = statistics; Render(statistics);
   frame.Status.Text = T("Counted in {0:N0} ms", statistics.Timings.Values.Sum());
  }
  catch (Exception error) when (error is InvalidOperationException or Microsoft.Data.Sqlite.SqliteException or ObjectDisposedException)
  { frame.Status.Text = T("Statistics not possible · ") + error.Message; }
 }

 private void Render(LibraryStatistics s)
 {
  body.Children.Clear(); Bars.Clear();
  var showO = repository.GetPreferences().History.ShowOCounter;
  var figures = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
  void Figure(string value, string label) => figures.Children.Add(new Border
  {
   Background = SettingsUi.CardBackground, BorderBrush = SettingsUi.CardBorder, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
   Padding = new Thickness(16, 12, 16, 12), Margin = new Thickness(0, 0, 12, 12), MinWidth = 150,
   Child = new StackPanel { Children = { new TextBlock { Text = value, FontSize = 24, FontWeight = FontWeights.SemiBold }, new TextBlock { Text = label, Foreground = SettingsUi.Muted, FontSize = 12 } } },
  });
  Figure(s.Media.ToString("N0", CultureInfo.CurrentCulture), T("Items"));
  Figure(Size(s.Bytes), T("On disk"));
  Figure(Hours(s.Hours), T("Length of videos and audio"));
  Figure(Hours(s.WatchedHours), T("Watched (history)"));
  Figure(s.Plays.ToString("N0", CultureInfo.CurrentCulture), T("Playbacks (history)"));
  Figure(s.NeverWatched.ToString("N0", CultureInfo.CurrentCulture), T("Never watched"));
  if (showO) Figure(s.OTotal.ToString("N0", CultureInfo.CurrentCulture), T("O in total"));
  body.Children.Add(figures);

  var grid = new UniformGrid2();
  grid.Add(BarCard(T("Media kinds"), null, s.Kinds, bar => bar.Count.ToString("N0", CultureInfo.CurrentCulture) + (bar.Hours > 0 ? " · " + Hours(bar.Hours) : "")));
  grid.Add(BarCard(T("Resolution of the videos"), T("By the shorter side of the picture."), s.Resolutions, Count));
  grid.Add(BarCard(T("File types"), T("By file extension."), s.FileTypes, Count));
  grid.Add(BarCard(T("Video codecs"), T("From the technical data (Organize ▾ → Read technical data)."), s.VideoCodecs, Count));
  grid.Add(BarCard(T("Most watched"), null, s.MostWatched, bar => Tn((int)Math.Min(int.MaxValue, bar.Count), "1×", "{0:N0}×")));
  grid.Add(BarCard(T("Performers"), T("With the most items."), s.Performers, Count));
  grid.Add(BarCard(T("Studios"), T("With the most items."), s.Studios, Count));
  grid.Add(BarCard(T("Tags"), T("With the most items."), s.Tags, Count));
  body.Children.Add(grid.Panel);
  body.Children.Add(ColumnCard(T("Imports per month"), T("The last 24 months; a click shows the imports of the month."), s.ImportsPerMonth));
  body.Children.Add(ColumnCard(T("Playbacks per week"), T("The last 26 weeks of the playback history."), s.PlaysPerWeek));
 }

 private static string Count(StatisticsBar bar) => bar.Count.ToString("N0", CultureInfo.CurrentCulture);
 private static string Hours(double hours) => hours < 1 ? MediaCard.DurationText(hours * 3600) : T("{0:N0} h", Math.Round(hours));
 private static string Size(long bytes) => MediaCard.SizeText(bytes);

 /// <summary>Horizontal bars, longest first, in one color; label and value stay in text color.</summary>
 private Border BarCard(string title, string? hint, IReadOnlyList<StatisticsBar> bars, Func<StatisticsBar, string> value)
 {
  var rows = new StackPanel { Margin = new Thickness(12, 8, 12, 10) };
  var largest = Math.Max(1, bars.Count == 0 ? 1 : bars.Max(b => b.Count));
  if (bars.Count == 0) rows.Children.Add(new TextBlock { Text = T("Nothing to count yet."), Foreground = SettingsUi.Muted, Margin = new Thickness(4) });
  foreach (var bar in bars)
  {
   var line = new Grid { Margin = new Thickness(0, 2, 0, 2) };
   line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) }); line.ColumnDefinitions.Add(new ColumnDefinition()); line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
   var label = new TextBlock { Text = bar.Label, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
   var track = new Grid { Height = 12, VerticalAlignment = VerticalAlignment.Center };
   track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(bar.Count, GridUnitType.Star) });
   track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(largest - bar.Count, GridUnitType.Star) });
   track.Children.Add(new Border { Background = SettingsUi.Accent, CornerRadius = new CornerRadius(0, 4, 4, 0), MinWidth = bar.Count > 0 ? 2 : 0 });
   var number = new TextBlock { Text = value(bar), Foreground = SettingsUi.Muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), Typography = { NumeralAlignment = FontNumeralAlignment.Tabular } };
   Grid.SetColumn(track, 1); Grid.SetColumn(number, 2);
   line.Children.Add(label); line.Children.Add(track); line.Children.Add(number);
   rows.Children.Add(Clickable(bar, line, bar.Label + " · " + value(bar)));
  }
  var card = SettingsUi.Card(title, hint, rows); card.Margin = new Thickness(0, 0, 14, 14); return card;
 }

 /// <summary>Columns over time, oldest left; the hover tip names period and count.</summary>
 private Border ColumnCard(string title, string hint, IReadOnlyList<StatisticsBar> bars)
 {
  var columns = new Grid { Height = 150, Margin = new Thickness(16, 12, 16, 4) };
  var largest = Math.Max(1, bars.Count == 0 ? 1 : bars.Max(b => b.Count));
  for (var i = 0; i < bars.Count; i++)
  {
   var bar = bars[i];
   columns.ColumnDefinitions.Add(new ColumnDefinition());
   var stack = new Grid();
   stack.RowDefinitions.Add(new RowDefinition { Height = new GridLength(largest - bar.Count, GridUnitType.Star) });
   stack.RowDefinitions.Add(new RowDefinition { Height = new GridLength(bar.Count, GridUnitType.Star) });
   var fill = new Border { Background = SettingsUi.Accent, CornerRadius = new CornerRadius(4, 4, 0, 0), Margin = new Thickness(1, 0, 1, 0), MinHeight = bar.Count > 0 ? 2 : 0 };
   Grid.SetRow(fill, 1); stack.Children.Add(fill);
   var cell = Clickable(bar, stack, bar.Label + " · " + bar.Count.ToString("N0", CultureInfo.CurrentCulture));
   Grid.SetColumn(cell, i); columns.Children.Add(cell);
  }
  // A recessive axis: first, middle and last period and the largest value.
  var axis = new DockPanel { Margin = new Thickness(16, 2, 16, 12) };
  if (bars.Count > 0)
  {
   var last = new TextBlock { Text = bars[^1].Label, Foreground = SettingsUi.Muted, FontSize = 11 }; DockPanel.SetDock(last, Dock.Right);
   axis.Children.Add(last); axis.Children.Add(new TextBlock { Text = bars[0].Label + "  ·  " + T("highest {0:N0}", largest), Foreground = SettingsUi.Muted, FontSize = 11 });
  }
  var card = SettingsUi.Card(title, hint, new StackPanel { Children = { columns, axis } }); card.Margin = new Thickness(0, 0, 0, 14); return card;
 }

 private FrameworkElement Clickable(StatisticsBar bar, FrameworkElement content, string tip)
 {
  var leads = bar.Expression != null || bar.MediaId != null || bar.View != null;
  content.ToolTip = tip; AutomationProperties.SetName(content, tip);
  if (!leads) return content;
  var button = new Button { Content = content, Template = StretchedButton, Background = Brushes.Transparent, Cursor = System.Windows.Input.Cursors.Hand, ToolTip = tip };
  button.MouseEnter += (_, _) => button.Background = SettingsUi.Chip; button.MouseLeave += (_, _) => button.Background = Brushes.Transparent;
  AutomationProperties.SetName(button, tip);
  button.Click += (_, _) => open(bar);
  Bars.Add((bar, button));
  return button;
 }

 /// <summary>A button that only frames its content and lets it take the full width, so bars keep their length.</summary>
 private static readonly ControlTemplate StretchedButton = CreateStretchedButton();
 private static ControlTemplate CreateStretchedButton()
 {
  var frame = new FrameworkElementFactory(typeof(Border));
  frame.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding(nameof(Background)) { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
  frame.SetValue(Border.CornerRadiusProperty, new CornerRadius(4)); frame.SetValue(Border.PaddingProperty, new Thickness(4, 2, 4, 2));
  frame.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));
  return new ControlTemplate(typeof(Button)) { VisualTree = frame };
 }

 /// <summary>Cards side by side in two columns.</summary>
 private sealed class UniformGrid2
 {
  internal Grid Panel { get; } = new();
  private int count;
  internal UniformGrid2() { Panel.ColumnDefinitions.Add(new ColumnDefinition()); Panel.ColumnDefinitions.Add(new ColumnDefinition()); }
  internal void Add(UIElement card)
  {
   if (count % 2 == 0) Panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
   Grid.SetRow(card, count / 2); Grid.SetColumn(card, count % 2); Panel.Children.Add(card); count++;
  }
 }
}
