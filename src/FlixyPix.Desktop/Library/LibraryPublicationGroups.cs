using System.IO;
using System.Windows;
using System.Windows.Controls;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

public partial class MainWindow
{
    private bool IsPublicationGroupView => _browse.Kind == LibraryViewKind.PublicationGroups;
    private Grid? _publicationPage;
    private ListBox? _publicationList, _publicationMembers;
    private Guid? _selectedPublicationGroup;
    private bool _refreshingPublications;
    private Task _publicationOpenTask = Task.CompletedTask;
    private readonly Dictionary<UIElement, Visibility> _publicationCovered = [];
    private readonly Dictionary<string, Button> _publicationActions = [];
    private sealed record PublicationMemberRow(PublicationGroupMember Member, string Label);
    private PublicationGroup? SelectedPublicationGroup => _publicationList?.SelectedItem as PublicationGroup;
    private PublicationGroup? CurrentPublicationGroup() => SelectedPublicationGroup is { } group ? _repository.GetPublicationGroup(group.Id) : null;
    private void HidePublicationGroups()
    {
        if (_publicationPage != null) _publicationPage.Visibility = Visibility.Collapsed;
        foreach (var (element, visibility) in _publicationCovered) element.Visibility = visibility; _publicationCovered.Clear();
    }
    private void RefreshPublicationGroups()
    {
        RememberDetailsDraft(); HideRoleView();
        if (_publicationPage == null)
        {
            _publicationList = new ListBox { DisplayMemberPath = nameof(PublicationGroup.Name), MinWidth = 180, Margin = new Thickness(0, 0, 16, 0) };
            _publicationMembers = new ListBox { DisplayMemberPath = nameof(PublicationMemberRow.Label) };
            _publicationList.SelectionChanged += (_, _) => { if (_refreshingPublications) return; _selectedPublicationGroup = SelectedPublicationGroup?.Id; ShowPublicationMembers(); if (IsPublicationGroupView) RecordView(); };
            _publicationMembers.SelectionChanged += (_, _) => UpdatePublicationActions();
            _publicationMembers.MouseDoubleClick += (_, _) => _publicationOpenTask = OpenPublicationMemberAsync();
            var body = new Grid(); body.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); body.ColumnDefinitions.Add(new() { Width = new GridLength(2, GridUnitType.Star) });
            body.Children.Add(_publicationList); Grid.SetColumn(_publicationMembers, 1); body.Children.Add(_publicationMembers);
            var frame = new WindowFrame(T("Publication groups"), null, body, scroll: false, wrapActions: true);
            void Add(string key, Action action) => _publicationActions[key] = frame.Add(T(key), () => { try { action(); } catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException or Microsoft.Data.Sqlite.SqliteException) { MenuFailed(T(key), error); } });
            Add(TKey("New entry"), () => EditPublicationGroup(true)); Add(TKey("Edit"), () => EditPublicationGroup(false));
            Add(TKey("Add"), ChoosePublicationMembers); Add(TKey("Open"), () => _publicationOpenTask = OpenPublicationMemberAsync());
            Add(TKey("Move up"), () => MovePublicationMember(-1)); Add(TKey("Move down"), () => MovePublicationMember(1));
            Add(TKey("Remove"), () => { if (CurrentPublicationGroup() is { } group && _publicationMembers.SelectedItem is PublicationMemberRow row) { _repository.UpsertPublicationGroup(group with { Members = group.Members.Where(m => m != row.Member).ToList() }); RefreshPublicationGroups(); } });
            Add(TKey("Delete"), () => { if (CurrentPublicationGroup() is { } group && Message.Show(this, group.Name, T("Delete"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No, yes: T("Delete")) == MessageBoxResult.Yes) { _repository.DeletePublicationGroup(group.Id); RefreshPublicationGroups(); } });
            _publicationPage = new Grid(); _publicationPage.SetResourceReference(BackgroundProperty, "WindowBackground"); _publicationPage.Children.Add(frame); Grid.SetRow(_publicationPage, 1); Grid.SetRowSpan(_publicationPage, 4); Panel.SetZIndex(_publicationPage, 20); MediaPane.Children.Add(_publicationPage);
        }
        foreach (UIElement element in MediaPane.Children) if (element != _publicationPage && Grid.GetRow(element) >= 1) { _publicationCovered.TryAdd(element, element.Visibility); element.Visibility = Visibility.Collapsed; }
        _publicationPage.Visibility = Visibility.Visible;
        var keep = _selectedPublicationGroup; var member = (_publicationMembers!.SelectedItem as PublicationMemberRow)?.Member; var text = SearchBox.Text.Trim();
        _refreshingPublications = true;
        try
        {
            var all = _repository.GetPublicationGroups();
            _publicationList!.ItemsSource = all.Where(g => text.Length == 0 || g.Name.Contains(text, StringComparison.CurrentCultureIgnoreCase) || g.Description?.Contains(text, StringComparison.CurrentCultureIgnoreCase) == true).ToArray();
            _publicationList.SelectedItem = _publicationList.Items.Cast<PublicationGroup>().FirstOrDefault(g => g.Id == keep) ?? _publicationList.Items.Cast<PublicationGroup>().FirstOrDefault();
            CountLabel.Text = T("{0:N0} of {1:N0} entries", _publicationList.Items.Count, all.Count);
        }
        finally { _refreshingPublications = false; }
        _selectedPublicationGroup = SelectedPublicationGroup?.Id; ShowPublicationMembers(SelectedPublicationGroup?.Id == keep ? member : null);
        ViewTitle.Text = T("Publication groups"); UpdateNavigation(); AdaptToWidth(); RecordView();
    }
    private void ShowPublicationMembers(PublicationGroupMember? selected = null)
    {
        if (_publicationMembers == null) return;
        _publicationMembers.ItemsSource = SelectedPublicationGroup?.Members.Select(member => new PublicationMemberRow(member, member.Kind == PublicationGroupMemberKind.Video ?
         T("Videos") + " \u00b7 " + (_repository.GetMedia(member.TargetId)?.Title ?? member.TargetId.ToString()) : T("Image sets") + " \u00b7 " + (_repository.GetImageSet(member.TargetId)?.Name ?? member.TargetId.ToString()))).ToArray() ?? [];
        _publicationMembers.SelectedItem = _publicationMembers.Items.Cast<PublicationMemberRow>().FirstOrDefault(r => r.Member == selected) ?? _publicationMembers.Items.Cast<PublicationMemberRow>().FirstOrDefault(); UpdatePublicationActions();
    }
    private void UpdatePublicationActions()
    {
        var group = SelectedPublicationGroup; var index = _publicationMembers?.SelectedIndex ?? -1; var count = _publicationMembers?.Items.Count ?? 0;
        foreach (var (key, button) in _publicationActions) button.IsEnabled = key switch
        { "New entry" => true, "Add" or "Edit" or "Delete" => group != null, "Open" => CanOpenPublicationMember(), "Move up" => index > 0, "Move down" => index >= 0 && index < count - 1, _ => index >= 0 };
    }
    private bool CanOpenPublicationMember() => _publicationMembers?.SelectedItem is PublicationMemberRow row && (row.Member.Kind == PublicationGroupMemberKind.Video ? _repository.GetMedia(row.Member.TargetId) != null : _repository.GetImageSet(row.Member.TargetId)?.MediaIds.Count > 0);
    private void EditPublicationGroup(bool create)
    {
        var group = create ? new PublicationGroup() : CurrentPublicationGroup(); if (group == null) return;
        var fields = Dialogs.Fields(this, T("Publication groups"), (T("Name"), group.Name), (T("Description"), group.Description ?? ""));
        if (fields == null || string.IsNullOrWhiteSpace(fields[0])) return;
        var current = create ? group : _repository.GetPublicationGroup(group.Id); if (current == null) return;
        _selectedPublicationGroup = _repository.UpsertPublicationGroup(current with { Name = fields[0], Description = fields[1] }).Id; RefreshPublicationGroups();
    }
    private void MovePublicationMember(int delta)
    {
        if (CurrentPublicationGroup() is not { } group || _publicationMembers?.SelectedItem is not PublicationMemberRow row) return;
        var members = group.Members.ToList(); var from = members.IndexOf(row.Member); var to = from + delta; if (from < 0 || to < 0 || to >= members.Count) return;
        (members[from], members[to]) = (members[to], members[from]); _repository.UpsertPublicationGroup(group with { Members = members }); RefreshPublicationGroups(); _publicationMembers.SelectedIndex = to;
    }
    private async Task OpenPublicationMemberAsync()
    {
        try
        {
            if (!CanOpenPublicationMember() || _publicationMembers?.SelectedItem is not PublicationMemberRow row) return;
            if (row.Member.Kind == PublicationGroupMemberKind.Video && _repository.GetMedia(row.Member.TargetId) is { } video) { StopSlideshow(); await Play(video); }
            else if (row.Member.Kind == PublicationGroupMemberKind.ImageSet && _repository.GetImageSet(row.Member.TargetId) is { } set) await OpenImageSetAsync(set);
        }
        catch (Exception error) when (IsSetActionFailure(error))
        { MenuFailed(T("Open"), error); }
    }
    private void ChoosePublicationMembers()
    {
        if (CurrentPublicationGroup() is not { } group) return; var repository = _repository;
        var picker = new PublicationGroupMembersWindow(this, repository, group.Members, members => _libraryTasks.Track(token => Task.Run(() => repository.AddPublicationGroupMembers(group.Id, members), token)));
        if (picker.ShowDialog() == true && IsPublicationGroupView) RefreshPublicationGroups();
    }
    private void AddVideosToPublicationGroup(PublicationGroup group, IReadOnlyList<MediaItem> items)
     => _repository.AddPublicationGroupMembers(group.Id, items.Where(m => m.Kind == MediaKind.Video).Select(m => new PublicationGroupMember(PublicationGroupMemberKind.Video, m.Id)));
    private void AddImageSetToPublicationGroup()
    {
        if (CurrentImageSet() is not { } set) return;
        Menus.Open(_setActions["Publication groups"], ImageSetPublicationGroupChoices(set), MenuFailed);
    }
    private IReadOnlyList<MenuSpec> ImageSetPublicationGroupChoices(ImageSet set) => _repository.GetPublicationGroups().Select(group => MenuSpec.Command(group.Name, () => _repository.AddPublicationGroupMembers(group.Id, [new(PublicationGroupMemberKind.ImageSet, set.Id)]))).ToArray();
}
