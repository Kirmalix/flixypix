using System.IO;
using System.Windows.Threading;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

public partial class MainWindow
{
    private async Task RunViewRestoreSmokeAsync(string root)
    {
        foreach (var encrypted in new[] { false, true })
        {
            var library = Path.Combine(root, encrypted ? "view-encrypted" : "view-plain");
            var items = Enumerable.Range(0, 650).Select(i => new MediaItem { Path = Path.Combine(library, i + ".mp4"), Title = "Restore " + i.ToString("D4"), Kind = MediaKind.Video, Rating = 8 }).ToArray();
            using (var db = new CatalogRepository(Path.Combine(library, "catalog.db")))
            {
                foreach (var item in items) db.Upsert(item);
                var preferences = db.GetPreferences();
                db.SavePreferences(preferences with { Home = preferences.Home with { StartOnHome = false }, General = preferences.General with { RestoreLastView = true, RestoreScope = RestoreScope.All } });
                db.SaveViewState(new ViewState { View = "Video", SearchText = "Restore", MinimumStars = 3, SortOrder = 0, Position = 620, AnchorId = items[620].Id, Selection = [items[0].Id, items[620].Id] });
            }
            if (encrypted) CatalogEncryption.EnableEncryption(Path.Combine(library, "catalog.db"), MetadataCrashProbe.Password);
            using var key = encrypted ? CatalogEncryption.Unlock(Path.Combine(library, "catalog.db"), MetadataCrashProbe.Password) : null;
            for (var round = 0; round < 3; round++)
            {
                var window = new MainWindow(new DesktopSettings { DataDirectory = library, AutoGeneratePreviews = false }, persistPreferences: false, unlockedKey: key);
                var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                window.Closed += (_, _) => closed.TrySetResult();
                try
                {
                    window.Show(); await window.Startup; await window._lastRefresh;
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    if (window._view != "Video" || window.SearchBox.Text != "Restore" || window.RatingFilter.SelectedIndex != 3)
                        throw new InvalidOperationException("Reopening lost media area, search or rating filter.");
                    if (round < 2 && (!window.SelectedMedia.Select(i => i.Id).ToHashSet().SetEquals(new[] { items[0].Id, items[620].Id }) || window.OffWindowSelectionCount != 1 || window._visibleLast < 620))
                        throw new InvalidOperationException("Reopening lost distant selection or saved position.");
                    if (round == 1)
                    {
                        await window.RestorePlaceAsync(window.CaptureViewState() with { Selection = [] });
                        if (window.SelectedMedia.Count != 0) throw new InvalidOperationException("Restoring empty selection retained old media.");
                    }
                    if (round == 2 && window.SelectedMedia.Count != 0) throw new InvalidOperationException("Empty selection did not survive restart.");
                    if (round == 0) Capture(window, Path.Combine(root, "view-restore-" + encrypted + ".png"));
                }
                finally { window.Close(); await closed.Task.WaitAsync(TimeSpan.FromSeconds(30)); }
            }
        }
        File.WriteAllText(Path.Combine(root, "view-restore-result.txt"), "PASS: plain and encrypted independent catalogs, 650 media, actual close/reopen, filters, position, distant and empty selection.");
    }
}
