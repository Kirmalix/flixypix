using System.Diagnostics;
using System.Windows;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

/// <summary>"Im Browser suchen": a tag, performer or studio name in the default browser with a provider of this computer.</summary>
public partial class MainWindow
{
 internal IReadOnlyList<WebSearchProvider> SearchProviders => _settings.WebSearchProviders ?? WebSearch.Defaults;

 internal (string Label, Action Action)[] WebSearchEntries(string query) =>
  [.. SearchProviders.Select(p => (p.Name, (Action)(() => OpenWebSearch(p, query, this))))];


 /// <summary>Opens the search page in the default browser (through <see cref="StartProcess"/>, so tests launch nothing).</summary>
 internal static bool OpenWebSearch(WebSearchProvider provider, string query, Window owner)
 {
  try { return StartProcess(new ProcessStartInfo(WebSearch.Url(provider, query, Texts.Current.Language)) { UseShellExecute = true }); }
  catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
  { Message.Show(owner, ex.Message, T("Search in browser"), MessageBoxButton.OK, MessageBoxImage.Warning); return false; }
 }
}
