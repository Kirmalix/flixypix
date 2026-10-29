using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

/// <summary>"Öffnen mit": the selected media in programs of this computer (original "Play video with" / "Open image
/// with"), the Windows default program, or a program picked in the Windows dialog. Also the right-click menu of tiles.</summary>
public partial class MainWindow
{
 /// <summary>Starts a program; the smoke test replaces it so that nothing is launched.</summary>
 internal static Func<ProcessStartInfo, bool> StartProcess { get; set; } = info => { using var process = Process.Start(info); return true; };

 private IEnumerable<ExternalProgram> ProgramsFor(MediaKind kind) => _settings.ExternalPrograms.Where(p => ExternalPrograms.Applies(p, kind));

 /// <summary>Opens each medium (at most 20) with the program; media of other kinds are skipped.</summary>
 internal int OpenWith(ExternalProgram program, IReadOnlyList<MediaItem> items)
 {
  var opened = 0;
  foreach (var item in items.Where(i => ExternalPrograms.Applies(program, i.Kind)).Take(20))
  {
   if (!MediaPresence.Exists(item.Path)) { Status.Text = T("File unavailable: {0}", item.Path); continue; }
   try
   {
    var info = new ProcessStartInfo(program.Path) { Arguments = ExternalPrograms.BuildArguments(program, item, item.PlaybackPositionSeconds), UseShellExecute = false,
     WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(item.Path)) ?? "" };
    if (StartProcess(info)) opened++;
   }
   catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
   {
    ErrorLog.Write("Öffnen mit", ex);
    Message.Show(this, T("“{0}” could not be started.\n{1}", program.Name, ex.Message), T("Open with"), MessageBoxButton.OK, MessageBoxImage.Warning);
    break;
   }
  }
  if (opened > 0) Status.Text = Tn(opened, "1 item opened with {1}", "{0:N0} items opened with {1}", program.Name);
  return opened;
 }
 private void OpenWith(ExternalProgram program, MediaItem item) => OpenWith(program, [item]);

 private void OpenWithDefault(MediaItem item)
 {
  try { StartProcess(new ProcessStartInfo(item.Path) { UseShellExecute = true }); }
  catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
  { Message.Show(this, ex.Message, T("Open with"), MessageBoxButton.OK, MessageBoxImage.Warning); }
 }

 /// <summary>The Windows "Open with" dialog; it starts the chosen program itself.</summary>
 private void OpenWithChooser(MediaItem item)
 {
  var info = new OpenAsInfo { File = item.Path, Flags = OpenAsFlags.Exec | OpenAsFlags.AllowRegistration | OpenAsFlags.RegisterExtension };
  var result = SHOpenWithDialog(new WindowInteropHelper(this).Handle, ref info);
  if (result < 0 && result != unchecked((int)0x800704C7)) Message.Show(this, Marshal.GetExceptionForHR(result)?.Message ?? result.ToString(), T("Open with"), MessageBoxButton.OK, MessageBoxImage.Warning);
 }

 /// <summary>A program's shortcut opens the selected media; text fields keep their keys.</summary>
 private bool OpenWithHotkey(KeyEventArgs e)
 {
  if (Keyboard.FocusedElement is TextBox || SelectedMedia.Count == 0) return false;
  var key = e.Key == Key.System ? e.SystemKey : e.Key;
  var program = _settings.ExternalPrograms.FirstOrDefault(p => PlayerHotkeys.TryParse(p.Hotkey, out var modifiers, out var k) && k == key && modifiers == Keyboard.Modifiers);
  if (program == null) return false;
  e.Handled = true;
  if (OpenWith(program, SelectedMedia) == 0 && SelectedMedia.All(i => !ExternalPrograms.Applies(program, i.Kind)))
   Status.Text = T("{0} is not set up for this media type.", program.Name);
  return true;
 }

 /// <summary>Right click on a tile selects it (unless it is part of the selection) and opens the menu of the selection.</summary>
 private void MediaRightClicked(object sender, MouseButtonEventArgs e)
 {
  if (ItemsControl.ContainerFromElement(MediaList, (DependencyObject)e.OriginalSource) is not ListBoxItem { DataContext: MediaCard card } container) return;
  e.Handled = true;
  if (!container.IsSelected) { MediaList.SelectedItems.Clear(); MediaList.SelectedItem = card; }
  container.Focus();
  OpenMediaMenu();
 }

 [Flags] private enum OpenAsFlags { AllowRegistration = 1, RegisterExtension = 2, Exec = 4 }
 [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
 private struct OpenAsInfo { [MarshalAs(UnmanagedType.LPWStr)] public string File; [MarshalAs(UnmanagedType.LPWStr)] public string? Class; public OpenAsFlags Flags; }
 [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHOpenWithDialog(nint parent, ref OpenAsInfo info);
}
