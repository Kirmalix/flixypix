using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

/// <summary>Where the loaded settings came from when appsettings.json was damaged.</summary>
internal enum SettingsSource { File, LastGoodCopy, Defaults }

/// <summary>What loading appsettings.json had to repair: the values reset to their defaults, where the settings came
/// from and where the damaged file was kept.</summary>
internal sealed record SettingsRepair(IReadOnlyList<string> ResetValues, SettingsSource Source, string? DamagedCopy)
{
 internal string Summary => Source == SettingsSource.LastGoodCopy ? T("The settings file was damaged; the last working copy was used.")
  : Source == SettingsSource.Defaults ? T("The settings file was damaged; FlixyPix starts with default settings.")
  : ResetValues.Count == 0 ? T("The settings file was damaged and has been repaired.")
  : Tn(ResetValues.Count, "The settings file was damaged and has been repaired; 1 value was reset: {1}.",
   "The settings file was damaged and has been repaired; {0:N0} values were reset: {1}.", string.Join(", ", ResetValues));
}

/// <summary>Loading that survives a hand-edited or truncated appsettings.json: whatever is readable and valid is kept,
/// the rest falls back to its default, an unreadable file gives way to the last good copy. The damaged file is kept beside
/// it, the repaired one is written back so later loads do not repair again.</summary>
internal sealed partial record DesktopSettings
{
 private static readonly JsonDocumentOptions Lenient = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };
 private static readonly string[] Themes = ["system", "light", "dark"];
 /// <summary>The repair of the last load in this process, for a notice at start; null when nothing had to be repaired.</summary>
 internal static SettingsRepair? LastRepair { get; private set; }
 internal static string LastGoodPath(string path) => Path.ChangeExtension(path, ".last-good.json");

 internal static DesktopSettings LoadFrom(string path)
 {
  if (!File.Exists(path)) return new();
  var text = File.ReadAllText(path);
  var reset = new List<string>(); var source = SettingsSource.File;
  var read = TryRead(text); var damaged = read == null;
  if (damaged)
  {
   read = Salvage(text, reset);
   if (read == null) { read = TryReadFile(LastGoodPath(path)); source = read != null ? SettingsSource.LastGoodCopy : SettingsSource.Defaults; }
  }
  var settings = (read ?? new()).Validated(reset);
  if (!damaged && reset.Count == 0) return settings;
  LastRepair = new SettingsRepair(reset, source, KeepDamaged(path));
  try { settings.SaveTo(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { }
  return settings;
 }

 /// <summary>The settings in a text; null when they do not read as a whole.</summary>
 private static DesktopSettings? TryRead(string text)
 {
  try { return JsonSerializer.Deserialize<DesktopSettings>(text); }
  catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException) { return null; }
 }
 private static DesktopSettings? TryReadFile(string path)
 {
  try { return File.Exists(path) ? TryRead(File.ReadAllText(path)) : null; }
  catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
 }

 /// <summary>Takes every property that reads on its own; the others are named in <paramref name="reset"/>. Null when the
 /// text is no JSON object at all.</summary>
 private static DesktopSettings? Salvage(string text, List<string> reset)
 {
  JsonObject? source;
  try { source = JsonNode.Parse(text, documentOptions: Lenient) as JsonObject; }
  catch (JsonException) { return null; }
  if (source == null) return null;
  var kept = new JsonObject();
  foreach (var property in typeof(DesktopSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanWrite))
  {
   var entry = source.FirstOrDefault(e => string.Equals(e.Key, property.Name, StringComparison.OrdinalIgnoreCase));
   if (entry.Key == null) continue;
   try
   {
    var json = entry.Value?.ToJsonString() ?? "null";
    JsonSerializer.Deserialize(json, property.PropertyType);
    kept[property.Name] = JsonNode.Parse(json);
   }
   catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException) { reset.Add(property.Name); }
  }
  try { return JsonSerializer.Deserialize<DesktopSettings>(kept.ToJsonString()); }
  catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException) { return null; }
 }

 private static string? KeepDamaged(string path)
 {
  try
  {
   var target = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + ".broken-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json");
   File.Copy(path, target, overwrite: true); return target;
  }
  catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
 }

 /// <summary>Values that read but cannot be used (missing lists, unknown theme or language, a relative library folder)
 /// fall back to their defaults and are named in <paramref name="reset"/>.</summary>
 internal DesktopSettings Validated(List<string> reset)
 {
  var defaults = new DesktopSettings(); var result = this;
  T Fix<T>(string name, bool invalid, T value, T replacement) { if (!invalid) return value; reset.Add(name); return replacement; }
  static List<string> Clean(List<string>? values) => values?.Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? [];
  result = result with
  {
   DataDirectory = Fix(nameof(DataDirectory), string.IsNullOrWhiteSpace(DataDirectory) || !Path.IsPathFullyQualified(DataDirectory), DataDirectory, defaults.DataDirectory),
   Theme = Fix(nameof(Theme), !Themes.Contains(Theme ?? "", StringComparer.OrdinalIgnoreCase), Theme, defaults.Theme)!,
   Language = Fix(nameof(Language), Language != Texts.Automatic && Texts.Find(Language) == null, Language, defaults.Language)!,
   LastView = Fix(nameof(LastView), string.IsNullOrWhiteSpace(LastView), LastView, defaults.LastView)!,
   BossKey = BossKey ?? "",
   Privacy = (Privacy ?? new()).Validated(reset, nameof(Privacy) + "."),
   PlayerVolume = Fix(nameof(PlayerVolume), PlayerVolume is < 0 or > 100, PlayerVolume, defaults.PlayerVolume),
   MainWindowPlace = Fix(nameof(MainWindowPlace), MainWindowPlace is { } place && !(double.IsFinite(place.Left) && double.IsFinite(place.Top)
    && double.IsFinite(place.Width) && double.IsFinite(place.Height) && place.Width > 0 && place.Height > 0), MainWindowPlace, null),
   Ffmpeg = string.IsNullOrWhiteSpace(Ffmpeg) ? null : Ffmpeg, Ffprobe = string.IsNullOrWhiteSpace(Ffprobe) ? null : Ffprobe, Mpv = string.IsNullOrWhiteSpace(Mpv) ? null : Mpv,
   ProtectedSourcePaths = Clean(ProtectedSourcePaths), QuickAccessFolders = Clean(QuickAccessFolders),
   Libraries = (Libraries ?? []).Where(l => l != null && !string.IsNullOrWhiteSpace(l.Directory) && Path.IsPathFullyQualified(l.Directory))
    .DistinctBy(l => Path.TrimEndingDirectorySeparator(l.Directory), StringComparer.OrdinalIgnoreCase).ToList(),
   ExternalPrograms = (ExternalPrograms ?? []).Where(p => p != null).ToList(),
   WebSearchProviders = WebSearchProviders?.Where(p => p != null && !string.IsNullOrWhiteSpace(p.Name) && !string.IsNullOrWhiteSpace(p.Url)).ToList(),
  };
  // Dropped entries of lists count as a repair too, so the user hears about them.
  if (result.Libraries.Count != (Libraries?.Count ?? 0)) reset.Add(nameof(Libraries));
  if (result.ExternalPrograms.Count != (ExternalPrograms?.Count ?? 0)) reset.Add(nameof(ExternalPrograms));
  if ((result.WebSearchProviders?.Count ?? 0) != (WebSearchProviders?.Count ?? 0)) reset.Add(nameof(WebSearchProviders));
  return result;
 }
}
