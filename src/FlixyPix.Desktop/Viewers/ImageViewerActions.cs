using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

/// <summary>What the original image viewer (frmMaxPic) offered besides viewing: flip, copy picture or path, desktop
/// wallpaper, show in Explorer, favorite and delete. Flipping changes the view only, never the file.</summary>
internal sealed partial class ImageViewerWindow
{
 /// <summary>Clipboard and wallpaper go through these, so the smoke test leaves the user's clipboard and desktop alone.</summary>
 internal static Action<BitmapSource> PutImage { get; set; } = Clipboard.SetImage;
 internal static Action<string> PutText { get; set; } = Clipboard.SetText;
 internal static Func<string, bool> SetWallpaper { get; set; } = path => SystemParametersInfo(0x14, 0, path, 0x01 | 0x02);

 /// <summary>Set by the library: asks and deletes; true when the medium is gone.</summary>
 internal Func<MediaItem, bool>? DeleteMedium { get; set; }
 /// <summary>Set by the library: switches the favorite state and returns the new one.</summary>
 internal Func<MediaItem, bool>? ToggleFavorite { get; set; }
 /// <summary>Set by the library: lets the user pick a tag (performer, studio …) whose picture the image becomes.</summary>
 internal Action<MediaItem>? UseAsTagPicture { get; set; }
 /// <summary>Set by the library: lets the user pick a medium whose thumbnail the image becomes (original "Set as thumbnail for").</summary>
 internal Action<MediaItem>? UseAsThumbnail { get; set; }

 private bool _flipX, _flipY;
 internal bool FlippedHorizontally => _flipX;
 internal bool FlippedVertically => _flipY;
 internal MediaItem? CurrentItem => _currentItem;
 internal string Notice { get; private set; } = "";

 private void Note(string text) { Notice = text; _label.Text = text; }
 internal void Tell(string text) => Note(text);

 internal void Flip(bool horizontal)
 {
  if (horizontal) _flipX = !_flipX; else _flipY = !_flipY;
  Apply();
 }

 internal bool CopyImage()
 {
  if (_image.Source is not BitmapSource picture) return false;
  PutImage(picture); Note(T("Image copied to the clipboard")); return true;
 }

 internal bool CopyPath()
 {
  if (_currentItem == null) return false;
  PutText(_currentItem.Path); Note(T("Path copied")); return true;
 }

 internal bool UseAsWallpaper()
 {
  if (_currentItem is not { } item || !File.Exists(item.Path)) { Note(T("Only image files on a drive can be used as the desktop background.")); return false; }
  var done = SetWallpaper(Path.GetFullPath(item.Path));
  Note(done ? T("Desktop background set") : T("Windows could not set the image as the desktop background."));
  return done;
 }

 private void Reveal()
 {
  if (_currentItem is not { } item || !File.Exists(item.Path)) return;
  var info = new ProcessStartInfo("explorer.exe"); info.ArgumentList.Add("/select,"); info.ArgumentList.Add(item.Path); Process.Start(info);
 }

 internal bool SwitchFavorite()
 {
  if (_currentItem is not { } item || ToggleFavorite == null) return false;
  var favorite = ToggleFavorite(item);
  Note(favorite ? T("Added to favorites") : T("Removed from favorites"));
  return favorite;
 }

 /// <summary>Deletes after the library's question and shows the next image; the last one closes the viewer.</summary>
 internal bool DeleteCurrent()
 {
  if (_currentItem is not { } item || DeleteMedium == null || !DeleteMedium(item)) return false;
  if (_itemAt == null) _items.RemoveAt(_index);
  _itemCount--;
  if (_itemCount <= 0) { Close(); return true; }
  GoTo(_index);
  return true;
 }

 private void ShowActions(UIElement? target)
 {
  var menu = new ContextMenu();
  if (target != null) { menu.PlacementTarget = target; menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom; }
  void Add(string label, string? gesture, Action action, bool enabled = true)
  {
   var entry = new MenuItem { Header = label, InputGestureText = gesture ?? "", IsEnabled = enabled };
   entry.Click += (_, _) => action(); menu.Items.Add(entry);
  }
  Add(T("Flip horizontally"), "H", () => Flip(true));
  Add(T("Flip vertically"), "V", () => Flip(false));
  menu.Items.Add(new Separator());
  Add(T("Copy image"), T("Ctrl+C"), () => CopyImage());
  Add(T("Copy path"), T("Ctrl+Shift+C"), () => CopyPath());
  Add(T("Set as desktop background"), null, () => UseAsWallpaper());
  Add(T("Show in Explorer"), null, Reveal);
  var onDisk = _currentItem is { } shown && File.Exists(shown.Path);
  Add(T("Use as tag image …"), null, () => { if (_currentItem is { } item) UseAsTagPicture?.Invoke(item); }, onDisk && UseAsTagPicture != null);
  Add(T("Use as thumbnail for …"), null, () => { if (_currentItem is { } item) UseAsThumbnail?.Invoke(item); }, onDisk && UseAsThumbnail != null);
  menu.Items.Add(new Separator());
  Add(T("Toggle favorite"), null, () => SwitchFavorite(), ToggleFavorite != null);
  Add(T("Delete …"), T("Del"), () => DeleteCurrent(), DeleteMedium != null);
  menu.IsOpen = true;
 }

 /// <summary>Keys of these actions; true when the key was one of them.</summary>
 private bool HandleActionKey(KeyEventArgs e)
 {
  var modifiers = Keyboard.Modifiers;
  switch (e.Key)
  {
   case Key.H when modifiers == ModifierKeys.None: Flip(true); return true;
   case Key.V when modifiers == ModifierKeys.None: Flip(false); return true;
   case Key.C when modifiers == ModifierKeys.Control: CopyImage(); return true;
   case Key.C when modifiers == (ModifierKeys.Control | ModifierKeys.Shift): CopyPath(); return true;
   case Key.Delete when modifiers == ModifierKeys.None: DeleteCurrent(); return true;
   default: return false;
  }
 }

 [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
 [return: MarshalAs(UnmanagedType.Bool)]
 private static extern bool SystemParametersInfo(uint action, uint parameter, string value, uint flags);
}
