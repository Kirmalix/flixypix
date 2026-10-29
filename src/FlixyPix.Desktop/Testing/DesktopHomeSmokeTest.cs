using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

public partial class MainWindow
{
 /// <summary>The start page: it opens at start when switched on, shows the chosen rows over the hidden grid, counts plays,
 /// gives way to a search and leads to the matching view with "Alle anzeigen".</summary>
 private async Task RunHomeSmokeTestAsync(string directory)
 {
  static void Require(bool check, string message) { if (!check) throw new InvalidOperationException(message); }
  async Task Settled() { await _lastRefresh; await _lastHome; await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); }
  var before = _repository.GetPreferences();
  try
  {
   // Playing counts (an image as a view): it opens in the viewer and is now recently watched.
   var image = _repository.Search().First(i => i.Kind == MediaKind.Image && File.Exists(i.Path));
   var views = image.ViewCount ?? 0;
   await Play(image); StopSlideshow();
   Require(_repository.GetMedia(image.Id) is { LastPlayedAt: not null } played && played.ViewCount == views + 1, "Playing did not count for the start page.");
   _repository.Upsert(_repository.GetMedia(image.Id)! with { Rating = 9, WatchLater = true });

   // The settings decide: start page at start, and which rows.
   var settings = new SettingsWindow(this, _repository, _settings, _settings.DataDirectory, () => { }, () => { }); settings.Show();
   try
   {
    settings.StartOnHome.IsChecked = false;
    Require(settings.TrySave(), "Start page options could not be saved.");
   }
   finally { settings.Close(); }
   ApplyViewState(new ViewState()); await Settled();
   await StartAsync(mayOpenHome: true); await Settled();
   Require(_view != "home" && !Home.IsVisible, "The start page opened although it is switched off.");
   settings = new SettingsWindow(this, _repository, _settings, _settings.DataDirectory, () => { }, () => { }); settings.Show();
   try
   {
    settings.StartOnHome.IsChecked = true;
    foreach (var (kind, check) in settings.HomeRows) check.IsChecked = kind is HomeRowKind.RecentlyAdded or HomeRowKind.WatchLater or HomeRowKind.Popular or HomeRowKind.RandomOfTheDay;
    Require(settings.TrySave(), "Start page rows could not be saved.");
    settings.SelectPage("Home page"); await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    Capture((FrameworkElement)settings.Content, Path.Combine(directory, "settings-home.png"));
   }
   finally { settings.Close(); }
   await StartAsync(mayOpenHome: true); await Settled();
   Require(_view == "home" && Home.IsVisible && MediaList.Visibility == Visibility.Collapsed && ViewTitle.Text == T("Home"), "The start page did not open at start.");
   Require(DetailsPane.Visibility == Visibility.Collapsed && DetailsColumn.Width.Value == 0, "The start page did not take the place of the details.");
   Require(NavItems.Children.OfType<RadioButton>().First() is { IsChecked: true, Tag: "home" }, "The sidebar did not mark the start page.");
   Require(Home.RowCount == 4 && Home.HeroCount >= 1, $"The start page showed {Home.RowCount} rows and {Home.HeroCount} banner entries instead of the four chosen rows.");
   Require(History.CanGoBack, "The view before the start page is not one step back.");
   await Task.Delay(400); await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
   Capture(RootContent, Path.Combine(directory, "desktop-home.png"));
   Theme.Apply(ColorScheme.Light); await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
   Capture(RootContent, Path.Combine(directory, "desktop-home-light.png"));
   Theme.Apply(ColorScheme.Dark); await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
   // Cards share the width: at least two, about 240 px each.
   Require(Home.CardWidth is >= 180 and <= 520, $"Start page cards are {Home.CardWidth} px wide.");

   // Typing a search leaves the start page for all media.
   SearchBox.Text = "Abend"; _searchTimer.Stop(); Refresh(); await Settled();
   Require(_view == "all" && !Home.IsVisible && MediaList.Visibility == Visibility.Visible && MediaList.Items.Count >= 1 && DetailsPane.Visibility == Visibility.Visible, "A search did not leave the start page.");
   SearchBox.Text = ""; _searchTimer.Stop();
   GoHome(); await Settled();
   Require(_view == "home" && Home.IsVisible && SearchBox.Text.Length == 0, "The start page did not come back.");
   // "Alle anzeigen" of a row opens the matching view.
   Home.ShowAll!(new HomeRow(HomeRowKind.WatchLater, "Später ansehen", [], [])); await Settled();
   Require(_view == "watchlater" && !Home.IsVisible && MediaList.Items.Cast<MediaCard>().Any(c => c.Item.Id == image.Id), "\"Alle anzeigen\" did not open the watch later view.");
  }
  finally
  {
   _repository.SavePreferences(_repository.GetPreferences() with { Home = before.Home });
   ApplyViewState(new ViewState()); await Settled();
  }
 }
}
