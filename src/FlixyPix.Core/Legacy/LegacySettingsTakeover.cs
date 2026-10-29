using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace FlixyPix.Core;

/// <summary>Groups in which the original's program settings are offered; the user takes over whole groups, not single values.</summary>
public enum LegacySettingGroup { Player, Previews, FileTypes, General, Slideshow, Nfo }

/// <summary>One original setting: its raw value, what FlixyPix makes of it and what FlixyPix uses today.
/// <see cref="Conflict"/> marks a value the user has already changed in FlixyPix and that the takeover would overwrite.</summary>
public sealed record LegacySettingChange(LegacySettingGroup Group, string Label, string Original, string Proposed, string Current, bool Differs, bool Conflict);

public sealed record LegacySettingsPlan(string Batch, string PreferencesSha256, IReadOnlyList<LegacySettingChange> Changes, IReadOnlyList<string> Warnings)
{
    public IReadOnlyList<LegacySettingGroup> Groups => Changes.Select(c => c.Group).Distinct().Order().ToList();
    public int Differences(LegacySettingGroup group) => Changes.Count(c => c.Group == group && c.Differs);
    public int Conflicts(LegacySettingGroup group) => Changes.Count(c => c.Group == group && c.Conflict);
    public static string Name(LegacySettingGroup group) => group switch
    {
        LegacySettingGroup.Player => T("Player and shortcuts"),
        LegacySettingGroup.Previews => T("Thumbnails and hover preview"),
        LegacySettingGroup.FileTypes => T("File types"),
        LegacySettingGroup.Slideshow => T("Slideshow"),
        LegacySettingGroup.Nfo => T("NFO files"),
        _ => T("General"),
    };
}

public sealed partial class CatalogRepository
{
    private sealed record SettingField(LegacySettingGroup Group, string Label, string Key, Func<CatalogPreferences, string> Show);

    private static string YesNo(bool value) => value ? T("yes") : T("no");
    private static string Seconds(double value) => value.ToString("0.##", CultureInfo.GetCultureInfo("de-DE")) + " s";
    private static string Key(string value) => value.Length == 0 ? T("not assigned") : value;
    public static string MouseLabel(PlayerMouseAction action) => action switch
    {
        PlayerMouseAction.PlayPause => T("Play / Pause"), PlayerMouseAction.PreviousVideo => T("Previous video"), PlayerMouseAction.NextVideo => T("Next video"),
        PlayerMouseAction.PlayRandom => T("Random video"), PlayerMouseAction.PreviousBookmark => T("Previous bookmark"), PlayerMouseAction.NextBookmark => T("Next bookmark"),
        PlayerMouseAction.SmallBackward => T("Small jump back"), PlayerMouseAction.SmallForward => T("Small jump forward"), PlayerMouseAction.FrameBackward => T("Previous frame"),
        PlayerMouseAction.FrameForward => T("Next frame"), PlayerMouseAction.Screenshot => T("Screenshot"), PlayerMouseAction.SwitchWheel => T("Toggle mouse wheel mode"), PlayerMouseAction.Mute => T("Sound off / on"), PlayerMouseAction.ContextMenu => T("Player menu"), _ => T("nothing"),
    };
    public static string WheelLabel(PlayerWheelAction action) => action switch
    {
        PlayerWheelAction.Seek => T("seek"), PlayerWheelAction.Volume => T("Volume"), PlayerWheelAction.SeekAndVolume => T("seek, volume with Ctrl"),
        PlayerWheelAction.FrameStep => T("Frame by frame"), _ => T("nothing"),
    };

    private static readonly SettingField[] SettingFields =
    [
        new(LegacySettingGroup.Player, T("Always play in full screen"), "fullscreen", p => YesNo(p.Playback.AlwaysFullscreen)),
        new(LegacySettingGroup.Player, T("Small jump forward"), "nhotkeysmallffwd", p => Seconds(p.Playback.SmallJumpForwardSeconds)),
        new(LegacySettingGroup.Player, T("Small jump back"), "nhotkeysmallrwnd", p => Seconds(p.Playback.SmallJumpBackwardSeconds)),
        new(LegacySettingGroup.Player, T("Large jump forward"), "nhotkeylargeffwd", p => Seconds(p.Playback.LargeJumpForwardSeconds)),
        new(LegacySettingGroup.Player, T("Large jump back"), "nhotkeylargerwnd", p => Seconds(p.Playback.LargeJumpBackwardSeconds)),
        new(LegacySettingGroup.Player, T("Hardware decoding"), "ffmpegcommandline", p => YesNo(p.Playback.HardwareDecoding)),
        new(LegacySettingGroup.Player, T("No screen saver during playback"), "disablescreensaver", p => YesNo(p.Playback.PreventSleep)),
        new(LegacySettingGroup.Player, T("Media keys"), "ezfpmediakeys", p => YesNo(p.Playback.MediaKeys)),
        new(LegacySettingGroup.Player, T("Left click in the video"), "ezfpleftclick", p => MouseLabel(p.Playback.LeftClick)),
        new(LegacySettingGroup.Player, T("Right click in the video"), "ezfprightclick", p => MouseLabel(p.Playback.RightClick)),
        new(LegacySettingGroup.Player, T("Middle mouse button"), "ezfpmiddleclick", p => MouseLabel(p.Playback.MiddleClick)),
        new(LegacySettingGroup.Player, T("Mouse button “Back”"), "ezfpx1click", p => MouseLabel(p.Playback.BackClick)),
        new(LegacySettingGroup.Player, T("Mouse button “Forward”"), "ezfpx2click", p => MouseLabel(p.Playback.ForwardClick)),
        new(LegacySettingGroup.Player, T("Mouse wheel in the video"), "ezfpwheelposition", p => WheelLabel(p.Playback.Wheel)),
        new(LegacySettingGroup.Player, T("Mouse wheel step"), "ezfpwheelincrement", p => Seconds(p.Playback.WheelStep)),
        new(LegacySettingGroup.Player, T("Resume at the remembered position"), "rememberlastposition", p => YesNo(p.Playback.ResumePlayback)),
        new(LegacySettingGroup.Player, T("Remember volume"), "remembervolume", p => YesNo(p.Playback.RememberVolume)),
        new(LegacySettingGroup.Player, T("Initial volume"), "defaultvolume", p => p.Playback.StartVolume.ToString(CultureInfo.InvariantCulture) + " %"),
        new(LegacySettingGroup.Player, T("Player always on top"), "ezfpplayertopmost", p => YesNo(p.Playback.AlwaysOnTop)),
        new(LegacySettingGroup.Player, T("Overlays in the player"), "ezfpplayermessages", p => YesNo(p.Playback.ShowMessages)),
        new(LegacySettingGroup.Player, T("Hide the control bar in full screen"), "ezfpplayerautohidecontrols", p => YesNo(p.Playback.AutoHideControls)),
        new(LegacySettingGroup.Player, T("Shortcuts in the player"), "enablekeyshortcuts", p => YesNo(p.Playback.KeyboardShortcuts)),
        new(LegacySettingGroup.Player, T("Show jump marks automatically"), "showchapters", p => YesNo(p.Playback.ShowMarks)),
        new(LegacySettingGroup.Player, T("Screenshot folder"), "videoscreenshots-directory", p => p.Playback.Screenshots.Folder ?? "Bilder\\FlixyPix"),
        new(LegacySettingGroup.Player, T("Screenshot name prefix"), "videoscreenshots-prefix", p => p.Playback.Screenshots.Prefix.Length > 0 ? p.Playback.Screenshots.Prefix : T("Title of the video")),
        new(LegacySettingGroup.Player, T("Screenshot format"), "videoscreenshots-format", p => p.Playback.Screenshots.Format == ScreenshotFormat.Jpg ? "JPG" : "PNG"),
        new(LegacySettingGroup.Player, T("Number screenshots"), "videoscreenshots-seqnumbering", p => YesNo(p.Playback.Screenshots.Numbering)),
        .. PlayerActions.All.Where(a => a.LegacyKey.Length > 0).Select(a => new SettingField(LegacySettingGroup.Player, T("Key: {0}", a.Label), a.LegacyKey, p => Key(p.Playback.Hotkeys.GetValueOrDefault(a.Action) ?? ""))),
        new(LegacySettingGroup.Previews, T("Columns in the thumbnail"), "vidthumbcolumns", p => p.Previews.SheetColumns.ToString(CultureInfo.InvariantCulture)),
        new(LegacySettingGroup.Previews, T("Rows in the thumbnail"), "vidthumbrows", p => p.Previews.SheetRows.ToString(CultureInfo.InvariantCulture)),
        new(LegacySettingGroup.Previews, T("Thumbnail width"), "vidthumbwidth", p => p.Previews.SheetWidth + " px"),
        new(LegacySettingGroup.Previews, T("Simultaneous preview processes"), "numvidthumbproc", p => p.Previews.GeneratorProcesses.ToString(CultureInfo.InvariantCulture)),
        new(LegacySettingGroup.Previews, T("Skip videos with custom thumbnails"), "bypassvideoswithcustomthumbnail", p => YesNo(p.Previews.SkipWhenCustomThumbnail)),
        new(LegacySettingGroup.Previews, T("Header with file details"), "vidthumbnailheader", p => YesNo(p.Previews.SheetHeader)),
        new(LegacySettingGroup.Previews, T("Border"), "vidthumbborder", p => p.Previews.SheetBorder + " px"),
        new(LegacySettingGroup.Previews, T("Time stamp"), "vidthumbtimestamp", p => p.Previews.SheetTimestamp switch
        {
            SheetTimestamp.TopLeft => "oben links", SheetTimestamp.TopRight => "oben rechts", SheetTimestamp.BottomLeft => "unten links",
            SheetTimestamp.BottomRight => "unten rechts", _ => "aus",
        }),
        new(LegacySettingGroup.Previews, T("Background color"), "vidthumbbackcolor", p => p.Previews.SheetBackground),
        new(LegacySettingGroup.Previews, T("Text color"), "vidthumbforecolor", p => p.Previews.SheetForeground),
        new(LegacySettingGroup.Previews, Tc("legacy setting", "Font"), "thumbnailfont", p => p.Previews.SheetFont),
        new(LegacySettingGroup.Previews, T("Hover preview"), "ezfphoverenabled", p => YesNo(p.Previews.HoverEnabled)),
        new(LegacySettingGroup.Previews, T("Images on hover"), "ezfphoverthumbnails", p => p.Previews.HoverFrames.ToString(CultureInfo.InvariantCulture)),
        new(LegacySettingGroup.Previews, T("Speed on hover"), "ezfphoverspeed", p => p.Previews.HoverSpeed switch { HoverSpeed.Slow => "langsam", HoverSpeed.Fast => "schnell", _ => "mittel" }),
        new(LegacySettingGroup.Previews, T("Wait briefly before starting"), "ezfphoverdelay", p => YesNo(p.Previews.HoverDelay)),
        new(LegacySettingGroup.Previews, T("Time stamp on hover"), "ezfphovertimestamp", p => YesNo(p.Previews.HoverTimestamp)),
        new(LegacySettingGroup.FileTypes, T("Videos"), "videofiletypes", p => string.Join(" ", p.Previews.VideoExtensions)),
        new(LegacySettingGroup.FileTypes, T("Images"), "imagefiletypes", p => string.Join(" ", p.Previews.ImageExtensions)),
        new(LegacySettingGroup.General, T("Read hidden folders"), "searchhiddenfolders", p => YesNo(p.General.IncludeHiddenFolders)),
        new(LegacySettingGroup.General, T("Ask before removing"), "confirmdelete", p => YesNo(p.General.ConfirmRemove)),
        new(LegacySettingGroup.General, T("Show file extension"), "showmediaextension", p => YesNo(p.General.ShowExtension)),
        new(LegacySettingGroup.General, T("Item counts by folder"), "showvideofoldercounts", p => YesNo(p.General.ShowFolderCounts)),
        new(LegacySettingGroup.General, T("Item counts by category"), "showvideocategorycounts", p => YesNo(p.General.ShowCategoryCounts)),
        new(LegacySettingGroup.General, T("Item counts by tag category"), "showvideotagcategorycounts", p => YesNo(p.General.ShowTagCategoryCounts)),
        new(LegacySettingGroup.General, T("Mark missing files"), "highlightvideonotfound", p => YesNo(p.General.MarkMissingFiles)),
        new(LegacySettingGroup.General, T("Always show the import report"), "alwaysshowimporterrors", p => YesNo(p.General.AlwaysShowImportReport)),
        new(LegacySettingGroup.General, T("Restore last view"), "loadlastvideoview", p => YesNo(p.General.RestoreLastView)),
        new(LegacySettingGroup.General, T("Title instead of file name on tiles"), "displayvideotitle", p => YesNo(p.General.ShowTitles)),
        new(LegacySettingGroup.General, T("Wrap long titles"), "displaymoviename", p => YesNo(p.General.FullTitles)),
        new(LegacySettingGroup.General, T("Progress bar"), "showvideoprogress", p => YesNo(p.General.ShowProgress)),
        new(LegacySettingGroup.General, T("Click the image to play"), "clickvidthumb", p => YesNo(p.General.ClickPlays)),
        new(LegacySettingGroup.General, T("Lift tiles on hover"), "enablemousehoverzoomthumbnail", p => YesNo(p.General.HoverLift)),
        new(LegacySettingGroup.General, T("Page bar below the grid"), "pageselectioncontrols", p => YesNo(p.General.ShowPager)),
        new(LegacySettingGroup.General, T("Steps for back/forward"), "navigationhistorystore", p => p.General.HistoryLength.ToString(CultureInfo.InvariantCulture)),
        new(LegacySettingGroup.General, T("Remember window position"), "rememberform", p => YesNo(p.General.RememberWindow)),
        new(LegacySettingGroup.General, T("Several tags: at least one"), "videotagmatchtype", p => YesNo(p.General.TagsMatchAny)),
        new(LegacySettingGroup.General, T("Remove characters from titles"), "fileexclusionchars", p => p.General.TitleRemoveCharacters.Length > 0 ? p.General.TitleRemoveCharacters : T("none")),
        new(LegacySettingGroup.General, T("Replace characters in titles with spaces"), "filereplacechars", p => p.General.TitleSpaceCharacters.Length > 0 ? p.General.TitleSpaceCharacters : T("none")),
        new(LegacySettingGroup.General, T("Skip images with these name parts"), "filteroutpic", p => p.General.SkipImageNames.Count > 0 ? string.Join("; ", p.General.SkipImageNames) : T("none")),
        new(LegacySettingGroup.General, T("Cross-fade between images"), "image_fade", p => YesNo(p.General.ImageFade)),
        new(LegacySettingGroup.General, T("Move deleted files to the Recycle Bin"), "deletetorecyclebin", p => YesNo(p.General.DeleteToRecycleBin)),
        new(LegacySettingGroup.General, T("“Recently watched” in days"), "recentlywatcheddays", p => p.Home.RecentDays.ToString(CultureInfo.InvariantCulture)),
        new(LegacySettingGroup.General, T("Mouse wheel in the image viewer"), "maximagethumbmousewheelcmd",p => p.General.ImageWheel switch { ImageWheelAction.Zoom => T("zoom"), ImageWheelAction.Scroll => T("pan"), _ => T("browse") }),
        new(LegacySettingGroup.Slideshow, T("Display time per image"), "slideshow_interval", p => Seconds(p.Slideshow.IntervalSeconds)),
        new(LegacySettingGroup.Slideshow, T("Order"), "slideshow_display", p => p.Slideshow.Order switch { SlideshowOrder.InOrder => T("in order"), SlideshowOrder.Random => T("random"), _ => T("less random") }),
        new(LegacySettingGroup.Slideshow, T("Cross-fade"), "slideshow_fade", p => YesNo(p.Slideshow.Fade)),
        new(LegacySettingGroup.Slideshow, T("Pan and zoom"), "slideshow_pan", p => YesNo(p.Slideshow.PanAndZoom)),
        new(LegacySettingGroup.Slideshow, T("Speed of the transitions"), "slideshow_speed", p => p.Slideshow.Speed switch { HoverSpeed.Slow => "langsam", HoverSpeed.Fast => "schnell", _ => "mittel" }),
        new(LegacySettingGroup.Slideshow, T("Show image details"), "image_details", p => YesNo(p.Slideshow.ShowInfo)),
        new(LegacySettingGroup.Nfo, T("Import title"), "chkimportnfo_title", p => YesNo(p.Nfo.ImportTitle)),
        new(LegacySettingGroup.Nfo, T("Import tags"), "chkimportnfo_tags", p => YesNo(p.Nfo.ImportTags)),
        new(LegacySettingGroup.Nfo, T("Elements as tags"), "importnfodata_tags", p => p.Nfo.TagElements),
        new(LegacySettingGroup.Nfo, T("Import description"), "chkimportnfo_description", p => YesNo(p.Nfo.ImportDescription)),
        new(LegacySettingGroup.Nfo, T("Elements of the description"), "importnfodata_description", p => p.Nfo.DescriptionElements),
        new(LegacySettingGroup.Nfo, T("Search subfolders"), "chkimportnfo_searchsubfolders", p => YesNo(p.Nfo.SearchSubfolders)),
        new(LegacySettingGroup.Nfo, T("Import thumbnail"), "chkimportnfo_thumbnail", p => YesNo(p.Nfo.ImportThumbnail)),
        new(LegacySettingGroup.Nfo, T("Import fanart"), "chkimportnfo_fanart", p => YesNo(p.Nfo.ImportFanart)),
    ];

    /// <summary>Compares the original settings kept raw in a migration batch with today's FlixyPix values. Works on any
    /// migrated library, also long after the migration, because the configsettings rows are retained verbatim.</summary>
    public LegacySettingsPlan PlanLegacySettings(string batch)
    {
        lock (gate) { ThrowIfDisposed(); using var transaction = connection.BeginTransaction(); return PlanLegacySettingsCore(batch, transaction); }
    }

    /// <summary>Takes over the chosen groups in one step; returns how many values changed.</summary>
    public int ApplyLegacySettings(LegacySettingsPlan plan, IReadOnlyCollection<LegacySettingGroup> groups)
    {
        ArgumentNullException.ThrowIfNull(plan); ArgumentNullException.ThrowIfNull(groups);
        lock (gate)
        {
            ThrowIfDisposed(); using var transaction = connection.BeginTransaction();
            var changed = ApplyLegacySettingsCore(plan, groups, transaction); transaction.Commit(); return changed;
        }
    }

    private LegacySettingsPlan PlanLegacySettingsCore(string batch, SqliteTransaction transaction)
    {
        var values = LegacySettingValues(ReadRetainedRows(batch, "configsettings", transaction));
        var current = ReadPreferences(transaction); var warnings = new List<string>();
        var projected = ProjectLegacyAppSettings(values, current, [], warnings);
        var defaults = new CatalogPreferences();
        var changes = SettingFields.Where(f => values.ContainsKey(f.Key)).Select(f =>
        {
            var (proposed, now) = (f.Show(projected), f.Show(current));
            return new LegacySettingChange(f.Group, f.Label, values[f.Key].Value ?? "", proposed, now, proposed != now, proposed != now && now != f.Show(defaults));
        }).ToList();
        return new(batch, PreferencesHash(current), changes, warnings);
    }

    private int ApplyLegacySettingsCore(LegacySettingsPlan plan, IReadOnlyCollection<LegacySettingGroup> groups, SqliteTransaction transaction)
    {
        if (groups.Count == 0) return 0;
        var current = ReadPreferences(transaction);
        if (PreferencesHash(current) != plan.PreferencesSha256) throw new InvalidOperationException(T("The settings have changed in the meantime. Please check the import again."));
        var values = LegacySettingValues(ReadRetainedRows(plan.Batch, "configsettings", transaction));
        var adopted = new List<string>();
        var projected = ProjectLegacyAppSettings(values, current, adopted, []);
        var previews = current.Previews;
        if (groups.Contains(LegacySettingGroup.Previews)) previews = projected.Previews with { VideoExtensions = previews.VideoExtensions, ImageExtensions = previews.ImageExtensions };
        if (groups.Contains(LegacySettingGroup.FileTypes)) previews = previews with { VideoExtensions = projected.Previews.VideoExtensions, ImageExtensions = projected.Previews.ImageExtensions };
        var merged = current with
        {
            Playback = groups.Contains(LegacySettingGroup.Player) ? projected.Playback : current.Playback, Previews = previews,
            General = groups.Contains(LegacySettingGroup.General) ? projected.General : current.General,
            Slideshow = groups.Contains(LegacySettingGroup.Slideshow) ? projected.Slideshow : current.Slideshow,
            Nfo = groups.Contains(LegacySettingGroup.Nfo) ? projected.Nfo : current.Nfo,
            Home = groups.Contains(LegacySettingGroup.General) ? current.Home with { RecentDays = projected.Home.RecentDays } : current.Home,
        };
        ValidatePlayback(merged.Playback); ValidatePreviews(merged.Previews); ValidateSlideshow(merged.Slideshow);
        SaveDocument("preferences", Guid.Empty, merged, transaction);
        // The rows of the chosen groups now have a working counterpart, as after an automatic migration.
        var keys = SettingFields.Where(f => groups.Contains(f.Group)).Select(f => f.Key).Intersect(adopted, StringComparer.OrdinalIgnoreCase);
        foreach (var key in keys)
        {
            using var audit = Command("UPDATE migration_rows SET status='partially-projected',code='app-setting' WHERE batch=$batch AND table_name='configsettings' COLLATE NOCASE AND legacy_key=$key AND status='raw-retained'",
                transaction, ("$batch", plan.Batch), ("$key", (object?)values[key].Id ?? DBNull.Value));
            audit.ExecuteNonQuery();
        }
        return SettingFields.Count(f => groups.Contains(f.Group) && f.Show(merged) != f.Show(current));
    }

    /// <summary>Rows of one original table as kept verbatim by a migration batch.</summary>
    private IEnumerable<JsonElement> ReadRetainedRows(string batch, string table, SqliteTransaction transaction)
    {
        using var command = Command("SELECT record FROM legacy_records WHERE batch=$batch AND json_extract(record,'$.type')='row' AND json_extract(record,'$.table')=$table COLLATE NOCASE ORDER BY ordinal",
            transaction, ("$batch", batch), ("$table", table));
        using var reader = command.ExecuteReader(); var rows = new List<JsonElement>();
        while (reader.Read()) { using var parsed = JsonDocument.Parse(reader.GetString(0)); rows.Add(Property(parsed.RootElement, "values").Clone()); }
        return rows;
    }

    private static string PreferencesHash(CatalogPreferences preferences) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(preferences, Json))));
}
