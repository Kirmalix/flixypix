using System.IO;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using FlixyPix.Core;
using FlixyPix.MediaTools;
using Microsoft.Win32;

namespace FlixyPix.Desktop;

public partial class MainWindow
{
    private void ShowHistoricalFunctions() => new HistoricalFunctionsWindow(this, _repository,
        MediaList.SelectedItems.OfType<MediaCard>().Select(value => value.Item.Id).ToArray(), async proposal =>
        {
            OpenQueue();
            if (_queueWindow == null) throw new InvalidOperationException(T("The queue could not be opened."));
            _queueWindow.AddHistoricalJob(proposal);
            await Task.CompletedTask;
        }, () => RefreshAsync(includePreviews: false)).ShowDialog();
}

internal sealed class HistoricalFunctionsWindow : Window
{
    private readonly CatalogRepository catalog;
    private readonly IReadOnlyCollection<Guid> selected;
    private readonly Func<LegacyJobProposal, Task> enqueue;
    private readonly Func<Task> refresh;
    private readonly ComboBox searches = new() { DisplayMemberPath = "Name", MinWidth = 350 };
    private readonly ComboBox parsers = new() { DisplayMemberPath = "LegacyId", MinWidth = 350 };
    private readonly ComboBox activeParsers = new() { DisplayMemberPath = "Name", MinWidth = 350 };
    private readonly ComboBox jobs = new() { DisplayMemberPath = "LegacyId", MinWidth = 350 };
    private readonly ComboBox combines = new() { MinWidth = 350 };
    internal int SearchDraftCount => searches.Items.Count;
    internal int ParserDraftCount => parsers.Items.Count;
    internal int JobDraftCount => jobs.Items.Count;

    internal HistoricalFunctionsWindow(Window owner, CatalogRepository catalog, IReadOnlyCollection<Guid> selected,
        Func<LegacyJobProposal, Task> enqueue, Func<Task> refresh)
    {
        Owner = owner; this.catalog = catalog; this.selected = selected; this.enqueue = enqueue; this.refresh = refresh;
        Title = T("Historical functions · FlixyPix"); Width = 860; Height = 760; MinWidth = 700; MinHeight = 480; WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        var panel = new StackPanel();
        Section(panel, T("Saved and recent searches"), searches, T("Check translation …"), SearchAsync);
        Section(panel, T("Historical file name parsers"), parsers, T("Check formatting and translation …"), ActivateParserAsync);
        Section(panel, Tn(selected.Count, "Imported parsers · 1 selected item", "Imported parsers · {0} selected items"), activeParsers, T("Check changes to the selected items …"), ApplyParserAsync);
        Section(panel, T("Historical media jobs"), jobs, T("Check new job …"), JobAsync);
        Section(panel, T("Combine lists"), combines, T("Check join …"), CombineAsync);
        var more = new WrapPanel();
        Button(more, T("Read old Combine list (.ezl) …"), ImportCombineAsync);
        Button(more, T("Check content folders and categories for the selected items …"), FolderCategoriesAsync);
        panel.Children.Add(SettingsUi.Card(T("More legacy data"), null, new Border { Padding = new Thickness(16, 10, 16, 12), Child = more }));
        var frame = new WindowFrame(T("Import historical functions"), T("The historical original data is kept. Every import shows a preview first; media jobs are only queued and not started here."), panel, "");
        frame.Add(T("Close"), Close, primary: true, cancel: true);
        Content = frame;
        Reload();
    }
    private void Reload()
    {
        searches.ItemsSource = catalog.GetLegacySearchDrafts(); searches.SelectedIndex = 0;
        parsers.ItemsSource = catalog.GetLegacyParserDrafts(); parsers.SelectedIndex = 0;
        activeParsers.ItemsSource = catalog.GetHistoricalParserDefinitions(); activeParsers.SelectedIndex = 0;
        jobs.ItemsSource = catalog.GetLegacyJobDrafts(); jobs.SelectedIndex = 0;
        combines.DisplayMemberPath = "Name"; combines.ItemsSource = catalog.GetLegacyCombineLists(); combines.SelectedIndex = 0;
    }
    private void Section(Panel panel, string title, Control choices, string action, Func<Task> work)
    {
        var row = new DockPanel { Margin = new Thickness(16, 10, 16, 12) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 0, 0, 0) }; Button(actions, action, work);
        DockPanel.SetDock(actions, Dock.Right); row.Children.Add(actions); row.Children.Add(choices);
        panel.Children.Add(SettingsUi.Card(title, null, row));
    }
    private void Button(Panel panel, string label, Func<Task> action)
    {
        var button = new Button { Content = label, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 8, 0) };
        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            try { await action(); }
            catch (Exception error) { Message.Show(this, error.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning); }
            finally { button.IsEnabled = true; }
        };
        panel.Children.Add(button);
    }
    private bool Review(string title, string body, string action, bool available = true)
    {
        var window = new Window { Owner = this, Title = title, Width = 800, Height = 560, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
        var frame = new WindowFrame(title, available ? T("Please review the preview; changes are applied only after confirmation.") : T("This cannot be imported as it is. The preview explains why."),
            new TextBox { Text = body, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(8) }, scroll: false);
        frame.Add(action, () => window.DialogResult = true, primary: true).IsEnabled = available; frame.Add(T("Cancel"), window.Close, cancel: true);
        window.Content = frame;
        return window.ShowDialog() == true;
    }
    private async Task SearchAsync()
    {
        if (searches.SelectedItem is not LegacySearchDraft draft) return;
        var plan = await Task.Run(() => catalog.PreviewLegacySearchTranslation(draft.Id));
        var body = draft.Name + "\n\n" + string.Join('\n', plan.Notes) + "\n\n" + string.Join('\n', plan.Blockers);
        if (Review(T("Import search preset"), body, T("Create saved search"), plan.CanApply))
        { catalog.ApplyLegacySearchTranslation(plan); await refresh(); Message.Show(this, T("The search is available under saved searches.")); }
    }
    private Task ActivateParserAsync()
    {
        if (parsers.SelectedItem is not LegacyParserDraft draft) return Task.CompletedTask;
        var fields = Dialogs.Fields(this, T("Set the formatting for this parser"),
            (T("Language/culture for text and numbers"), CultureInfo.CurrentCulture.Name),
            (T("Excluded text fragments (separated by semicolons)"), ""), (T("Exception pattern"), ""), (T("Reserved separator"), ""));
        if (fields == null) return Task.CompletedTask;
        var context = new HistoricalTagFormatting { Confirmed = true, SourceDescription = T("Formatting checked in the import dialog"),
            CultureName = fields[0], ExcludedSubstrings = fields[1], BypassPatterns = fields[2], ReservedDelimiter = fields[3] };
        var plan = catalog.PreviewLegacyParserTranslation(draft.Id, context, catalog.GetFilenameTagRules());
        if (Review(T("Parser translation"), string.Join('\n', plan.Warnings) + "\n\n" + string.Join('\n', plan.Blockers), T("Import parser"), plan.CanActivate))
        { catalog.ActivateLegacyParserTranslation(plan); Reload(); }
        return Task.CompletedTask;
    }
    private async Task ApplyParserAsync()
    {
        if (activeParsers.SelectedItem is not HistoricalParserDefinition parser || selected.Count == 0) return;
        var plan = await Task.Run(() => catalog.PreviewHistoricalParser(parser.Id, selected));
        var body = string.Join("\n\n", plan.Changes.Select(change => change.Before.Path + T("\nTitle: ") + change.Before.Title + " → " + change.After.Title +
            T("\nTags before: ") + string.Join(", ", change.Before.Tags) + T("\nTags after: ") + string.Join(", ", change.After.Tags)));
        if (Review(T("Check parser changes"), body, T("Apply changes"), plan.Changes.Count > 0))
        { await Task.Run(() => catalog.ApplyHistoricalParser(plan)); await refresh(); }
    }
    private string? NewOutput()
    {
        var picker = new SaveFileDialog { AddToRecent = Privacy.AddToRecent, Title = T("Choose new output file"), Filter = T("MP4 video|*.mp4"), FileName = "FlixyPix-Ausgabe.mp4" };
        return picker.ShowDialog(this) == true ? picker.FileName : null;
    }
    private async Task JobAsync()
    {
        if (jobs.SelectedItem is not LegacyJobDraft job || NewOutput() is not { } output) return;
        await ReviewJob(LegacyJobTranslator.PreviewSql(job, output), () => LegacyJobTranslator.Fingerprint(catalog.GetLegacyJobDrafts().Single(value => value.Id == job.Id)));
    }
    private async Task CombineAsync()
    {
        if (combines.SelectedItem is not LegacyCombineList list || NewOutput() is not { } output) return;
        await ReviewJob(LegacyJobTranslator.PreviewCombine(list, output), () => catalog.GetLegacyCombineLists().Single(value => value.Id == list.Id).PayloadSha256);
    }
    private async Task ReviewJob(LegacyJobProposal proposal, Func<string> sourceHash)
    {
        var body = string.Join('\n', proposal.Warnings) + "\n\n" + string.Join('\n', proposal.BlockingIssues);
        if (proposal.Job is { } job) body = T("New output: ") + job.Output + "\n\n" + body;
        if (Review(T("Check media job"), body, T("Add to the queue"), proposal.CanEnqueue))
        {
            if (sourceHash() != proposal.SourceSha256) throw new InvalidOperationException(T("The source has changed in the meantime. Please create the preview again."));
            await enqueue(proposal);
        }
    }
    private async Task ImportCombineAsync()
    {
        var picker = new OpenFileDialog { AddToRecent = Privacy.AddToRecent, Title = T("Read historical Combine list"), Filter = T("Combine list|*.ezl") };
        if (picker.ShowDialog(this) != true) return;
        await Task.Run(() => catalog.ImportLegacyCombineList(picker.FileName, "explicit-combine-file")); Reload();
    }
    private async Task FolderCategoriesAsync()
    {
        if (selected.Count == 0) { Message.Show(this, T("Please select items in the library first.")); return; }
        var plan = await Task.Run(() => catalog.PreviewHistoricalContentFolderCategories(selected));
        var body = string.Join('\n', plan.Notes) + "\n\n" + string.Join('\n', plan.Blockers) + "\n\n" +
            string.Join('\n', plan.Changes.Select(change => change.Path + "\n" + change.CurrentCategory + " → " + change.ProposedCategory));
        if (Review(T("Content folder assignment"), body, T("Import catalog categories"), plan.CanApply && plan.Changes.Count > 0))
        { await Task.Run(() => catalog.ApplyHistoricalContentFolderCategories(plan)); await refresh(); }
    }
}
