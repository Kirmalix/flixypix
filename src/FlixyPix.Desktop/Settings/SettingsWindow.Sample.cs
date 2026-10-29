using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

/// <summary>Live sample and layout preview of the video thumbnail rubric.</summary>
internal sealed partial class SettingsWindow
{
 /// <summary>Sheet layout and look from the controls of this page, on top of <paramref name="basis"/>.</summary>
 private PreviewPreferences PreviewsFrom(PreviewPreferences basis) => basis with
 {
  SheetColumns = columns.Value, SheetRows = rows.Value, SheetWidth = (int)Math.Round(SheetWidth.Value!.Value),
  SheetFastSeek = SheetFast.IsChecked == true, SheetBorder = (int)Math.Round(SheetBorder.Value!.Value), SheetHeader = SheetHeader.IsChecked == true,
  SheetTimestamp = (SheetTimestamp)Math.Max(0, SheetTimestampChoice.SelectedIndex), SheetSkipPercent = (int)Math.Round(SheetSkip.Value!.Value),
  SheetBackground = SheetBackground.Value, SheetForeground = SheetForeground.Value, SheetFont = SheetFontChoice.SelectedItem as string ?? basis.SheetFont,
 };

 private StackPanel SamplePanel()
 {
  var show = SettingsUi.SmallButton(T("Show the preview on a video")); show.Margin = new Thickness(0); show.HorizontalAlignment = HorizontalAlignment.Left;
  show.Click += (_, _) => ShowSample();
  show.IsEnabled = renderSample != null;
  var panel = new StackPanel { Margin = new Thickness(16, 12, 16, 14) };
  panel.Children.Add(show); panel.Children.Add(sampleNote); panel.Children.Add(samplePreview);
  return panel;
 }

 internal void ShowSample() { sampleShown = true; SampleTask = RenderSampleAsync(); }
 internal bool HasSample => samplePreview.Source != null;
 internal void ScrollToSample() => samplePreview.BringIntoView();

 /// <summary>After the first preview, every change renders again once the values rest for a moment.</summary>
 private void ScheduleSample() { if (!sampleShown || loading) return; sampleTimer.Stop(); sampleTimer.Start(); }

 private async Task RenderSampleAsync()
 {
  if (renderSample == null) return;
  if (!SheetWidth.IsValid || !SheetBorder.IsValid || !SheetSkip.IsValid || !SheetBackground.IsValid || !SheetForeground.IsValid) { sampleNote.Text = T("Please enter valid values first."); return; }
  sampleRun?.Cancel(); var run = sampleRun = new CancellationTokenSource();
  var previews = PreviewsFrom(repository.GetPreferences().Previews);
  sampleNote.Text = T("Creating preview …");
  var watch = Stopwatch.StartNew();
  try
  {
   var path = await renderSample(previews, run.Token);
   if (run.IsCancellationRequested) return;
   if (path == null) { sampleNote.Text = T("No video with a known duration found, or FFmpeg is missing."); return; }
   samplePreview.Source = MediaCard.LoadBitmap(path, 0);
   try { File.Delete(path); } catch (IOException) { }
   sampleNote.Text = T("Created in {0:0.0} s. If a value changes, the preview is created again.", watch.Elapsed.TotalSeconds);
  }
  catch (OperationCanceledException) { }
  catch (Exception failure) when (failure is IOException or InvalidOperationException or ArgumentException) { sampleNote.Text = T("Preview not possible: ") + failure.Message; }
 }

 private void UpdateSheetPreview()
 {
  int c = columns.Value, r = rows.Value;
  sheetCells.Columns = c; sheetCells.Rows = r; sheetCells.Children.Clear();
  for (var scene = 1; scene <= c * r; scene++)
   sheetCells.Children.Add(new Border { Background = SettingsUi.Chip, Margin = new Thickness(1), Child = new TextBlock {
    Text = scene.ToString(CultureInfo.CurrentCulture), FontSize = 10, Foreground = SettingsUi.Muted, HorizontalAlignment = HorizontalAlignment.Center,
    VerticalAlignment = VerticalAlignment.Center } });
  var aspect = c * 16d / (r * 9d); double width = 256, height = width / aspect;
  if (height > 150) { height = 150; width = height * aspect; }
  sheetFrame.Width = width; sheetFrame.Height = height;
  sheetResult.Text = SheetWidth.IsValid && SheetWidth.Value is { } total
   ? T("For 16:9 videos {0:0} × {1:0} pixels", total, Math.Round(total / aspect)) + "\n" + Tn(c * r, "1 scene · {1:0} × {2:0} pixels", "{0} scenes · {1:0} × {2:0} pixels each, spread evenly across the video", total / c, Math.Round(total / c * 9 / 16))
   : T("Please enter a width between 160 and 3840 pixels.");
 }
}
