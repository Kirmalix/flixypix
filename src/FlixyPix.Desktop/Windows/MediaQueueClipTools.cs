using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using FlixyPix.MediaTools;
using Microsoft.Win32;

namespace FlixyPix.Desktop;

internal enum CombineRange { Whole, Fixed, Random }

/// <summary>Choices of the combine dialog. Like the original's video tools, one start and length apply to every video;
/// the random start uses a seed so the queued job keeps the ranges that were planned.</summary>
internal sealed record CombinePlan(CombineRange Range = CombineRange.Whole, double StartSeconds = 0, double LengthSeconds = 10, bool MuteAudio = false, double CrossfadeSeconds = 0, int Seed = 0);

internal sealed partial class MediaQueueWindow
{
 /// <summary>Builds the combine job: whole, matching files are joined losslessly; ranges, crossfade or differing files
 /// use the normalized path with re-encoding.</summary>
 internal async Task<MediaJob> PlanCombineAsync(IReadOnlyList<string> inputs, CombinePlan plan, CancellationToken cancellationToken = default)
 {
  if (inputs.Count < 2) throw new InvalidOperationException(T("Please select at least two files."));
  var options = new CombineOptions(plan.MuteAudio, plan.CrossfadeSeconds); options.Validate(inputs.Count);
  var probes = new List<MediaProbe>();
  foreach (var input in inputs) probes.Add(await _tools.ProbeAsync(input, cancellationToken));
  if (probes.Any(probe => probe.VideoCodec is null || probe.DurationSeconds is not > 0)) throw new InvalidOperationException(T("Choose only videos with a known length for joining."));
  var matching = probes.All(probe => probe.Width == probes[0].Width && probe.Height == probes[0].Height && probe.VideoCodec == probes[0].VideoCodec && (plan.MuteAudio || probe.AudioCodec == probes[0].AudioCodec));
  if (plan.Range == CombineRange.Whole && !options.Crossfade && matching)
   return new MediaJob { Kind = MediaJobKind.Combine, Input = inputs[0], Inputs = inputs.ToList(), MuteAudio = plan.MuteAudio };
  var videos = inputs.Zip(probes, (input, probe) => (input, probe.DurationSeconds!.Value)).ToList();
  var segments = plan.Range switch
  {
   CombineRange.Fixed => ClipPlanner.Fixed(videos, plan.StartSeconds, plan.LengthSeconds),
   CombineRange.Random => ClipPlanner.Random(videos, plan.LengthSeconds, plan.Seed),
   _ => videos.Select(video => new CombineSegment(video.input)).ToList()
  };
  return new MediaJob { Kind = MediaJobKind.CombineSegments, Input = inputs[0], Inputs = inputs.ToList(), Segments = segments.ToList(), MuteAudio = plan.MuteAudio, CrossfadeSeconds = plan.CrossfadeSeconds };
 }

 /// <summary>One clip job per video with a random start, written next to each other into <paramref name="folder"/>.</summary>
 internal async Task<List<MediaJob>> PlanRandomClipsAsync(IReadOnlyList<string> inputs, double lengthSeconds, bool muteAudio, string folder, int seed, CancellationToken cancellationToken = default)
 {
  var videos = new List<(string, double)>();
  foreach (var input in inputs)
  {
   var probe = await _tools.ProbeAsync(input, cancellationToken);
   if (probe.VideoCodec is null || probe.DurationSeconds is not > 0) throw new InvalidOperationException(T("No video length for {0}.", Path.GetFileName(input)));
   videos.Add((input, probe.DurationSeconds.Value));
  }
  var planned = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var jobs = new List<MediaJob>();
  foreach (var (segment, duration) in ClipPlanner.Random(videos, lengthSeconds, seed).Zip(videos, (segment, video) => (segment, video.Item2)))
  {
   var start = TimeSpan.FromSeconds(segment.StartSeconds);
   var name = Path.GetFileNameWithoutExtension(segment.Input) + "-clip-" + start.ToString(start.TotalHours >= 1 ? @"h\-mm\-ss" : @"mm\-ss", CultureInfo.InvariantCulture);
   var output = Path.Combine(folder, name + ".mp4");
   for (var number = 2; File.Exists(output) || !planned.Add(output); number++) output = Path.Combine(folder, $"{name} ({number}).mp4");
   jobs.Add(new MediaJob { Kind = MediaJobKind.Clip, Input = segment.Input, Output = output, StartSeconds = segment.StartSeconds,
    DurationSeconds = (segment.EndSeconds ?? duration) - segment.StartSeconds, MuteAudio = muteAudio });
  }
  return jobs;
 }

 private async Task AddCombine()
 {
  var choice = CombineDialog(_collectionInputs); if (choice == null) return;
  _status.Text = T("Checking videos …");
  var job = await PlanCombineAsync(choice.Value.Inputs, choice.Value.Plan);
  _status.Text = job.Kind == MediaJobKind.Combine ? T("The files match and are joined without converting.") : T("The sections are re-encoded uniformly (MP4, H.264).");
  var picker = new SaveFileDialog { AddToRecent = Privacy.AddToRecent, Title = T("New output file"), Filter = T("MP4|*.mp4"), FileName = Path.GetFileNameWithoutExtension(job.Input) + "-combine" };
  if (job.Kind == MediaJobKind.Combine) { var extension = Path.GetExtension(job.Input); picker.Filter = extension.TrimStart('.').ToUpperInvariant() + "|*" + extension; }
  if (picker.ShowDialog(this) != true) return;
  _queue.Enqueue(job with { Output = picker.FileName }); _status.Text = T("Job saved. Click Start to run queued jobs one at a time.");
 }

 private async Task AddRandomClips()
 {
  var length = new TextBox { Text = "10", Width = 80 }; var mute = new CheckBox { Content = T("Mute audio") };
  var options = OptionRow((T("Length in seconds"), length), (null, mute));
  var inputs = InputsDialog(_collectionInputs, 1, T("Random clips · videos"), T("Each video becomes a clip with a random start. Shorter videos are used in full."), options,
   () => MainWindow.TrySeconds(length.Text, out var seconds) && seconds > 0 ? null : T("The length must be greater than 0."));
  if (inputs == null) return;
  var folder = new OpenFolderDialog { AddToRecent = Privacy.AddToRecent, Title = T("Destination folder for the clips") }; if (folder.ShowDialog(this) != true) return;
  _ = MainWindow.TrySeconds(length.Text, out var clipLength);
  _status.Text = T("Checking videos …");
  var jobs = await PlanRandomClipsAsync(inputs, clipLength, mute.IsChecked == true, folder.FolderName, Random.Shared.Next());
  foreach (var job in jobs) _queue.Enqueue(job);
  _status.Text = Tn(jobs.Count, "1 clip queued. Click Start to run queued jobs one at a time.", "{0} clips queued. Click Start to run queued jobs one at a time.");
 }

 private (List<string> Inputs, CombinePlan Plan)? CombineDialog(IReadOnlyList<string> initial)
 {
  var range = new ComboBox { Width = 210, ItemsSource = new[] { T("Whole files"), T("Same start for all"), T("Random start") }, SelectedIndex = 0 };
  var start = new TextBox { Text = "0", Width = 80 }; var length = new TextBox { Text = "10", Width = 80 };
  var mute = new CheckBox { Content = T("Mute audio") }; var fade = new CheckBox { Content = T("Cross-fade") }; var fadeSeconds = new TextBox { Text = "1", Width = 60 };
  void Update() { start.IsEnabled = range.SelectedIndex == 1; length.IsEnabled = range.SelectedIndex > 0; fadeSeconds.IsEnabled = fade.IsChecked == true; }
  range.SelectionChanged += (_, _) => Update(); fade.Checked += (_, _) => Update(); fade.Unchecked += (_, _) => Update(); Update();
  var options = new StackPanel();
  options.Children.Add(OptionRow((T("Section"), range), (T("Start in seconds"), start), (T("Length in seconds"), length)));
  options.Children.Add(OptionRow((null, mute), (null, fade), (T("Seconds"), fadeSeconds)));
  string? Validate()
  {
   if (range.SelectedIndex > 0 && (!MainWindow.TrySeconds(length.Text, out var seconds) || seconds <= 0)) return T("The length must be greater than 0.");
   if (range.SelectedIndex == 1 && (!MainWindow.TrySeconds(start.Text, out var from) || from < 0)) return T("The start must be at least 0 and the length greater than 0.");
   if (fade.IsChecked == true && (!double.TryParse(fadeSeconds.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var crossfade) || crossfade is <= 0 or > CombineOptions.MaximumCrossfadeSeconds))
    return T("The crossfade must be between 0 and 5 seconds.");
   return null;
  }
  var inputs = InputsDialog(initial, 2, T("Join files · order"),
   T("Order from top to bottom. Whole files with the same codecs and dimensions are joined without converting; everything else is re-encoded uniformly."), options, Validate);
  if (inputs == null) return null;
  if (fade.IsChecked == true && inputs.Count > CombineOptions.MaximumCrossfadeParts) throw new InvalidOperationException(T("With crossfade, at most {0} videos can be joined.", CombineOptions.MaximumCrossfadeParts));
  _ = MainWindow.TrySeconds(start.Text, out var startSeconds); _ = MainWindow.TrySeconds(length.Text, out var lengthSeconds);
  var crossfadeSeconds = fade.IsChecked == true ? double.Parse(fadeSeconds.Text.Replace(',', '.'), CultureInfo.InvariantCulture) : 0;
  return (inputs, new CombinePlan((CombineRange)range.SelectedIndex, startSeconds, lengthSeconds, mute.IsChecked == true, crossfadeSeconds, Random.Shared.Next()));
 }

 private static WrapPanel OptionRow(params (string? Label, FrameworkElement Control)[] items)
 {
  var row = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
  foreach (var (label, control) in items)
  {
   if (label != null) row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
   control.Margin = new Thickness(0, 0, 18, 0); control.VerticalAlignment = VerticalAlignment.Center; row.Children.Add(control);
  }
  return row;
 }

 /// <summary>Ordered list of input files with add, move and remove; <paramref name="validate"/> returns an error text or null.</summary>
 private List<string>? InputsDialog(IReadOnlyList<string> initial, int minimum, string title, string explanation, FrameworkElement? options = null, Func<string?>? validate = null)
 {
  var paths = new ObservableCollection<string>(initial.Where(File.Exists)); var list = new ListBox { ItemsSource = paths, MinHeight = 200 };
  var panel = new DockPanel { Margin = new Thickness(20) };
  var text = new TextBlock { Text = explanation, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) }; DockPanel.SetDock(text, Dock.Top); panel.Children.Add(text);
  var actions = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
  var dialog = new Window { Owner = this, ShowInTaskbar = false, Title = title, Width = 780, Height = options == null ? 460 : 560, Content = panel, WindowStartupLocation = WindowStartupLocation.CenterOwner };
  AddButton(actions, T("Add files …"), () => { var picker = new OpenFileDialog { AddToRecent = Privacy.AddToRecent, Multiselect = true, Title = T("Add media files") }; if (picker.ShowDialog(dialog) == true) foreach (var path in picker.FileNames) paths.Add(path); });
  AddButton(actions, "↑", () => { var index = list.SelectedIndex; if (index > 0) { paths.Move(index, index - 1); list.SelectedIndex = index - 1; } });
  AddButton(actions, "↓", () => { var index = list.SelectedIndex; if (index >= 0 && index < paths.Count - 1) { paths.Move(index, index + 1); list.SelectedIndex = index + 1; } });
  AddButton(actions, T("Remove"), () => { if (list.SelectedIndex >= 0) paths.RemoveAt(list.SelectedIndex); });
  var accept = new Button { Content = T("Apply"), IsDefault = true };
  accept.Click += (_, _) =>
  {
   var error = paths.Count < minimum ? (minimum == 1 ? T("Please select at least one file.") : T("Please select at least two files.")) : validate?.Invoke();
   if (error == null) dialog.DialogResult = true; else Message.Show(dialog, error);
  };
  actions.Children.Add(accept); DockPanel.SetDock(actions, Dock.Bottom); panel.Children.Add(actions);
  if (options != null) { DockPanel.SetDock(options, Dock.Bottom); panel.Children.Add(options); }
  panel.Children.Add(list);
  return dialog.ShowDialog() == true ? paths.ToList() : null;
 }
}
