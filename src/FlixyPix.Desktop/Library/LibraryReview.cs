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
        _reviewEditor = new ComboBox { Margin = new Thickness(0, 0, 0, 10) };
        System.Windows.Automation.AutomationProperties.SetName(_reviewEditor, T("Review status"));
        foreach (var state in Enum.GetValues<ReviewState>()) _reviewEditor.Items.Add(new ComboBoxItem { Content = ReviewLabel(state), Tag = state });
        _reviewEditor.SelectionChanged += (_, _) =>
        {
            if (_syncReviewEditor || _reviewEditor.SelectedItem is not ComboBoxItem { Tag: ReviewState state }) return;
            var ids = SelectedMedia.Select(i => i.Id).ToArray(); if (ids.Length == 0) return;
            ApplyImmediate(ids, ImmediateEditFields.ReviewState, m => m with { ReviewState = state }); Refresh();
        };
        if (SingleActions.Parent is FrameworkElement { Parent: Panel column } actions) column.Children.Insert(column.Children.IndexOf(actions) + 1, _reviewEditor);
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
    private static string ReviewLabel(ReviewState state) => state switch
    { ReviewState.Unspecified => T("Review unspecified"), ReviewState.Unreviewed => T("Unreviewed"), ReviewState.NeedsAttention => T("Needs attention"), _ => T("Reviewed") };
}
