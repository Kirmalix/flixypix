using FlixyPix.Core;
using System.Windows;
using System.Windows.Controls;
namespace FlixyPix.Desktop;

public partial class MainWindow
{
    private ComboBox? _reviewEditor;
    private bool _syncReviewEditor;
    private void InitializeReviewEditor()
    {
        _reviewEditor = new ComboBox { Margin = new Thickness(0, 8, 0, 8), MinWidth = 150 };
        System.Windows.Automation.AutomationProperties.SetName(_reviewEditor, T("Review status"));
        foreach (var state in Enum.GetValues<ReviewState>()) _reviewEditor.Items.Add(new ComboBoxItem { Content = ReviewLabel(state), Tag = state });
        _reviewEditor.SelectionChanged += (_, _) =>
        {
            if (_syncReviewEditor || _reviewEditor.SelectedItem is not ComboBoxItem { Tag: ReviewState state }) return;
            var ids = SelectedMedia.Select(i => i.Id).ToArray(); if (ids.Length == 0) return;
            ApplyImmediate(ids, ImmediateEditFields.ReviewState, m => m with { ReviewState = state }); Refresh();
        };
        if (SingleActions.Parent is Panel parent) parent.Children.Insert(parent.Children.IndexOf(SingleActions) + 1, _reviewEditor);
    }
    private void UpdateReviewEditor()
    {
        if (_reviewEditor == null) return;
        _syncReviewEditor = true;
        try
        {
            var states = SelectedMedia.Select(i => i.ReviewState).Distinct().ToArray();
            _reviewEditor.IsEnabled = states.Length > 0;
            _reviewEditor.SelectedIndex = states.Length == 1 ? (int)states[0] : -1;
        }
        finally { _syncReviewEditor = false; }
    }
    private void AddReviewFilters(Panel panel)
    {
        panel.Children.Add(Heading(T("Review status")));
        var choices = _reviewStates.Count > 0 ? _reviewStates : _view == "inbox" ? [ReviewState.Unreviewed, ReviewState.NeedsAttention] : [];
        var all = new CheckBox { Content = T("All"), IsChecked = choices.Count == 0 || choices.Count == 4 };
        panel.Children.Add(all);
        var boxes = new List<(ReviewState State, CheckBox Box)>();
        foreach (var state in Enum.GetValues<ReviewState>())
        {
            var box = new CheckBox { Content = ReviewLabel(state), IsChecked = choices.Count == 0 || choices.Contains(state) };
            boxes.Add((state, box)); panel.Children.Add(box);
            box.Click += (_, _) => { _reviewStates = boxes.Where(b => b.Box.IsChecked == true).Select(b => b.State).ToList(); if (_reviewStates.Count == 0) { box.IsChecked = true; _reviewStates = [state]; } all.IsChecked = _reviewStates.Count == 4; _anchor = 0; UpdateFilterBar(); Refresh(); };
        }
        all.Click += (_, _) => { _reviewStates = Enum.GetValues<ReviewState>().ToList(); foreach (var pair in boxes) pair.Box.IsChecked = true; all.IsChecked = true; _anchor = 0; UpdateFilterBar(); Refresh(); };
    }
    private static string ReviewLabel(ReviewState state) => state switch
    { ReviewState.Unspecified => T("Review unspecified"), ReviewState.Unreviewed => T("Unreviewed"), ReviewState.NeedsAttention => T("Needs attention"), _ => T("Reviewed") };
}
