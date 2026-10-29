using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using FlixyPix.MediaTools;
using Microsoft.Win32;

namespace FlixyPix.Desktop;

internal sealed partial class MediaQueueWindow : Window
{
 private readonly MediaJobQueue _queue;
 private readonly MediaToolService _tools;
 private readonly ListView _jobs = new() { Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
 private readonly TextBox _source = new() { IsReadOnly = true, MinWidth = 340 };
 private readonly TextBlock _status = new() { Text = T("Click Start to run queued jobs one at a time."), Margin = new Thickness(0, 14, 0, 0) };
 private readonly Button _start = new() { Content = T("▶ Start") };
 private readonly Button _cancel = new() { Content = T("Cancel"), IsEnabled = false };
 private readonly IReadOnlyList<string> _collectionInputs;
 private CancellationTokenSource? _cancellation;
 private Task _run = Task.CompletedTask;
 private bool _closing;
 private bool _applyingResults;
 private readonly Action<MediaJob>? _applyResults;
 private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
 internal Task ClosedTask => _closed.Task;
 internal int JobCount => _queue.Jobs.Count;
 internal void AddPreparedJob(MediaJob job) => _queue.Enqueue(job);
 internal void AddHistoricalJob(LegacyJobProposal proposal) => LegacyJobTranslator.Enqueue(_queue,proposal,proposal.SourceSha256);
 private sealed record QueueRow(MediaJob Job)
 {
  public string Type => Job.Kind switch { MediaJobKind.Convert => "MP4", MediaJobKind.Clip => T("Clip"), MediaJobKind.Gif => "GIF", MediaJobKind.Crop => T("Crop"), MediaJobKind.ScenePreviews => T("Scene images"), MediaJobKind.ExtractAudio => Tc("player", "Audio"), _ => T("Join") };
  public string Input => Job.Kind is MediaJobKind.Combine or MediaJobKind.CombineSegments ? Tn(Job.Inputs.Count, "1 file", "{0} files") : Path.GetFileName(Job.Input);
  public string Output => Job.Output;
  public string State => Job.State switch { MediaJobState.Pending => T("Waiting"), MediaJobState.Running => T("Running"), MediaJobState.Completed => T("Done"), MediaJobState.Cancelled => T("Cancelled"), _ => T("Error") };
  public string? Error => Job.Error;
 }
 internal MediaQueueWindow(Window owner, string dataDirectory, MediaToolService tools, string? selectedPath, IReadOnlyList<string> collectionInputs, Action<MediaJob>? applyResults = null)
 {
  Owner = owner; Title = T("Media jobs · FlixyPix"); Width = 1120; Height = 680; MinWidth = 850; MinHeight = 500; WindowStartupLocation = WindowStartupLocation.CenterOwner; FontSize = 14;
  _queue = new MediaJobQueue(Path.Combine(dataDirectory, "media-jobs.json"), tools); _tools = tools; _collectionInputs = collectionInputs; _source.Text = selectedPath ?? ""; _applyResults = applyResults;
  var layout = new DockPanel();
  var header = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
  var sourceRow = new DockPanel(); var choose = new Button { Content = T("Choose source file …"), Margin = new Thickness(6, 0, 0, 0) };
   choose.Click += (_, _) => ChooseSource(); DockPanel.SetDock(choose, Dock.Right); sourceRow.Children.Add(choose); sourceRow.Children.Add(_source);
   header.Children.Add(sourceRow);
  var addRow = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
  AddButton(addRow, T("＋ MP4"), () => AddJob(MediaJobKind.Convert)); AddButton(addRow, T("＋ Clip"), () => AddJob(MediaJobKind.Clip));
   AddButton(addRow, T("＋ GIF"), () => AddJob(MediaJobKind.Gif)); AddButton(addRow, T("＋ Crop"), () => AddJob(MediaJobKind.Crop));
   AddButton(addRow, T("＋ Audio …"), () => AddJob(MediaJobKind.ExtractAudio)); AddButton(addRow, T("＋ Random clips …"), AddRandomClips);
   AddButton(addRow, T("＋ Join …"), AddCombine);
  header.Children.Add(addRow); DockPanel.SetDock(header, Dock.Top); layout.Children.Add(header);
  var rowActions = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
  AddButton(rowActions, T("Queue again"), () => { if (_jobs.SelectedItem is QueueRow row) _queue.Retry(row.Job.Id); });
   AddButton(rowActions, T("Remove"), () => { if (_jobs.SelectedItem is QueueRow row) _queue.Remove(row.Job.Id); });
   AddButton(rowActions, T("Error / details"), () => { if (_jobs.SelectedItem is QueueRow row) Dialogs.Text(this, T("Job details"),
   T("Type: {0}\nStatus: {1}\nInput: {2}\nOutput: {3}\n\n{4}", row.Type, row.State, row.Job.Input, row.Output, row.Error)); });
  DockPanel.SetDock(rowActions, Dock.Bottom); layout.Children.Add(rowActions);
  var view = new GridView(); foreach (var column in new[] { (T("Type"), "Type", 100d), (Tc("Spalte", "Input"), "Input", 230d), ("Status", "State",
   110d), (T("Output file"), "Output", 490d) }) view.Columns.Add(new GridViewColumn {
   Header = column.Item1, DisplayMemberBinding = new Binding(column.Item2), Width = column.Item3 });
  _jobs.View = view; layout.Children.Add(_jobs);
  var frame = new WindowFrame(T("Media jobs"), T("Conversions, clips, audio, GIFs, crops, random clips and joined files. Jobs run one after another only after “Start”; source files stay unchanged."), layout, "", scroll: false, status: _status);
  _start.Click += async (_, _) => await Start(); _cancel.Click += (_, _) => _cancellation?.Cancel();
  _start.Style = (Style)Application.Current.Resources["Primary"]; _start.Margin = _cancel.Margin = new Thickness(8, 0, 0, 0);
  frame.Buttons.Children.Add(_start); frame.Buttons.Children.Add(_cancel); frame.Add(T("Close"), Close);
  Content = frame;
  _queue.Changed += QueueChanged; _queue.ProgressChanged += ProgressChanged; Closing += OnClosing; Closed += (_, _) => _closed.TrySetResult(); RefreshJobs();
 }
 private void AddButton(Panel panel, string label, Action action)
 {
  var button = new Button { Content = label }; button.Click += (_, _) => { try { action(); } catch (Exception ex) { Message.Show(this, ex.Message, label, MessageBoxButton.OK, MessageBoxImage.Warning); } }; panel.Children.Add(button);
 }
 private void AddButton(Panel panel, string label, Func<Task> action)
 {
  var button = new Button { Content = label };
  button.Click += async (_, _) =>
  {
   button.IsEnabled = false;
   try { await action(); }
   catch (Exception ex) { _status.Text = ""; Message.Show(this, ex.Message, label, MessageBoxButton.OK, MessageBoxImage.Warning); }
   finally { button.IsEnabled = true; }
  };
  panel.Children.Add(button);
 }
 private void ChooseSource() { var picker = new OpenFileDialog { AddToRecent = Privacy.AddToRecent, Title = T("Choose source file"), Filter = T("Media|*.mp4;*.mkv;*.mov;*.avi;*.webm;*.mp3;*.wav;*.flac;*.png;*.jpg|All files|*.*") }; if (picker.ShowDialog(this) == true) _source.Text = picker.FileName; }
 private void AddJob(MediaJobKind kind)
 {
  var job = new MediaJob { Kind = kind, Input = _source.Text };
  if (!File.Exists(job.Input)) { ChooseSource(); if (!File.Exists(_source.Text)) return; job = job with { Input = _source.Text }; }
  if (kind is MediaJobKind.Clip or MediaJobKind.Gif)
  {
   var values = Dialogs.Fields(this, T("Time range"), (T("Start in seconds"), "0"), (T("Length in seconds"), "5")); if (values == null) return;
   if (!MainWindow.TrySeconds(values[0], out var start) || !MainWindow.TrySeconds(values[1], out var length) || start < 0 || length <= 0) throw new InvalidOperationException(T("The start must be at least 0 and the length greater than 0."));
   job = job with { StartSeconds = start, DurationSeconds = length };
  }
  if (kind == MediaJobKind.ExtractAudio)
  {
   var values = Dialogs.Fields(this, T("Extract audio"), (T("Start in seconds"), "0"), (T("Length in seconds (0 = to the end)"), "0")); if (values == null) return;
   if (!MainWindow.TrySeconds(values[0], out var start) || !MainWindow.TrySeconds(values[1], out var length) || start < 0 || length < 0) throw new InvalidOperationException(T("Start and length must not be negative."));
   job = job with { StartSeconds = start, DurationSeconds = length };
  }
  if (kind == MediaJobKind.Crop)
  {
   var values = Dialogs.Fields(this, T("Crop video"), (T("Left · X in pixels"), "0"), (T("Top · Y in pixels"), "0"), (T("Width in pixels"), "640"), (T("Height in pixels"), "360")); if (values == null) return;
   if (!int.TryParse(values[0], out var x) || !int.TryParse(values[1], out var y) || !int.TryParse(values[2],
    out var width) || !int.TryParse(values[3], out var height) || x < 0 || y < 0 || width <= 0
    || height <= 0) throw new InvalidOperationException(T("Please enter a valid image area with a positive width and height."));
   job = job with { X = x, Y = y, Width = width, Height = height };
  }
  var picker = new SaveFileDialog { AddToRecent = Privacy.AddToRecent, Title = T("New output file"), Filter = kind switch { MediaJobKind.Gif => T("GIF|*.gif"), MediaJobKind.ExtractAudio => T("MP3|*.mp3|M4A|*.m4a"), _ => T("MP4|*.mp4") },
   FileName = Path.GetFileNameWithoutExtension(job.Input) + "-" + (kind == MediaJobKind.ExtractAudio ? "audio" : kind.ToString().ToLowerInvariant()) };
  if (picker.ShowDialog(this) != true) return;
  _queue.Enqueue(job with { Output = picker.FileName }); _status.Text = T("Job saved. Click Start to run queued jobs one at a time.");
 }
 private void QueueChanged(object? sender, EventArgs e) { if (!_closing) _ = Dispatcher.BeginInvoke(RefreshJobs); }
 private void ProgressChanged(object? sender, MediaJobProgress progress)
 { if (!_closing) _ = Dispatcher.BeginInvoke(() => _status.Text = T("Scene images: {0} / {1}", progress.Completed, progress.Total)); }
 private void RefreshJobs()
 {
  if (_closing) return;
  if (!_applyingResults && _applyResults != null)
  {
   _applyingResults = true;
   try
   {
    foreach (var job in _queue.Jobs.Where(j => j.Kind == MediaJobKind.ScenePreviews && j.State == MediaJobState.Completed && !j.ResultsApplied))
    {
     try { _applyResults(job); _queue.MarkResultsApplied(job.Id); }
     catch (Exception ex) { _status.Text = T("Scene images were created but are not in the catalog yet: ") + ex.Message; }
    }
   }
   finally { _applyingResults = false; }
  }
  var selected = (_jobs.SelectedItem as QueueRow)?.Job.Id;
  var rows = _queue.Jobs.Select(job => new QueueRow(job)).ToList(); _jobs.ItemsSource = rows; _jobs.SelectedItem = rows.FirstOrDefault(row => row.Job.Id == selected);
 }
 private async Task Start()
 {
  if (!_run.IsCompleted) return;
  _cancellation = new CancellationTokenSource(); _start.IsEnabled = false; _cancel.IsEnabled = true; _status.Text = T("Jobs running …");
  try { _run = _queue.RunAsync(_cancellation.Token); await _run; _status.Text = T("Run finished. Errors are listed in the job details."); }
  catch (OperationCanceledException) { _status.Text = T("Run cancelled."); }
  catch (Exception ex) { _status.Text = ex.Message; }
  finally { _cancellation.Dispose(); _cancellation = null; _start.IsEnabled = true; _cancel.IsEnabled = false; }
 }
 private async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
 {
  if (_closing) return;
  e.Cancel = true; _closing = true; _cancellation?.Cancel();
  try { await _run; } catch { }
  _queue.Changed -= QueueChanged; _queue.ProgressChanged -= ProgressChanged; _queue.Dispose(); _ = Dispatcher.BeginInvoke(Close);
 }
}
