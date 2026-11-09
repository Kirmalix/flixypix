using System.Windows;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

public partial class MainWindow
{
 private ImmediateEditUndo? _immediateUndo;
 private void RememberImmediateUndo(ImmediateEditUndo undo, string message)
 {
  if (undo.Rows.Count == 0) return;
  _immediateUndo = undo;
  ImmediateUndoButton.IsEnabled = true; UpdateDetailsDraftState();
  // In tag mode every key press is an edit: the status line and the undo button say enough, a stack of notices would only cover the grid.
  if (_tagWork) { Status.Text = message; return; }
  ShowToast(message, action: T("Undo"), onAction: () => UndoImmediateAction(undo), seconds: 8);
 }
 private void UndoImmediate(object? sender = null, RoutedEventArgs? e = null)
 {
  if (_immediateUndo is not { } undo) return;
  UndoImmediateAction(undo);
 }
 private void UndoImmediateAction(ImmediateEditUndo undo)
 {
  try
  {
   if (!_repository.UndoImmediateEdit(undo)) { Status.Text = T("Conflict"); return; }
   if (ReferenceEquals(_immediateUndo, undo)) _immediateUndo = null;
   ImmediateUndoButton.IsEnabled = _immediateUndo != null; UpdateDetailsDraftState();
   Refresh(Selected?.Id); Status.Text = T("Undone");
  }
  catch (Exception error) when (error is System.IO.IOException or Microsoft.Data.Sqlite.SqliteException)
  { Status.Text = error.Message; }
 }
 private void ApplyImmediate(IReadOnlyCollection<Guid> ids, ImmediateEditFields fields, Func<MediaItem, MediaItem> change, string? message = null) =>
  RememberImmediateUndo(_repository.ApplyImmediateEdit(ids, fields, change), message ?? T("Saved"));
}
