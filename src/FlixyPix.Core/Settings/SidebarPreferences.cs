namespace FlixyPix.Core;

public sealed record SidebarPreferences
{
 public List<string> CollapsedSections { get; init; } = [];
 public List<string> ExpandedFolders { get; init; } = [];
 public List<string> ExpandedCategories { get; init; } = [];
 public List<Guid> CollapsedSets { get; init; } = [];
 public double FolderHeight { get; init; } = 260;
 public double FolderHorizontalOffset { get; init; }
 public double FolderVerticalOffset { get; init; }
 public bool IncludeSubfolders { get; init; } = true;
 public bool TagsByName { get; init; }
 public bool ShowUnusedTags { get; init; }
}
