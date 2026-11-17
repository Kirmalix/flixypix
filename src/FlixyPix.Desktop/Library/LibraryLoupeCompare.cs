using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

/// <summary>Comparing and surveying in place of the tiles. Compare (C): the image on show stays on the left as the
/// reference, the loupe leafs on beside it, and zoom and place follow on both sides. Survey (N): the chosen images side
/// by side; one is selected and stands for the selection, so rating, favorite and removing work on it.</summary>
public partial class MainWindow
{
    private ImageViewer? _reference;
    private SurveyView? _survey;
    /// <summary>The survey an image was opened from: the way back from the loupe leads to it.</summary>
    private SurveyView? _surveyBehindLoupe;
    internal ImageViewer? CompareReference => _reference;
    internal SurveyView? Survey => _survey;

    private Border LoupeHost()
    {
        if (_loupeHost != null) return _loupeHost;
        _loupeHost = new Border { Visibility = Visibility.Collapsed };
        _loupeHost.SetResourceReference(Border.BackgroundProperty, "WindowBackground");
        Grid.SetColumn(_loupeHost, Grid.GetColumn(MediaPane)); Panel.SetZIndex(_loupeHost, 25);
        ((Grid)MediaPane.Parent).Children.Add(_loupeHost);
        _loupeDetails.Tick += (_, _) => { _loupeDetails.Stop(); if (_loupe != null || _survey != null) ShowDetails(); };
        DetailsPane.PreviewMouseDown += (_, _) => FlushLoupeDetails();
        return _loupeHost;
    }

    /// <summary>C in the loupe: the image on show becomes the reference and the next one is shown beside it; again, the
    /// comparison ends and the loupe goes on alone.</summary>
    internal void ToggleCompare()
    {
        if (_reference != null) { EndCompare(); return; }
        if (_loupe is not { CurrentItem: { } current } viewer) return;
        StartCompare(viewer, current);
        if (viewer.CurrentIndex + 1 < viewer.Count) viewer.Navigate(1);
    }

    /// <summary>C on the tiles with two images chosen: the first is the reference, the loupe shows the second.</summary>
    private void CompareSelection()
    {
        var images = SelectedMedia.Where(item => item.Kind == MediaKind.Image).ToArray();
        if (images.Length != 2 || SelectedMedia.Count != 2) { Status.Text = T("Select two images to compare them (C)."); return; }
        var viewer = ShowImage(images[1]);
        if (viewer.Embedded) StartCompare(viewer, images[0]);
    }

    private void StartCompare(ImageViewer viewer, MediaItem reference)
    {
        EndCompare();
        var left = ImageViewer.Reference(reference); ConfigureViewer(left); _reference = left;
        var host = LoupeHost(); host.Child = null;
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); grid.ColumnDefinitions.Add(new ColumnDefinition());
        var divider = new Border { Width = 2 }; divider.SetResourceReference(Border.BackgroundProperty, "Divider");
        Grid.SetColumn(divider, 1); Grid.SetColumn(viewer, 2);
        grid.Children.Add(left); grid.Children.Add(divider); grid.Children.Add(viewer);
        host.Child = grid;
        left.ViewChanged += FollowView; viewer.ViewChanged += FollowView; viewer.CurrentChanged += CandidateMoved;
        viewer.ShowComparing(true);
        Status.Text = T("Comparing with “{0}” · C ends the comparison", reference.Title);
        Dispatcher.BeginInvoke(() => { if (ReferenceEquals(_loupe, viewer)) viewer.Focus(); }, DispatcherPriority.Loaded);
    }

    private void EndCompare()
    {
        if (_reference is not { } left) return;
        _reference = null; left.ViewChanged -= FollowView; left.Close();
        if (Status.Text.StartsWith(T("Comparing with “{0}” · C ends the comparison", left.CurrentItem?.Title ?? ""), StringComparison.Ordinal)) Status.Text = "";
        if (_loupe is { } viewer)
        {
            viewer.ViewChanged -= FollowView; viewer.CurrentChanged -= CandidateMoved; viewer.ShowComparing(false);
            if (viewer.Parent is Grid grid) grid.Children.Remove(viewer);
            if (_loupeHost != null) _loupeHost.Child = viewer;
            Dispatcher.BeginInvoke(() => { if (ReferenceEquals(_loupe, viewer)) viewer.Focus(); }, DispatcherPriority.Loaded);
        }
        else if (_loupeHost != null) _loupeHost.Child = null;
    }

    /// <summary>One side was zoomed or moved: the other takes the same place.</summary>
    private void FollowView(ImageViewer source)
    {
        var other = ReferenceEquals(source, _reference) ? _loupe : _reference;
        if (other != null && source.Place is { } place) other.Follow(place);
    }
    /// <summary>The next candidate starts fitted, and so does the reference beside it.</summary>
    private void CandidateMoved(MediaItem item) { if (_reference is { Fitted: false } left) left.Fit(); }

    /// <summary>N on the tiles: the chosen images side by side.</summary>
    internal bool OpenSurvey()
    {
        var images = SelectedMedia.Where(item => item.Kind == MediaKind.Image).ToArray();
        if (images.Length < 2) { Status.Text = T("Select at least two images for the survey (N)."); return false; }
        CloseLoupe(reselect: false); CloseSurvey(reselect: false); DropParkedSurvey();
        var survey = new SurveyView(images, (item, _) => _libraryTasks.Run<string?>(async token =>
            await EnsureArchiveImageAsync(item, token) is { } ready ? await Task.Run(() => RawPairs.DisplayPath(ready), token) : null));
        survey.SelectedChanged += item => { if (ReferenceEquals(_survey, survey)) LoupeMoved(item); };
        survey.OpenRequested += item => { if (ReferenceEquals(_survey, survey)) ShowImage(item); };
        survey.Closed += (_, _) => SurveyClosed(survey);
        ShowSurvey(survey);
        return true;
    }

    private void ShowSurvey(SurveyView survey)
    {
        if (QuickLook is { } quick) quick.Close();
        _survey = survey; var host = LoupeHost(); host.Child = survey; host.Visibility = Visibility.Visible;
        MediaPane.Visibility = Visibility.Hidden;
        Dispatcher.BeginInvoke(() => { if (ReferenceEquals(_survey, survey)) survey.Focus(); }, DispatcherPriority.Loaded);
    }

    /// <summary>The loupe opens from the survey: the survey waits behind it.</summary>
    private void ParkSurvey()
    {
        if (_survey is not { } survey) return;
        _survey = null; _surveyBehindLoupe = survey;
        if (_loupeHost?.Child == survey) _loupeHost.Child = null;
    }
    /// <summary>Back from the loupe: to the survey it was opened from, if there was one.</summary>
    private bool ReturnToSurvey()
    {
        if (_surveyBehindLoupe is not { } survey) return false;
        _surveyBehindLoupe = null; ShowSurvey(survey);
        if (survey.SelectedItem is { } item) LoupeMoved(item);
        return true;
    }
    private void DropParkedSurvey() { var parked = _surveyBehindLoupe; _surveyBehindLoupe = null; parked?.Close(); }

    internal void CloseSurvey(bool reselect = true)
    {
        if (_survey is not { } survey) return;
        _surveyReselect = reselect; survey.Close();
    }
    private bool _surveyReselect = true;

    private void SurveyClosed(SurveyView survey)
    {
        if (ReferenceEquals(_surveyBehindLoupe, survey)) { _surveyBehindLoupe = null; return; }
        if (!ReferenceEquals(_survey, survey)) return;
        var reselect = _surveyReselect; _surveyReselect = true;
        _survey = null; _loupeDetails.Stop();
        if (_loupeHost != null) { _loupeHost.Child = null; _loupeHost.Visibility = Visibility.Collapsed; }
        MediaPane.Visibility = Visibility.Visible;
        if (_closing || !reselect) return;
        _ = BackToTileAsync(survey.SelectedItem);
    }

    /// <summary>After each refresh of the list: images that left the library leave the survey too.</summary>
    private void SurveyListChanged()
    {
        foreach (var survey in new[] { _survey, _surveyBehindLoupe }.OfType<SurveyView>())
            survey.Refresh(id => _repository.GetMedia(id));
    }

    private bool SurveyTakesKeys(object? source) => _survey != null && !TextEntry(source) && !FilterFlyout.IsOpen && !ViewPopup.IsOpen
        && Keyboard.FocusedElement is not (System.Windows.Controls.ListBoxItem or System.Windows.Controls.ListBox or System.Windows.Controls.TreeViewItem or System.Windows.Controls.TreeView
            or System.Windows.Controls.Slider or System.Windows.Controls.MenuItem or System.Windows.Controls.ContextMenu);

    /// <summary>Keys in the survey: those of the tiles that work on the selected image, then those of the survey.</summary>
    private bool SurveyKey(KeyEventArgs e)
    {
        if (_survey is not { } survey) return false;
        var modifiers = Keyboard.Modifiers;
        if (e.Key is not (Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End)) FlushLoupeDetails();
        var digit = e.Key is >= Key.D0 and <= Key.D5 ? e.Key - Key.D0 : e.Key is >= Key.NumPad0 and <= Key.NumPad5 ? e.Key - Key.NumPad0 : -1;
        if (digit >= 0 && modifiers is ModifierKeys.None or ModifierKeys.Shift && SelectedMedia.Count > 0)
        {
            RateSelection(digit);
            if (modifiers == ModifierKeys.Shift && survey.SelectedItem is { } rated) survey.Select(survey.Items.ToList().FindIndex(item => item.Id == rated.Id) + 1);
            return true;
        }
        if (modifiers == ModifierKeys.None)
            switch (e.Key)
            {
                case Key.F when SelectedMedia is { Count: > 0 } chosen: PinFavorite(chosen); return true;
                case Key.B when SelectedMedia.Count > 0: ToggleTargetCollection(); return true;
                case Key.Delete: RemoveSelected(); return true;
            }
        return survey.HandleKey(e);
    }
}
