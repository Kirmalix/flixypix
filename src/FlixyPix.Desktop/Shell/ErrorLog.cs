using System.Diagnostics;
using System.IO;
using System.Text;

namespace FlixyPix.Desktop;

/// <summary>Error log under %LOCALAPPDATA%\FlixyPix\logs, one file per day, kept for 14 days. It records failures of the
/// interface, of background tasks nobody awaited and of operations, with type, message and stack, so a problem can be
/// reported after its message box is gone. Only this computer reads it; it is never sent anywhere.</summary>
internal static class ErrorLog
{
 private static readonly object gate = new();
 internal static string Folder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FlixyPix", "logs");
 internal static string TodayFile => Path.Combine(Folder, $"fehler-{DateTime.Now:yyyy-MM-dd}.log");

 /// <summary>Hooks the process-wide handlers: tasks whose exception nobody observed, and crashes outside the interface thread.</summary>
 internal static void Install()
 {
  TaskScheduler.UnobservedTaskException += (_, e) => { Write("Hintergrundaufgabe", e.Exception); e.SetObserved(); };
  AppDomain.CurrentDomain.UnhandledException += (_, e) => { if (e.ExceptionObject is Exception error) Write(e.IsTerminating ? T("Crash") : T("Process"), error); };
  Cleanup();
 }

 internal static void Write(string source, Exception error)
 {
  try
  {
   var text = new StringBuilder()
    .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append(" · ").Append(source).Append(T(" · FlixyPix "))
    .Append(typeof(ErrorLog).Assembly.GetName().Version).AppendLine()
    .AppendLine(error.ToString()).AppendLine();
   lock (gate) { Directory.CreateDirectory(Folder); File.AppendAllText(TodayFile, text.ToString(), new UTF8Encoding(false)); }
  }
  catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { Debug.WriteLine(failure); }
 }

 private static void Cleanup()
 {
  try
  {
   if (!Directory.Exists(Folder)) return;
   foreach (var file in new DirectoryInfo(Folder).EnumerateFiles("fehler-*.log").Where(f => f.LastWriteTime < DateTime.Now.AddDays(-14))) file.Delete();
  }
  catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { Debug.WriteLine(failure); }
 }

 internal static void OpenFolder()
 {
  Directory.CreateDirectory(Folder);
  Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { Folder }, UseShellExecute = false });
 }
}
