using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

public partial class MainWindow
{
 /// <summary>"Öffnen mit": a program is checked and saved in the settings, offered only for its media kinds, and opens
 /// the selected video with the expected command line (the start itself is caught).</summary>
 internal async Task RunOpenWithSmokeTestAsync(string directory)
 {
  static void Require(bool check, string message) { if (!check) throw new InvalidOperationException(message); }
  var file = Path.Combine(directory, "open-with", "Clip mit Leerzeichen.mp4"); Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllBytes(file, new byte[64]);
  var video = _repository.Upsert(new MediaItem { Path = file, Title = "Öffnen mit", Kind = MediaKind.Video, DurationSeconds = 60 });
  var programsBefore = _settings.ExternalPrograms;
  var startBefore = StartProcess;
  var started = new List<ProcessStartInfo>();
  var settings = new SettingsWindow(this, _repository, _settings, _settings.DataDirectory, () => { }, () => { }); settings.Show();
  try
  {
   settings.SelectPage("Tools");
   var dialog = new ExternalProgramDialog(settings, null, []); dialog.Show();
   var program = Environment.ProcessPath!;
   dialog.Fill(program, "Testplayer", "--start {seconds} \"{path}\"", video: true, image: false, audio: false, hotkey: "Ctrl+F");
   Require(!dialog.TrySave() && dialog.Error.Length > 0, "A shortcut of the main window was accepted for a program.");
   dialog.Fill(program, "Testplayer", "--fullscreen", true, false, false, "Ctrl+Shift+P");
   Require(!dialog.TrySave(), "Arguments without the file were accepted.");
   dialog.Fill(program, "Testplayer", "--start {seconds} \"{path}\"", true, false, false, "Ctrl+Shift+P");
   Require(dialog.TrySave() && dialog.Result is { Kinds: [MediaKind.Video] }, "A valid program was refused: " + dialog.Error);
   dialog.Close();
   settings.AddProgram(dialog.Result!);
   await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
   settings.ScrollToPrograms(); await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
   Capture((FrameworkElement)settings.Content, Path.Combine(directory, "settings-open-with.png"));
   Require(settings.TrySave() && settings.Result?.ExternalPrograms is [{ Name: "Testplayer" }], "The settings did not keep the program.");
   _settings = _settings with { ExternalPrograms = [.. settings.Result!.ExternalPrograms!] };
  }
  finally { if (settings.IsVisible) settings.Close(); }

  try
  {
   StartProcess = info => { started.Add(info); return true; };
   Require(ProgramsFor(MediaKind.Video).Count() == 1 && !ProgramsFor(MediaKind.Image).Any(), "The program is not offered by media kind.");
   // The media menu: real submenus instead of a second menu, shortcuts on the right, disabled entries instead of errors afterwards.
   var current = _repository.GetMedia(video.Id)!;
   var menu = Menus.Create(MediaMenu([current]));
   var labels = Menus.Labels(menu).ToList();
   var arrow = " ▸ ";
   Require(labels[0] == T("Open") && labels.Contains(T("Open with") + arrow + "Testplayer") && labels.Contains(T("Rate") + arrow + "★★★☆☆")
    && labels.Contains(T("Add to collection") + arrow + T("New collection from the selection …")) && labels.Contains(T("In the target collection")) && labels.Contains(T("File") + arrow + T("Copy to") + arrow + T("Other folder …"))
    && !labels.Any(l => l.Contains("▾", StringComparison.Ordinal)) && !labels.Contains(T("Pause playback")),
    "The media menu is not built as planned: " + string.Join(" | ", labels));
   MenuItem Entry(ItemsControl parent, string header) => parent.Items.OfType<MenuItem>().First(i => (string)i.Header == header);
   Require(Entry(menu, T("Open with")).HasItems && Entry(menu, T("Open")).InputGestureText == SettingsUi.KeyName("Enter")
    && Entry(Entry(menu, T("Process media")), T("Convert to MP4 …")).IsEnabled, "Submenus, shortcuts or enabled entries are missing in the media menu.");
   var picture = _repository.Upsert(new MediaItem { Path = Path.Combine(directory, "open-with", "Bild.jpg"), Title = "Bild", Kind = MediaKind.Image });
   try
   {
    var several = Menus.Create(MediaMenu([current, picture]));
    Require(several.Items[0] is MenuItem { IsEnabled: false } heading && (string)heading.Header == Tn(2, "1 item", "{0:N0} items")
     && !Entry(several, T("Media information")).IsEnabled && !Entry(Entry(several, T("Process media")), T("Convert to MP4 …")).IsEnabled
     && Entry(several, T("Rate")).IsEnabled, "With several media the menu does not say how many or offers what applies to one only.");
    var outside = Menus.Labels(Menus.Create(MediaMenu([current], MediaMenuPlace.Home))).ToList();
    Require(outside.Contains(T("Show in library")) && !labels.Contains(T("Show in library")), "The start page menu does not lead to the library.");
    // One look at the menu with an open submenu, light theme as set up for the smoke test.
    var shown = Menus.Open(this, MediaMenu([current]), placement: System.Windows.Controls.Primitives.PlacementMode.Center);
    await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    Entry(shown, T("Rate")).IsSubmenuOpen = true;
    await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    Capture(shown, Path.Combine(directory, "media-menu.png"));
    shown.IsOpen = false;
   }
   finally { _repository.Delete(picture.Id); }
   var saved = _settings.ExternalPrograms[0];
   _repository.Upsert(_repository.GetMedia(video.Id)! with { PlaybackPositionSeconds = 42.7 });
   Require(OpenWith(saved, [_repository.GetMedia(video.Id)!]) == 1 && started.Count == 1, "The program was not started.");
   var info = started[0];
   Require(info.FileName == saved.Path && info.Arguments == $"--start 42 \"{Path.GetFullPath(video.Path)}\"" && !info.UseShellExecute
    && info.WorkingDirectory == Path.GetDirectoryName(Path.GetFullPath(video.Path)), "The program got the wrong command line: " + info.Arguments);
   Require(OpenWith(saved, [new MediaItem { Path = file, Title = "Bild", Kind = MediaKind.Image }]) == 0 && started.Count == 1, "A video program opened an image.");
   // "Im Browser suchen" opens the escaped name with the provider in the default browser.
   var web = WebSearchEntries("Jane Doe"); Require(web.Length == WebSearch.Defaults.Count, "The web search menu does not list the providers.");
   web[0].Action();
   Require(started.Count == 2 && started[1].UseShellExecute && started[1].FileName == "https://www.google.com/search?q=Jane%20Doe", "The web search opened the wrong address: " + started[^1].FileName);
   // The whole view as an M3U playlist, in the view's order.
   var playlistFile = Path.Combine(directory, "ansicht.m3u8");
   await SaveM3uForTestAsync(selectionOnly: false, playlistFile);
   var expected = _window is { } view ? PositionInView(view, Guid.Empty).Total : 0;
   var lines = File.ReadAllLines(playlistFile);
   Require(lines.Length > 0 && lines[0] == "#EXTM3U" && lines.Count(l => l.StartsWith("#EXTINF:", StringComparison.Ordinal)) == expected && expected > 0,
    $"The playlist does not list the view ({expected} expected).");
   // Quick access folders become targets of "Kopieren nach" and "Verschieben nach".
   var quick = Path.Combine(directory, "schnellzugriff"); Directory.CreateDirectory(quick);
   _settings = _settings with { QuickAccessFolders = [quick] };
   Require(QuickAccessEntries(BatchFileAction.Copy) is [var first, _, _] && first.Label.StartsWith("schnellzugriff", StringComparison.Ordinal), "The quick access folder is not offered.");

  }
  finally
  {
   StartProcess = startBefore;
   _settings = _settings with { ExternalPrograms = programsBefore, QuickAccessFolders = [] };
   _repository.Delete(video.Id);
  }
 }
}
