using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace FlixyPix.Core;

/// <summary>Player actions that can carry a keyboard shortcut, with the original configsettings key.</summary>
public static class PlayerActions
{
    public const string PlayPause = "PlayPause", Fullscreen = "Fullscreen", SmallForward = "SmallForward", SmallBackward = "SmallBackward",
        LargeForward = "LargeForward", LargeBackward = "LargeBackward", SpeedUp = "SpeedUp", SpeedDown = "SpeedDown",
        FrameForward = "FrameForward", FrameBackward = "FrameBackward", CreateBookmark = "CreateBookmark",
        NextBookmark = "NextBookmark", PreviousBookmark = "PreviousBookmark",
        NextVideo = "NextVideo", PreviousVideo = "PreviousVideo", ClipStart = "ClipStart", ClipEnd = "ClipEnd", ClipCreate = "ClipCreate",
        ToggleBookmark = "ToggleBookmark", Screenshot = "Screenshot", SlowMotion = "SlowMotion", SwitchWheel = "SwitchWheel",
        AddO = "AddO", UndoO = "UndoO", Mute = "Mute", ToggleMarks = "ToggleMarks";

    public static IReadOnlyList<(string Action, string Label, string LegacyKey)> All { get; } =
    [
        (PlayPause, T("Play / Pause"), "hotkeyplaypause"), (Fullscreen, T("Full screen"), "hotkeyfullscreen"),
        (SmallForward, T("Small jump forward"), "hotkeysmallffwd"), (SmallBackward, T("Small jump back"), "hotkeysmallrwnd"),
        (LargeForward, T("Large jump forward"), "hotkeylargeffwd"), (LargeBackward, T("Large jump back"), "hotkeylargerwnd"),
        (SpeedUp, T("Faster"), "hotkeyincreasespeed"), (SpeedDown, T("Slower"), "hotkeydecreasespeed"),
        (FrameForward, T("Next frame"), "hotkeyframeforward"), (FrameBackward, T("Previous frame"), "hotkeyframebackward"),
        (CreateBookmark, T("Set bookmark"), "hotkeybookmarkcreate"), (NextBookmark, T("Next bookmark"), "hotkeybookmarknext"),
        (PreviousBookmark, T("Previous bookmark"), "hotkeybookmarkprevious"),
        (ToggleBookmark, T("Set or remove bookmark"), "hotkeybookmarktoggle"),
        (NextVideo, T("Next video"), "hotkeyplaynext"), (PreviousVideo, T("Previous video"), "hotkeyplayprevious"),
        (ClipStart, T("Mark scene start"), "hotkeyclipstart"), (ClipEnd, T("Mark scene end"), "hotkeyclipend"),
        (ClipCreate, T("Create scene from marks"), "hotkeyclipcreate"),
        (Screenshot, T("Save screenshot"), "hotkeyscreenshot"), (SlowMotion, T("Slow motion on/off"), "hotkeyslowmo"),
        (SwitchWheel, T("Mouse wheel: seek or volume"), "hotkeyswitchmousewheel"),
        (AddO, T("O counter +1"), ""), (UndoO, T("Take back the last O"), ""),
        (Mute, T("Sound off / on"), ""), (ToggleMarks, T("Show or hide the jump mark list"), ""),
    ];

    public static Dictionary<string, string> Defaults() => new(StringComparer.Ordinal)
    {
        [PlayPause] = "Space", [Fullscreen] = "F", [SmallForward] = "Right", [SmallBackward] = "Left",
        [LargeForward] = "Shift+Right", [LargeBackward] = "Shift+Left", [SpeedUp] = "OemPeriod", [SpeedDown] = "OemComma",
        [FrameForward] = "", [FrameBackward] = "", [CreateBookmark] = "B", [NextBookmark] = "PageDown", [PreviousBookmark] = "PageUp",
        [ToggleBookmark] = "", [NextVideo] = "N", [PreviousVideo] = "P", [ClipStart] = "I", [ClipEnd] = "O", [ClipCreate] = "Ctrl+I",
        [Screenshot] = "S", [SlowMotion] = "Z", [SwitchWheel] = "", [AddO] = "Ctrl+O", [UndoO] = "Ctrl+Shift+O",
        [Mute] = "M", [ToggleMarks] = "L",
    };

    /// <summary>Saved shortcuts plus the defaults of actions added later; a default whose key the user already gave to
    /// another action stays unassigned.</summary>
    public static Dictionary<string, string> Complete(IReadOnlyDictionary<string, string> saved)
    {
        var result = new Dictionary<string, string>(saved, StringComparer.Ordinal);
        foreach (var (action, key) in Defaults())
            if (!result.ContainsKey(action))
                result[action] = key.Length > 0 && result.Values.Any(v => string.Equals(v, key, StringComparison.OrdinalIgnoreCase)) ? "" : key;
        return result;
    }
}

/// <summary>Player behaviour, following the original "Video Player" settings.</summary>
public sealed record PlaybackPreferences
{
    public bool AlwaysFullscreen { get; init; }
    public double SmallJumpForwardSeconds { get; init; } = 10;
    public double SmallJumpBackwardSeconds { get; init; } = 10;
    public double LargeJumpForwardSeconds { get; init; } = 30;
    public double LargeJumpBackwardSeconds { get; init; } = 30;
    public bool HardwareDecoding { get; init; } = true;
    /// <summary>Original "Disable screensaver when playing videos and slideshows".</summary>
    public bool PreventSleep { get; init; } = true;
    /// <summary>Original "Enable use of media keys (Play, Pause, Next, Previous, etc) via keyboard and headphones".</summary>
    public bool MediaKeys { get; init; } = true;
    /// <summary>Action → shortcut such as "Ctrl+Shift+Right"; empty means unassigned.</summary>
    public Dictionary<string, string> Hotkeys { get; init; } = PlayerActions.Defaults();
    /// <summary>Mouse buttons over the video (original "ezfpleftclick" … "ezfpx2click"); a double click always switches full screen.</summary>
    public PlayerMouseAction LeftClick { get; init; } = PlayerMouseAction.PlayPause;
    public PlayerMouseAction RightClick { get; init; } = PlayerMouseAction.ContextMenu;
    public PlayerMouseAction MiddleClick { get; init; } = PlayerMouseAction.Mute;
    public PlayerMouseAction BackClick { get; init; } = PlayerMouseAction.PreviousVideo;
    public PlayerMouseAction ForwardClick { get; init; } = PlayerMouseAction.NextVideo;
    /// <summary>Mouse wheel over the video (original "ezfpwheelposition", which defaults to volume); "seek and volume" seeks and changes the volume with Ctrl.</summary>
    public PlayerWheelAction Wheel { get; init; } = PlayerWheelAction.Seek;
    /// <summary>0 for libraries saved while the wheel changed the volume by default; reading them switches such a wheel to seeking once.</summary>
    public int WheelRevision { get; init; }
    public const int CurrentWheelRevision = 1;
    /// <summary>0 for libraries saved while the right click did nothing by default; reading them gives it the player menu once.</summary>
    public int MouseRevision { get; init; }
    public const int CurrentMouseRevision = 1;
    /// <summary>Step of the plain wheel when it seeks (original "ezfpwheelincrement").</summary>
    public double WheelStep { get; init; } = 5;
    public JumpUnit WheelUnit { get; init; } = JumpUnit.Seconds;
    /// <summary>Wheel up seeks back instead of forward.</summary>
    public bool InvertWheel { get; init; }
    /// <summary>The wheel with Shift, Ctrl or Alt, each with its own action and step.</summary>
    public List<WheelBinding> WheelModifiers { get; init; } = WheelBinding.Defaults();
    /// <summary>Further jumps on keys of one's choice, e.g. Ctrl+Right = +60 s or 9 = +10 %.</summary>
    public List<CustomJump> CustomJumps { get; init; } = [];
    /// <summary>Start where the video was left (original "rememberlastposition").</summary>
    public bool ResumePlayback { get; init; } = true;
    /// <summary>Keep the volume of the last video (original "remembervolume"); otherwise start with <see cref="StartVolume"/> (original "defaultvolume").</summary>
    public bool RememberVolume { get; init; } = true;
    public int StartVolume { get; init; } = 100;
    /// <summary>The player window stays above other windows (original "ezfpplayertopmost").</summary>
    public bool AlwaysOnTop { get; init; }
    /// <summary>A medium with several files plays its best version (most lines, then bit rate); off, it plays the main file.</summary>
    public bool PlayBestVersion { get; init; } = true;
    /// <summary>Notices in the player (original "ezfpplayermessages"): the play/pause symbol over the video and short notes
    /// such as "Lesezeichen gesetzt" in the status line.</summary>
    public bool ShowMessages { get; init; } = true;
    /// <summary>In full screen the control bar hides after a moment without mouse movement (original "ezfpplayerautohidecontrols").</summary>
    public bool AutoHideControls { get; init; } = true;
    /// <summary>The shortcuts of the player (original "enablekeyshortcuts"); Esc and F11 always work.</summary>
    public bool KeyboardShortcuts { get; init; } = true;
    /// <summary>The list of jump marks opens by itself when a video has bookmarks or scenes (original "showchapters").</summary>
    public bool ShowMarks { get; init; } = true;
    /// <summary>A bookmark set in the player gets a name field at once (UX plan U6); otherwise it is named after its time.</summary>
    public bool NameNewBookmarks { get; init; }
    public ScreenshotPreferences Screenshots { get; init; } = new();

    /// <summary>The shortcuts of all actions and custom jumps, for conflict checks.</summary>
    public IEnumerable<(string Owner, string Key)> AllShortcuts() =>
        Hotkeys.Where(p => !string.IsNullOrWhiteSpace(p.Value)).Select(p => (p.Key, p.Value.Trim()))
            .Concat(CustomJumps.Where(j => !string.IsNullOrWhiteSpace(j.Key)).Select(j => (T("Jump {0}", j.Describe()), j.Key.Trim())));
}

public enum JumpUnit { Seconds, Percent }

public enum ScreenshotFormat { Png, Jpg }

/// <summary>Where and how the player saves screenshots (original "videoscreenshots-*").</summary>
public sealed record ScreenshotPreferences
{
    /// <summary>Target folder; null means Pictures\FlixyPix.</summary>
    public string? Folder { get; init; }
    /// <summary>Start of the file name; empty means the title of the video.</summary>
    public string Prefix { get; init; } = "";
    public ScreenshotFormat Format { get; init; } = ScreenshotFormat.Png;
    /// <summary>Numbered names ("Prefix 0001.png") instead of the time in the video.</summary>
    public bool Numbering { get; init; }

    public string Extension => Format == ScreenshotFormat.Jpg ? ".jpg" : ".png";

    /// <summary>A free file name in <paramref name="folder"/> for a picture of <paramref name="title"/> at <paramref name="seconds"/>.</summary>
    public string NextPath(string folder, string title, double seconds)
    {
        var start = Clean(Prefix.Length > 0 ? Prefix : title);
        if (start.Length == 0) start = T("Image");
        if (Numbering)
        {
            // Continues after the highest number already there, so deleted pictures leave no gaps to fill.
            var pattern = new System.Text.RegularExpressions.Regex("^" + System.Text.RegularExpressions.Regex.Escape(start) + @" (\d{4,})\.(png|jpg)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var highest = Directory.Exists(folder) ? Directory.EnumerateFiles(folder).Select(f => pattern.Match(Path.GetFileName(f))).Where(m => m.Success)
                .Select(m => int.TryParse(m.Groups[1].Value, out var n) ? n : 0).DefaultIfEmpty(0).Max() : 0;
            return Path.Combine(folder, $"{start} {highest + 1:0000}{Extension}");
        }
        var stamp = TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(@"hh\-mm\-ss", CultureInfo.InvariantCulture);
        var path = Path.Combine(folder, $"{start} {stamp}{Extension}");
        for (var n = 2; File.Exists(path); n++) path = Path.Combine(folder, $"{start} {stamp} ({n}){Extension}");
        return path;
    }

    private static string Clean(string name) => string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
}

/// <summary>Seconds of a jump; percent counts from the video's length (unknown length: nothing).</summary>
public static class Jumps
{
    public static double Seconds(double amount, JumpUnit unit, double? duration) =>
        unit == JumpUnit.Percent ? duration is > 0 and var length ? amount / 100 * length : 0 : amount;

    public static string Describe(double amount, JumpUnit unit) =>
        (amount >= 0 ? "+" : "−") + Math.Abs(amount).ToString("0.##", CultureInfo.CurrentCulture) + (unit == JumpUnit.Percent ? " %" : " s");
}

public enum WheelModifier { Shift, Ctrl, Alt }

public sealed record WheelBinding(WheelModifier Modifier, PlayerWheelAction Action, double Step = 30, JumpUnit Unit = JumpUnit.Seconds)
{
    public static List<WheelBinding> Defaults() =>
        [new(WheelModifier.Shift, PlayerWheelAction.Seek, 30), new(WheelModifier.Ctrl, PlayerWheelAction.Volume, 5), new(WheelModifier.Alt, PlayerWheelAction.FrameStep, 1)];
}

/// <summary>A key that jumps by a signed amount.</summary>
public sealed record CustomJump(string Key, double Amount, JumpUnit Unit = JumpUnit.Seconds)
{
    public string Describe() => Jumps.Describe(Amount, Unit);
}

/// <summary>Actions a mouse button can trigger in the player, in the order of the original's lists.</summary>
/// <summary>Mouse actions in the player; the order up to <see cref="SwitchWheel"/> is the original's list, later ones are FlixyPix's own.</summary>
public enum PlayerMouseAction { Nothing, PlayPause, PreviousVideo, NextVideo, PlayRandom, PreviousBookmark, NextBookmark, SmallBackward, SmallForward, FrameBackward, FrameForward, Screenshot, SwitchWheel, Mute, ContextMenu }
/// <summary>What the mouse wheel does over the video, in the order of the original's list.</summary>
public enum PlayerWheelAction { Nothing, Seek, Volume, SeekAndVolume, FrameStep }

/// <summary>Video thumbnails and preview generation, following the original "Thumbnail Settings".</summary>
public sealed record PreviewPreferences
{
    public int SheetColumns { get; init; } = 1;
    public int SheetRows { get; init; } = 1;
    public int SheetWidth { get; init; } = 480;
    public int GeneratorProcesses { get; init; } = 2;
    public bool SkipWhenCustomThumbnail { get; init; } = true;
    /// <summary>Compute the similarity fingerprints (P2) right after an import instead of at the next search.</summary>
    public bool FingerprintsOnImport { get; init; }
    public List<string> VideoExtensions { get; init; } = MediaFormats.VideoExtensions.Order(StringComparer.OrdinalIgnoreCase).ToList();
    public List<string> ImageExtensions { get; init; } = MediaFormats.ImageExtensions.Order(StringComparer.OrdinalIgnoreCase).ToList();
    /// <summary>Hover preview on video tiles, following the original "Hover preview" options (frame mode).</summary>
    public bool HoverEnabled { get; init; } = true;
    public int HoverFrames { get; init; } = 5;
    public HoverSpeed HoverSpeed { get; init; } = HoverSpeed.Medium;
    public bool HoverDelay { get; init; } = true;
    public bool HoverTimestamp { get; init; }
    /// <summary>Take the keyframe nearest each scene instead of the exact second: several times faster; the timestamp shows the real time.</summary>
    public bool SheetFastSeek { get; init; } = true;
    /// <summary>Gap around and between the scenes in pixels (original "vidthumbborder").</summary>
    public int SheetBorder { get; init; } = 2;
    /// <summary>File name, size, resolution and duration above the scenes (original "vidthumbnailheader").</summary>
    public bool SheetHeader { get; init; }
    /// <summary>Time of each scene in one corner (original "vidthumbtimestamp").</summary>
    public SheetTimestamp SheetTimestamp { get; init; } = SheetTimestamp.None;
    public string SheetBackground { get; init; } = "#14171C";
    public string SheetForeground { get; init; } = "#FFFFFF";
    /// <summary>Font family of header and timestamps (original "thumbnailfont").</summary>
    public string SheetFont { get; init; } = "Segoe UI";
    /// <summary>Percent of the duration left out at start and end, so intros and credits do not fill the sheet.</summary>
    public int SheetSkipPercent { get; init; }
    /// <summary>Preview clips (feature plan P3): parts, length of each part, width; made right after an import when asked.</summary>
    public int ClipSegments { get; init; } = 12;
    public int ClipSegmentMilliseconds { get; init; } = 750;
    public int ClipWidth { get; init; } = 320;
    public bool ClipsOnImport { get; init; }
    /// <summary>How many preview clips the video wall plays at once.</summary>
    public int WallClips { get; init; } = 6;
    /// <summary>The scene wall plays the clips of the tiles in view without hovering, within <see cref="WallClips"/>.</summary>
    public bool SceneWallAutoplay { get; init; }
    /// <summary>What the hover preview shows: changing frames (as in the original) or the preview clip.</summary>
    public HoverMode HoverMode { get; init; } = HoverMode.Frames;
}

public enum HoverSpeed { Slow, Medium, Fast }

public enum HoverMode { Frames, Clip }

/// <summary>Order of an image slideshow; "less random" picks a random place and shows a few images in order from there (original tooltip).</summary>
public enum SlideshowOrder { InOrder, Random, LessRandom }

/// <summary>Image slideshow in the viewer, following the original slideshow settings (<c>slideshow_*</c>, <c>image_details</c>).</summary>
public sealed record SlideshowPreferences
{
    /// <summary>Seconds per image (original "slideshow_interval", 0–30; 0 there means as fast as possible, here 0.25 s).</summary>
    public double IntervalSeconds { get; init; } = 5;
    public SlideshowOrder Order { get; init; } = SlideshowOrder.LessRandom;
    /// <summary>Cross-fade between images (original "Fade transition").</summary>
    public bool Fade { get; init; } = true;
    /// <summary>Slow pan and zoom over each image (original "Pan and zoom").</summary>
    public bool PanAndZoom { get; init; }
    /// <summary>Pace of fade and pan (original "slideshow_speed").</summary>
    public HoverSpeed Speed { get; init; } = HoverSpeed.Medium;
    /// <summary>File name and details over the image (original "Show file details").</summary>
    public bool ShowInfo { get; init; }
    public const double MinimumInterval = .25, MaximumInterval = 30;
}

public enum SheetTimestamp { None, TopLeft, TopRight, BottomLeft, BottomRight }

public sealed partial class CatalogRepository
{
    public void SavePlaybackPreferences(PlaybackPreferences playback) { ValidatePlayback(playback); SavePreferences(GetPreferences() with { Playback = playback }); }
    public void SavePreviewPreferences(PreviewPreferences previews) { ValidatePreviews(previews); SavePreferences(GetPreferences() with { Previews = previews }); }
    public void SaveSlideshowPreferences(SlideshowPreferences slideshow) { ValidateSlideshow(slideshow); SavePreferences(GetPreferences() with { Slideshow = slideshow }); }

    public static void ValidateSlideshow(SlideshowPreferences value)
    {
        if (!double.IsFinite(value.IntervalSeconds) || value.IntervalSeconds is < SlideshowPreferences.MinimumInterval or > SlideshowPreferences.MaximumInterval)
            throw new ArgumentException(T("The slideshow shows each image for 0.25 to 30 seconds."));
        if (!Enum.IsDefined(value.Order) || !Enum.IsDefined(value.Speed)) throw new ArgumentException(T("Unknown order or speed of the slideshow."));
    }

    public void SaveGeneralPreferences(GeneralPreferences general)
    {
        if (!Enum.IsDefined(general.ImageWheel)) throw new ArgumentException(T("Unknown mouse wheel action for the image viewer."));
        SavePreferences(GetPreferences() with { General = general });
    }

    public static void ValidatePlayback(PlaybackPreferences value)
    {
        if (new[] { value.SmallJumpForwardSeconds, value.SmallJumpBackwardSeconds, value.LargeJumpForwardSeconds, value.LargeJumpBackwardSeconds }.Any(s => !double.IsFinite(s) || s is < 1 or > 3600))
            throw new ArgumentException(T("Jump distances must be between 1 and 3600 seconds."));
        if (!new[] { value.LeftClick, value.RightClick, value.MiddleClick, value.BackClick, value.ForwardClick }.All(Enum.IsDefined) || !Enum.IsDefined(value.Wheel))
            throw new ArgumentException(T("Unknown mouse action in the player."));
        if (value.WheelModifiers is null || value.CustomJumps is null || !double.IsFinite(value.WheelStep) || value.WheelStep <= 0 || value.WheelStep > (value.WheelUnit == JumpUnit.Percent ? 50 : 3600) ||
            !Enum.IsDefined(value.WheelUnit) || value.StartVolume is < 0 or > 100)
            throw new ArgumentException(T("The mouse wheel step must be greater than 0 and at most 3600 seconds or 50%; the initial volume must be between 0 and 100%."));
        if (value.WheelModifiers.Any(b => b is null || !Enum.IsDefined(b.Modifier) || !Enum.IsDefined(b.Action) || !Enum.IsDefined(b.Unit) || !double.IsFinite(b.Step) || b.Step <= 0 ||
            b.Step > (b.Unit == JumpUnit.Percent ? 50 : 3600)) || value.WheelModifiers.GroupBy(b => b.Modifier).Any(g => g.Count() > 1))
            throw new ArgumentException(T("Each mouse wheel modifier key has exactly one action with a step greater than 0."));
        if (value.Screenshots is null || !Enum.IsDefined(value.Screenshots.Format) || value.Screenshots.Prefix is null || value.Screenshots.Prefix.Length > 80 ||
            value.Screenshots.Folder is { } folder && (folder.Trim().Length == 0 || !Path.IsPathFullyQualified(folder)))
            throw new ArgumentException(T("Screenshots need a complete folder path (or the default folder) and a name prefix of at most 80 characters."));
        if (value.CustomJumps.Count > 32 || value.CustomJumps.Any(j => j is null || string.IsNullOrWhiteSpace(j.Key) || !Enum.IsDefined(j.Unit) || !double.IsFinite(j.Amount) || j.Amount == 0 ||
            Math.Abs(j.Amount) > (j.Unit == JumpUnit.Percent ? 100 : 36000)))
            throw new ArgumentException(T("A custom jump needs a key and a distance other than 0 (at most 10 hours or 100 %)."));
        var used = value.AllShortcuts().GroupBy(p => p.Key, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (used != null) throw new ArgumentException(T("The shortcut “{0}” is assigned more than once.", used.Key));
    }

    public static void ValidatePreviews(PreviewPreferences value)
    {
        if (value.SheetColumns is < 1 or > 6 || value.SheetRows is < 1 or > 6) throw new ArgumentException(T("The thumbnail grid allows 1 to 6 columns and rows."));
        if (value.SheetWidth is < 160 or > 3840) throw new ArgumentException(T("The thumbnail width must be between 160 and 3840 pixels."));
        if (value.GeneratorProcesses is < 1 or > 8) throw new ArgumentException(T("1 to 8 preview processes are possible."));
        if (value.HoverFrames is < 1 or > HoverPreview.MaximumFrames) throw new ArgumentException(T("The hover preview shows 1 to 20 images."));
        if (!Enum.IsDefined(value.HoverSpeed)) throw new ArgumentException(T("Unknown speed for the hover preview."));
        if (!Enum.IsDefined(value.HoverMode)) throw new ArgumentException(T("Unknown kind of hover preview."));
        if (value.ClipSegments is < 1 or > 32 || value.ClipSegmentMilliseconds is < 250 or > 5000 || value.ClipWidth is < 120 or > 1280 || value.ClipWidth % 2 != 0)
            throw new ArgumentException(T("Preview clips have 1 to 32 parts of 0.25 to 5 seconds and an even width of 120 to 1280 pixels."));
        if (value.WallClips is < 1 or > 24) throw new ArgumentException(T("The video wall plays 1 to 24 clips at once."));
        if (value.SheetBorder is < 0 or > 20) throw new ArgumentException(T("The thumbnail border can be 0 to 20 pixels wide."));
        if (value.SheetSkipPercent is < 0 or > 20) throw new ArgumentException(T("Skip 0 to 20 percent at the start and end."));
        if (!Enum.IsDefined(value.SheetTimestamp)) throw new ArgumentException(T("Unknown position of the time stamp."));
        if (!IsColor(value.SheetBackground) || !IsColor(value.SheetForeground)) throw new ArgumentException(T("Please enter colors as #RRGGBB."));
        if (string.IsNullOrWhiteSpace(value.SheetFont)) throw new ArgumentException(T("Please choose a font."));
        if (value.VideoExtensions.Concat(value.ImageExtensions).Any(e => !e.StartsWith('.') || e.Length < 2 || e.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new ArgumentException(T("Please enter file types as extensions like .mp4."));
    }

    public static bool IsColor(string? value) => value is { Length: 7 } && value[0] == '#' && value.Skip(1).All(char.IsAsciiHexDigit);

    /// <summary>Converts an original shortcut ("ALT+SHIFT+CTRL+Right", WinForms key names) into "Ctrl+Shift+Alt+Right".</summary>
    public static string ConvertLegacyHotkey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Equals("set hotkey", StringComparison.OrdinalIgnoreCase)) return "";
        var parts = value.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "";
        var key = parts[^1] switch
        {
            var k when k.Equals("Return", StringComparison.OrdinalIgnoreCase) => "Enter",
            var k when k.Equals("Next", StringComparison.OrdinalIgnoreCase) => "PageDown",
            var k when k.Equals("Prior", StringComparison.OrdinalIgnoreCase) => "PageUp",
            var k => k,
        };
        bool Has(string modifier) => parts[..^1].Any(p => p.Equals(modifier, StringComparison.OrdinalIgnoreCase));
        return (Has("CTRL") ? "Ctrl+" : "") + (Has("SHIFT") ? "Shift+" : "") + (Has("ALT") ? "Alt+" : "") + key;
    }

    // Original configsettings rows that have a working counterpart; other rows stay raw-retained.
    // The takeover assistant migrates without them and lets the user choose groups afterwards (LegacySettingsTakeover.cs).
    private void ImportLegacyAppSettings(Func<string, IEnumerable<JsonElement>> rows, SqliteTransaction transaction, List<string> warnings)
    {
        if (legacySession is { AdoptAppSettings: false }) return;
        var values = LegacySettingValues(rows("configsettings"));
        if (values.Count == 0) return;
        var adopted = new List<string>();
        SaveDocument("preferences", Guid.Empty, ProjectLegacyAppSettings(values, ReadPreferences(transaction), adopted, warnings), transaction);
        foreach (var key in adopted.Distinct(StringComparer.OrdinalIgnoreCase))
            AuditLegacy("configsettings", values[key].Id, "partially-projected", "app-setting", transaction);
    }

    private static Dictionary<string, (string? Value, string? Id)> LegacySettingValues(IEnumerable<JsonElement> rows)
    {
        var values = new Dictionary<string, (string? Value, string? Id)>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows) if (Text(row, "parameter") is { } parameter) values[parameter] = (Text(row, "value1"), LegacyRowKey("configsettings", row));
        return values;
    }

    /// <summary>The preferences with every original setting that has a working counterpart; <paramref name="adopted"/> receives their keys.</summary>
    private static CatalogPreferences ProjectLegacyAppSettings(IReadOnlyDictionary<string, (string? Value, string? Id)> values, CatalogPreferences preferences, List<string> adopted, List<string> warnings)
    {
        var playback = preferences.Playback; var previews = preferences.Previews;
        bool Inverted(string key, bool current) => !Bool(key, !current);
        bool Bool(string key, bool current) { if (!values.TryGetValue(key, out var v) || !bool.TryParse(v.Value, out var b)) return current; adopted.Add(key); return b; }
        double Seconds(string key, double current) { if (!values.TryGetValue(key, out var v) || !double.TryParse(v.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var s) || s is < 1 or > 3600) return current; adopted.Add(key); return s; }
        int Whole(string key, int current, int min, int max) { if (!values.TryGetValue(key, out var v) || !int.TryParse(v.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || n < min || n > max) return current; adopted.Add(key); return n; }
        List<string> Types(string key, List<string> current)
        {
            if (!values.TryGetValue(key, out var v) || string.IsNullOrWhiteSpace(v.Value)) return current;
            var types = v.Value.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(t => "." + t.TrimStart('.').ToLowerInvariant())
                .Where(t => t.Length > 1 && t.IndexOfAny(Path.GetInvalidFileNameChars()) < 0).Distinct().ToList();
            if (types.Count == 0) return current; adopted.Add(key); return types;
        }
        HoverSpeed Speed(string key, HoverSpeed current) { if (!values.TryGetValue(key, out var v) || !Enum.TryParse<HoverSpeed>(v.Value?.Trim(), true, out var s) || !Enum.IsDefined(s) || int.TryParse(v.Value, out _)) return current; adopted.Add(key); return s; }
        // Combo text of the original ("None", "Top Left" … "Bottom Right").
        SheetTimestamp Stamp(string key, SheetTimestamp current)
        {
            if (!values.TryGetValue(key, out var v) || !Enum.TryParse<SheetTimestamp>(v.Value?.Replace(" ", ""), true, out var s) || !Enum.IsDefined(s) || int.TryParse(v.Value, out _)) return current;
            adopted.Add(key); return s;
        }
        string Color(string key, string current)
        {
            if (!values.TryGetValue(key, out var v) || !IsColor(v.Value?.Trim())) return current;
            adopted.Add(key); return v.Value!.Trim().ToUpperInvariant();
        }
        // "Arial:14:False:False:False": family, size and styles; FlixyPix keeps the family and sizes text to the sheet.
        string Font(string key, string current)
        {
            if (!values.TryGetValue(key, out var v) || v.Value?.Split(':')[0].Trim() is not { Length: > 0 } family) return current;
            adopted.Add(key); return family;
        }
        var hotkeys = new Dictionary<string, string>(playback.Hotkeys, StringComparer.Ordinal);
        foreach (var (action, _, legacyKey) in PlayerActions.All)
            if (legacyKey.Length > 0 && values.TryGetValue(legacyKey, out var v)) { hotkeys[action] = ConvertLegacyHotkey(v.Value); adopted.Add(legacyKey); }
        // A shortcut used twice in the original would block saving; keep the first assignment only.
        foreach (var duplicate in hotkeys.Where(p => p.Value.Length > 0).GroupBy(p => p.Value, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).SelectMany(g => g.Skip(1)).ToList())
        { hotkeys[duplicate.Key] = ""; warnings.Add(T("configsettings: shortcut {0} was assigned more than once; “{1}” stays free.", duplicate.Value, duplicate.Key)); }
        playback = playback with
        {
            AlwaysFullscreen = Bool("fullscreen", playback.AlwaysFullscreen),
            SmallJumpForwardSeconds = Seconds("nhotkeysmallffwd", playback.SmallJumpForwardSeconds), SmallJumpBackwardSeconds = Seconds("nhotkeysmallrwnd", playback.SmallJumpBackwardSeconds),
            LargeJumpForwardSeconds = Seconds("nhotkeylargeffwd", playback.LargeJumpForwardSeconds), LargeJumpBackwardSeconds = Seconds("nhotkeylargerwnd", playback.LargeJumpBackwardSeconds),
            Hotkeys = hotkeys,
            PreventSleep = Bool("disablescreensaver", playback.PreventSleep), MediaKeys = Bool("ezfpmediakeys", playback.MediaKeys),
        };
        // Mouse buttons and wheel: the index of the original's combo box ("" means the first entry, "Nothing").
        T Mouse<T>(string key, T current) where T : struct, Enum
        {
            if (!values.TryGetValue(key, out var v)) return current;
            var index = string.IsNullOrWhiteSpace(v.Value) ? 0 : int.TryParse(v.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : -1;
            if (index < 0 || index >= Enum.GetValues<T>().Length || current is PlayerMouseAction && index > (int)PlayerMouseAction.SwitchWheel) return current;
            adopted.Add(key); return Enum.GetValues<T>()[index];
        }
        // The original's wheel increment is a number of seconds (its control falls back to 2).
        double Step(string key, double current) { if (!values.TryGetValue(key, out var v) || !double.TryParse(v.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var s) || s is <= 0 or > 3600) return current; adopted.Add(key); return s; }
        int Percent(string key, int current) { if (!values.TryGetValue(key, out var v) || !int.TryParse(v.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || n is < 0 or > 100) return current; adopted.Add(key); return n; }
        playback = playback with
        {
            WheelStep = Step("ezfpwheelincrement", playback.WheelStep), ResumePlayback = Bool("rememberlastposition", playback.ResumePlayback),
            RememberVolume = Bool("remembervolume", playback.RememberVolume), StartVolume = Percent("defaultvolume", playback.StartVolume),
            AlwaysOnTop = Bool("ezfpplayertopmost", playback.AlwaysOnTop),
            ShowMessages = Bool("ezfpplayermessages", playback.ShowMessages), AutoHideControls = Bool("ezfpplayerautohidecontrols", playback.AutoHideControls),
            KeyboardShortcuts = Bool("enablekeyshortcuts", playback.KeyboardShortcuts), ShowMarks = Bool("showchapters", playback.ShowMarks),
        };
        // Screenshots: folder (kept only when it is a full path), name start, format text ("png", ".jpg", "JPEG") and numbering.
        var shots = playback.Screenshots;
        if (values.TryGetValue("videoscreenshots-directory", out var directory) && directory.Value?.Trim() is { Length: > 0 } folder && Path.IsPathFullyQualified(folder))
        { shots = shots with { Folder = folder }; adopted.Add("videoscreenshots-directory"); }
        if (values.TryGetValue("videoscreenshots-prefix", out var prefix) && prefix.Value is { } start && start.Trim().Length <= 80)
        { shots = shots with { Prefix = start.Trim() }; adopted.Add("videoscreenshots-prefix"); }
        if (values.TryGetValue("videoscreenshots-format", out var format) && format.Value?.Trim().TrimStart('.').ToLowerInvariant() is { } kind && kind is "png" or "jpg" or "jpeg")
        { shots = shots with { Format = kind == "png" ? ScreenshotFormat.Png : ScreenshotFormat.Jpg }; adopted.Add("videoscreenshots-format"); }
        shots = shots with { Numbering = Bool("videoscreenshots-seqnumbering", shots.Numbering) };
        playback = playback with { Screenshots = shots };
        playback = playback with
        {
            LeftClick = Mouse("ezfpleftclick", playback.LeftClick), RightClick = Mouse("ezfprightclick", playback.RightClick), MiddleClick = Mouse("ezfpmiddleclick", playback.MiddleClick),
            BackClick = Mouse("ezfpx1click", playback.BackClick), ForwardClick = Mouse("ezfpx2click", playback.ForwardClick), Wheel = Mouse("ezfpwheelposition", playback.Wheel),
        };
        if (values.TryGetValue("ffmpegcommandline", out var ffmpeg)) { playback = playback with { HardwareDecoding = ffmpeg.Value?.Contains("-hwaccel", StringComparison.OrdinalIgnoreCase) == true }; adopted.Add("ffmpegcommandline"); }
        previews = previews with
        {
            SheetColumns = Whole("vidthumbcolumns", previews.SheetColumns, 1, 6), SheetRows = Whole("vidthumbrows", previews.SheetRows, 1, 6),
            SheetWidth = Whole("vidthumbwidth", previews.SheetWidth, 160, 3840), GeneratorProcesses = Whole("numvidthumbproc", previews.GeneratorProcesses, 1, 8),
            SkipWhenCustomThumbnail = Bool("bypassvideoswithcustomthumbnail", previews.SkipWhenCustomThumbnail),
            VideoExtensions = Types("videofiletypes", previews.VideoExtensions), ImageExtensions = Types("imagefiletypes", previews.ImageExtensions),
            HoverEnabled = Bool("ezfphoverenabled", previews.HoverEnabled), HoverFrames = Whole("ezfphoverthumbnails", previews.HoverFrames, 1, HoverPreview.MaximumFrames),
            HoverSpeed = Speed("ezfphoverspeed", previews.HoverSpeed), HoverDelay = Bool("ezfphoverdelay", previews.HoverDelay), HoverTimestamp = Bool("ezfphovertimestamp", previews.HoverTimestamp),
            SheetHeader = Bool("vidthumbnailheader", previews.SheetHeader), SheetBorder = Whole("vidthumbborder", previews.SheetBorder, 0, 20),
            SheetTimestamp = Stamp("vidthumbtimestamp", previews.SheetTimestamp),
            SheetBackground = Color("vidthumbbackcolor", previews.SheetBackground), SheetForeground = Color("vidthumbforecolor", previews.SheetForeground),
            SheetFont = Font("thumbnailfont", previews.SheetFont),
        };
        var general = preferences.General;
        var wheel = Whole("maximagethumbmousewheelcmd", 0, 1, 4);
        general = general with
        {
            IncludeHiddenFolders = Bool("searchhiddenfolders", general.IncludeHiddenFolders), ConfirmRemove = Inverted("confirmdelete", general.ConfirmRemove),
            ShowExtension = Bool("showmediaextension", general.ShowExtension), MarkMissingFiles = Bool("highlightvideonotfound", general.MarkMissingFiles),
            AlwaysShowImportReport = Bool("alwaysshowimporterrors", general.AlwaysShowImportReport), RestoreLastView = Bool("loadlastvideoview", general.RestoreLastView),
            ImageWheel = wheel switch { 1 => ImageWheelAction.Zoom, 2 => ImageWheelAction.Navigate, 3 or 4 => ImageWheelAction.Scroll, _ => general.ImageWheel },
            ShowTitles = Bool("displayvideotitle", general.ShowTitles), FullTitles = Bool("displaymoviename", general.FullTitles),
            HoverLift = Bool("enablemousehoverzoomthumbnail", general.HoverLift), ShowPager = Bool("pageselectioncontrols", general.ShowPager),
            HistoryLength = Whole("navigationhistorystore", general.HistoryLength, 1, GeneralPreferences.MaximumHistoryLength),
            RememberWindow = Bool("rememberform", general.RememberWindow), ImageFade = Bool("image_fade", general.ImageFade),
            DeleteToRecycleBin = Bool("deletetorecyclebin", general.DeleteToRecycleBin),
            // The original kept separate switches for its video and image views; the video switch decides, the image switch only when it is missing.
            ShowFolderCounts = Bool("showvideofoldercounts", Bool("showimagefoldercounts", general.ShowFolderCounts)),
            ShowCategoryCounts = Bool("showvideocategorycounts", Bool("showimagecategorycounts", general.ShowCategoryCounts)),
            ShowTagCategoryCounts = Bool("showvideotagcategorycounts", Bool("showimagetagcategorycounts", general.ShowTagCategoryCounts)),
        };
        // "X" shows the progress bar, anything else hides it; the click combo stores its text; the tag filter "all", "match" or "none".
        if (values.TryGetValue("showvideoprogress", out var progress)) { general = general with { ShowProgress = progress.Value?.Trim() == "X" }; adopted.Add("showvideoprogress"); }
        if (values.TryGetValue("clickvidthumb", out var click) && click.Value?.Trim() is { Length: > 0 } clickText)
        { general = general with { ClickPlays = clickText.Equals("Plays video", StringComparison.OrdinalIgnoreCase) }; adopted.Add("clickvidthumb"); }
        if (values.TryGetValue("videotagmatchtype", out var match) && match.Value?.Trim().ToLowerInvariant() is "all" or "match")
        { general = general with { TagsMatchAny = match.Value.Trim().Equals("match", StringComparison.OrdinalIgnoreCase) }; adopted.Add("videotagmatchtype"); }
        // Import: characters for titles from file names, and name parts of images to leave out ("a, b" or "a;b").
        string Characters(string key, string current) { if (!values.TryGetValue(key, out var v) || v.Value is not { Length: > 0 } chars) return current; adopted.Add(key); return new string(chars.Distinct().ToArray()); }
        general = general with { TitleRemoveCharacters = Characters("fileexclusionchars", general.TitleRemoveCharacters), TitleSpaceCharacters = Characters("filereplacechars", general.TitleSpaceCharacters) };
        if (values.TryGetValue("filteroutpic", out var skip) && skip.Value?.Split([',', ';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) is { Length: > 0 } parts)
        { general = general with { SkipImageNames = parts.Distinct(StringComparer.OrdinalIgnoreCase).ToList() }; adopted.Add("filteroutpic"); }
        // Slideshow: interval combo "0" … "30" seconds, order combo text, switches.
        double Interval(string key, double current)
        {
            if (!values.TryGetValue(key, out var v) || !double.TryParse(v.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var s) || s is < 0 or > SlideshowPreferences.MaximumInterval) return current;
            adopted.Add(key); return Math.Max(SlideshowPreferences.MinimumInterval, s);
        }
        SlideshowOrder Sequence(string key, SlideshowOrder current)
        {
            if (!values.TryGetValue(key, out var v) || !Enum.TryParse<SlideshowOrder>(v.Value?.Replace(" ", ""), true, out var order) || !Enum.IsDefined(order) || int.TryParse(v.Value, out _)) return current;
            adopted.Add(key); return order;
        }
        var slideshow = preferences.Slideshow;
        slideshow = slideshow with
        {
            IntervalSeconds = Interval("slideshow_interval", slideshow.IntervalSeconds), Order = Sequence("slideshow_display", slideshow.Order),
            Fade = Bool("slideshow_fade", slideshow.Fade), PanAndZoom = Bool("slideshow_pan", slideshow.PanAndZoom),
            Speed = Speed("slideshow_speed", slideshow.Speed), ShowInfo = Bool("image_details", slideshow.ShowInfo),
        };
        // NFO import: switches and the element lists ("director;genre;…").
        string Elements(string key, string current)
        {
            if (!values.TryGetValue(key, out var v) || NfoPreferences.Split(v.Value ?? "") is not { Count: > 0 } list) return current;
            adopted.Add(key); return string.Join(";", list);
        }
        var nfo = preferences.Nfo;
        nfo = nfo with
        {
            ImportTitle = Bool("chkimportnfo_title", nfo.ImportTitle), ImportTags = Bool("chkimportnfo_tags", nfo.ImportTags), ImportDescription = Bool("chkimportnfo_description", nfo.ImportDescription),
            SearchSubfolders = Bool("chkimportnfo_searchsubfolders", nfo.SearchSubfolders),
            ImportThumbnail = Bool("chkimportnfo_thumbnail", nfo.ImportThumbnail), ImportFanart = Bool("chkimportnfo_fanart", nfo.ImportFanart),
            TagElements = Elements("importnfodata_tags", nfo.TagElements), DescriptionElements = Elements("importnfodata_description", nfo.DescriptionElements),
        };
        // Start page: the original's four sections are digits 1-4 in "homevideosections"; FlixyPix's further rows stay.
        var home = preferences.Home with { RecentDays = Whole("recentlywatcheddays", preferences.Home.RecentDays, 1, HomePreferences.MaximumRecentDays) };
        if (values.TryGetValue("homevideosections", out var sections) && sections.Value is { } digits && digits.All(char.IsDigit))
        {
            HomeRowKind[] original = [HomeRowKind.ContinueWatching, HomeRowKind.RecentlyAdded, HomeRowKind.WatchLater, HomeRowKind.FavoritePerformers];
            var off = original.Where((_, index) => !digits.Contains((char)('1' + index))).ToHashSet();
            home = home with { Rows = home.Rows.Where(r => !off.Contains(r)).ToList() };
            adopted.Add("homevideosections");
        }
        return preferences with { Playback = playback, Previews = previews, General = general, Slideshow = slideshow, Nfo = nfo, Home = home };
    }
}
