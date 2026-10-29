using System.IO;
using System.Runtime.InteropServices;

namespace FlixyPix.Desktop;

/// <summary>Deleting a medium's file: into the Windows recycle bin (original "deletetorecyclebin") or for good.</summary>
public partial class MainWindow
{
 /// <summary>Moves a file to the recycle bin; the smoke test replaces it so that no test file ends up in the user's bin.</summary>
 internal static Action<string> SendToRecycleBin { get; set; } = RecycleBin.Send;

 private static void DeleteFromDisk(string path, bool recycle)
 {
  if (recycle) SendToRecycleBin(path); else File.Delete(path);
 }
}

internal static class RecycleBin
{
 /// <summary>The shell moves the file to the recycle bin without dialogs; failures come back as an exception.</summary>
 internal static void Send(string path)
 {
  var operation = new FileOperation { Function = 3 /* FO_DELETE */, From = Path.GetFullPath(path) + "\0\0", Flags = 0x0040 | 0x0010 | 0x0400 | 0x0004 /* ALLOWUNDO, NOCONFIRMATION, NOERRORUI, SILENT */ };
  var result = SHFileOperation(ref operation);
  if (result != 0 || operation.Aborted) throw new IOException(T("The file could not be moved to the Recycle Bin (error {0}).", result));
  if (File.Exists(path)) throw new IOException(T("The file is still at its original location; it could not be moved to the Recycle Bin."));
 }

 [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
 private struct FileOperation
 {
  public nint Window;
  public uint Function;
  [MarshalAs(UnmanagedType.LPWStr)] public string From;
  [MarshalAs(UnmanagedType.LPWStr)] public string? To;
  public ushort Flags;
  [MarshalAs(UnmanagedType.Bool)] public bool Aborted;
  public nint NameMappings;
  [MarshalAs(UnmanagedType.LPWStr)] public string? Title;
 }

 [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHFileOperation(ref FileOperation operation);
}
