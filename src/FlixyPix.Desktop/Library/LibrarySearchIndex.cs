using FlixyPix.Core;
using Microsoft.Data.Sqlite;

namespace FlixyPix.Desktop;

public partial class MainWindow
{
 private Task _searchIndexWarmup = Task.CompletedTask;
 private readonly CancellationTokenSource _searchIndexStop = new();

 /// <summary>After the start and after changes, waiting rows are indexed in the background, so the quick search can use the
 /// trigram index. The catalog is released between portions; browsing never waits for a whole build.</summary>
 private void WarmSearchIndex()
 {
  if (!_searchIndexWarmup.IsCompleted || _closing) return;
  var repository = _repository; var stop = _searchIndexStop.Token;
  long total = 0;
  _searchIndexWarmup = Task.Run(() =>
  {
   try
   {
    total = repository.PendingSearchIndexRows();
    if (total > 0) repository.PrepareSearchIndex(stop);
   }
   catch (Exception error) when (error is ObjectDisposedException or OperationCanceledException or SqliteException) { }
  }, stop);
  ShowSearchIndexProgress(repository, () => total);
 }

 private string? _searchIndexStatus;

 /// <summary>While many rows wait (after a conversion or a large import), the status line says how far the quick search is.
 /// It only replaces its own text, never the message of an operation.</summary>
 private void ShowSearchIndexProgress(CatalogRepository repository, Func<long> total)
 {
  var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
  timer.Tick += (_, _) =>
  {
   var mine = _searchIndexStatus is null || Status.Text == _searchIndexStatus;
   if (_searchIndexWarmup.IsCompleted || _closing)
   {
    timer.Stop();
    if (_searchIndexStatus != null && mine && !_closing) Status.Text = T("Quick search is ready.");
    _searchIndexStatus = null; return;
   }
   long waiting;
   try { waiting = repository.PendingSearchIndexRows(); } catch (Exception error) when (error is ObjectDisposedException or SqliteException) { timer.Stop(); return; }
   if (total() < 5000 || _operation != null || !mine) return;
   Status.Text = _searchIndexStatus = T("Preparing quick search … {0:P0}", 1 - (double)waiting / total());
  };
  timer.Start();
 }
}
