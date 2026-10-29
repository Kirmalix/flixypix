using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

/// <summary>Opening a library that needs long work first (a format change, or finishing the encryption of its pictures):
/// the window first says what will happen, how long it takes and how much room the safety copy needs, and asks; then it
/// shows each phase with its progress while the work runs in the background. Cancelling leaves the library as it was.
/// Libraries without such work open directly, without this window.</summary>
internal sealed class LibraryOpeningWindow : Window
{
 /// <summary>Test runs on the hidden desktop: start without asking.</summary>
 internal static bool ConfirmAutomatically { get; set; }
 /// <summary>Test runs: called once the progress shows, for a screenshot.</summary>
 internal static Action<LibraryOpeningWindow>? ProgressShown { get; set; }

 private readonly string databasePath;
 private readonly CatalogKey? key;
 private readonly CatalogOpenPlan plan;
 private readonly CancellationTokenSource cancel = new();
 private readonly StackPanel body = new() { Margin = new Thickness(24, 22, 24, 8) };
 private readonly StackPanel footer = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(24, 14, 24, 20) };
 private readonly Dictionary<CatalogOpenPhase, (TextBlock Icon, TextBlock Label)> phaseRows = [];
 private readonly ProgressBar bar = new() { Height = 6, Margin = new Thickness(0, 14, 0, 6), Minimum = 0, Maximum = 1 };
 private readonly TextBlock detail = SettingsUi.Hint("");
 private readonly System.Diagnostics.Stopwatch watch = new();
 private Button? cancelButton;
 private bool running, finished;
 private CatalogRepository? opened;
 private Exception? failure;
 private CatalogOpenPhase? lastPhase;

 internal CatalogOpenPlan Plan => plan;

 private LibraryOpeningWindow(Window? owner, string databasePath, CatalogKey? key, CatalogOpenPlan plan)
 {
  this.databasePath = databasePath; this.key = key; this.plan = plan;
  if (owner is { IsLoaded: true }) { Owner = owner; WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false; }
  else { WindowStartupLocation = WindowStartupLocation.CenterScreen; ShowInTaskbar = true; }
  Title = T("Prepare library"); Width = 540; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize; FontSize = 14;
  Background = Theme.Brush("WindowBackground");
  var layout = new DockPanel();
  var divider = new Border { Height = 1, Background = Theme.Brush("Divider") };
  DockPanel.SetDock(footer, Dock.Bottom); DockPanel.SetDock(divider, Dock.Bottom);
  layout.Children.Add(footer); layout.Children.Add(divider); layout.Children.Add(body);
  Content = layout;
  // Neutral title and capture protection apply here too, before the main window exists.
  SourceInitialized += (_, _) => Privacy.Attach(this);
  Closing += (_, e) => { if (running && !finished) { e.Cancel = true; Cancel(); } };
  if (ConfirmAutomatically || !plan.Upgrades) Loaded += (_, _) => Start(); else ShowPlan();
 }

 /// <summary>Opens the library; with long work first through this window. Throws <see cref="OperationCanceledException"/>
 /// when the user declines or cancels, and the error of a failed change otherwise (both leave the library unchanged).</summary>
 internal static CatalogRepository Open(Window? owner, string databasePath, CatalogKey? key)
 {
  var plan = CatalogRepository.Inspect(databasePath, key);
  if (!plan.HasWork) return new CatalogRepository(databasePath, key);
  var window = new LibraryOpeningWindow(owner, databasePath, key, plan);
  window.ShowDialog();
  if (window.failure is OperationCanceledException) throw new OperationCanceledException(T("The library was not converted and stays unchanged."));
  if (window.failure is { } error) { ErrorLog.Write("Bibliothek umstellen · " + (window.lastPhase?.ToString() ?? "Start"), error); throw error; }
  return window.opened ?? throw new OperationCanceledException(T("The library was not converted and stays unchanged."));
 }

 /// <summary>Test runs: the question of a plan as a window that is not shown modally.</summary>
 internal static LibraryOpeningWindow Preview(Window owner, string databasePath, CatalogOpenPlan plan) => new(owner, databasePath, null, plan);

 private static string Duration(TimeSpan time) => time.TotalSeconds < 60 ? T("less than a minute") : Tn((int)Math.Ceiling(time.TotalMinutes), "about 1 minute", "about {0} minutes");

 private static string PhaseText(CatalogOpenPhase phase) => phase switch
 {
  CatalogOpenPhase.SafetyCopy => T("Safety copy of the catalog"),
  CatalogOpenPhase.Pictures => T("Moving pictures into files beside the catalog"),
  CatalogOpenPhase.Entries => T("Writing the entries anew and filling the tables for sorting and filters"),
  _ => T("Finishing the encryption of the pictures"),
 };

 private void Heading()
 {
  body.Children.Clear();
  body.Children.Add(new TextBlock
  {
   Text = plan.Upgrades ? T("The library is converted to the new format") : T("The encryption of the pictures is finished"),
   FontSize = 18, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Foreground = Theme.Brush("Text"),
  });
  var folder = Path.GetDirectoryName(databasePath)!;
  body.Children.Add(new TextBlock { Text = Path.GetFileName(folder), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 0), Foreground = Theme.Brush("Text") });
  body.Children.Add(SettingsUi.Hint(folder));
 }

 private void ShowPlan()
 {
  Heading();
  var facts = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
  void Fact(string text) => facts.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 2), Foreground = Theme.Brush("Text") });
  Fact(T("Duration: {0}", Duration(plan.Estimate)));
  Fact(Tn((int)plan.Media, "1 item", "{0:N0} items") + (plan.Pictures > 0 ? " · " + Tn((int)plan.Pictures, "1 picture", "{0:N0} pictures") : ""));
  Fact(T("Before that, a safety copy of the catalog is made: {0} (free on the drive: {1})", MediaCard.SizeText(plan.CatalogBytes), MediaCard.SizeText(plan.FreeBytes)));
  body.Children.Add(facts);
  body.Children.Add(SettingsUi.Hint(T("Older versions of FlixyPix cannot open the library afterwards. If you cancel during the conversion, the library stays as it was.")));
  footer.Children.Clear();
  if (!plan.HasRoom)
  {
   var warning = new TextBlock { Text = T("There is not enough free space for the safety copy. Free up space on the drive and open the library again."),
    TextWrapping = TextWrapping.Wrap, Foreground = SettingsUi.Gold, Margin = new Thickness(0, 12, 0, 0) };
   body.Children.Add(warning);
   var close = new Button { Content = T("Close"), MinWidth = 96, IsDefault = true, IsCancel = true, Style = (Style)Application.Current.Resources["Primary"] };
   close.Click += (_, _) => DialogResult = false;
   footer.Children.Add(close);
   return;
  }
  var start = new Button { Content = T("Convert now"), MinWidth = 120, IsDefault = true, Style = (Style)Application.Current.Resources["Primary"] };
  var later = new Button { Content = T("Cancel"), MinWidth = 96, IsCancel = true, Margin = new Thickness(8, 0, 0, 0) };
  start.Click += (_, _) => Start();
  later.Click += (_, _) => DialogResult = false;
  footer.Children.Add(start); footer.Children.Add(later);
  Loaded += (_, _) => start.Focus();
 }

 private void ShowProgress()
 {
  Heading();
  var list = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
  foreach (var phase in plan.Phases)
  {
   var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
   var icon = SettingsUi.Icon(((char)0xEA3A).ToString(), 13); icon.Foreground = SettingsUi.Muted; icon.Margin = new Thickness(0, 0, 10, 0); icon.Width = 18;
   var label = new TextBlock { Text = PhaseText(phase), TextWrapping = TextWrapping.Wrap, Foreground = SettingsUi.Muted };
   DockPanel.SetDock(icon, Dock.Left); row.Children.Add(icon); row.Children.Add(label);
   list.Children.Add(row); phaseRows[phase] = (icon, label);
  }
  body.Children.Add(list);
  AutomationProperties.SetName(bar, T("Progress"));
  body.Children.Add(bar); body.Children.Add(detail);
  body.Children.Add(SettingsUi.Hint(T("If you cancel, the library stays as it was.")));
  footer.Children.Clear();
  cancelButton = new Button { Content = T("Cancel"), MinWidth = 96, IsCancel = true };
  cancelButton.Click += (_, _) => Cancel();
  footer.Children.Add(cancelButton);
 }

 private void Cancel()
 {
  if (finished || cancel.IsCancellationRequested) return;
  cancel.Cancel();
  if (cancelButton != null) cancelButton.IsEnabled = false;
  detail.Text = T("Cancelling …");
 }

 private async void Start()
 {
  if (running) return;
  running = true; ShowProgress(); watch.Start();
  var progress = new Progress<CatalogOpenProgress>(Show);
  ProgressShown?.Invoke(this);
  try { opened = await Task.Run(() => new CatalogRepository(databasePath, key, progress, cancel.Token)); }
  catch (Exception error) { failure = error; }
  finished = true;
  DialogResult = opened != null;
 }

 /// <summary>Marks the phases before the current one done and shows how far the current one is.</summary>
 private void Show(CatalogOpenProgress step)
 {
  if (finished) return;
  lastPhase = step.Phase;
  var current = plan.Phases.ToList().IndexOf(step.Phase);
  for (var index = 0; index < plan.Phases.Count; index++)
  {
   var (icon, label) = phaseRows[plan.Phases[index]];
   var done = index < current; var active = index == current;
   icon.Text = ((char)(done ? 0xE73E : active ? 0xE768 : 0xEA3A)).ToString();
   icon.Foreground = done ? SettingsUi.Good : active ? Theme.Brush("Accent") : SettingsUi.Muted;
   label.Foreground = active ? Theme.Brush("Text") : SettingsUi.Muted; label.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
  }
  if (cancel.IsCancellationRequested) return;
  bar.IsIndeterminate = step.Total <= 0;
  var share = step.Total > 0 ? (double)step.Done / step.Total : 0;
  bar.Value = share;
  var remaining = plan.Estimate - watch.Elapsed;
  detail.Text = (step.Total > 0 ? step.Phase == CatalogOpenPhase.SafetyCopy ? T("{0:P0} copied", share) : T("{0:N0} of {1:N0}", step.Done, step.Total) : "")
   + (remaining > TimeSpan.FromSeconds(30) ? (step.Total > 0 ? " · " : "") + T("still {0}", Duration(remaining)) : "");
 }
}
