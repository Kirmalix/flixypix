using System.Text.Json;

namespace FlixyPix.Core;

public sealed record FolderFavorite(string Path,MediaKind? Kind = null,string? Category = null);
/// <summary>A tag known to the library, also without media; aliases are other spellings that find it.</summary>
public sealed record TagVocabularyEntry(string Tag,string Category,bool IsFavorite = false,string? LegacyId = null,List<string>? Aliases = null,string? Description = null,TagProfile? Profile = null);
public sealed record TextReplacement(string Find,string Replace);
public sealed record FilenameParserPreset
{
 public Guid Id { get; init; } = Guid.NewGuid();
 public string Name { get; init; } = "";
 public string Separators { get; init; } = "_-.,;";
 public int ParentFolderDepth { get; init; }
 public List<TextReplacement> Replacements { get; init; } = [];
 public List<string> Exclusions { get; init; } = [];
 public string? Category { get; init; }
}

public sealed partial class CatalogRepository
{
 public void ToggleFolderFavorite(FolderFavorite favorite)
 {
  if (string.IsNullOrWhiteSpace(favorite.Path) || favorite.Kind is { } kind && !Enum.IsDefined(kind)) throw new ArgumentException("Invalid favorite folder.");
  var full = PathKey(favorite.Path);
  lock(gate)
  {
   var preferences=GetPreferences(); var favorites=preferences.FolderFavorites.ToList();
   var existing=favorites.FindIndex(f=>PathKey(f.Path)==full && f.Kind==favorite.Kind && string.Equals(f.Category,favorite.Category,StringComparison.OrdinalIgnoreCase));
   if(existing>=0)favorites.RemoveAt(existing);else favorites.Add(favorite with { Path=System.IO.Path.GetFullPath(favorite.Path) });
   SavePreferences(preferences with { FolderFavorites=favorites });
  }
 }
 public void UpsertVocabularyTag(TagVocabularyEntry value)
 {
  if(string.IsNullOrWhiteSpace(value.Tag)||string.IsNullOrWhiteSpace(value.Category))throw new ArgumentException("Tag and category are required.");
  lock(gate)
  {
   var preferences=GetPreferences(); var tags=preferences.TagVocabulary.Where(t=>!Same(t.Tag,value.Tag)||!Same(t.Category,value.Category)).ToList();
   tags.Add(value with { Tag=value.Tag.Trim(),Category=value.Category.Trim() });
   SavePreferences(preferences with { TagVocabulary=tags });
  }
 }
 public FilenameParserPreset UpsertFilenameParser(FilenameParserPreset preset)
 {
  ValidateParser(preset);
  lock(gate)
  {
   var preferences=GetPreferences(); SavePreferences(preferences with { FilenameParsers=preferences.FilenameParsers.Where(p=>p.Id!=preset.Id).Append(preset).ToList() }); return preset;
  }
 }
 public static IReadOnlyList<string> ParseFilenameTags(string path,FilenameParserPreset preset)
 {
  ValidateParser(preset);
  var segments=new List<string>{System.IO.Path.GetFileNameWithoutExtension(path)};
  var directory=System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
  for(var i=0;i<preset.ParentFolderDepth && directory!=null;i++,directory=System.IO.Path.GetDirectoryName(directory))
  { var name=System.IO.Path.GetFileName(directory);if(name.Length>0)segments.Add(name); }
  var tags=new List<string>();
  foreach(var segment in segments)
  {
   var text=segment;
   foreach(var replacement in preset.Replacements)text=text.Replace(replacement.Find,replacement.Replace,StringComparison.OrdinalIgnoreCase);
   tags.AddRange(text.Split(preset.Separators.ToCharArray(),StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries)
    .Where(t=>!preset.Exclusions.Any(e=>Same(e,t))));
  }
  return tags.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
 }
 public int ApplyFilenameParser(Guid presetId,Guid? mediaId=null,CancellationToken cancellationToken=default)
 {
  lock(gate)
  {
   ThrowIfDisposed();using var transaction=connection.BeginTransaction();
   var preset=ReadPreferences(transaction).FilenameParsers.Single(p=>p.Id==presetId);
   using var idsCommand=Command("SELECT id FROM media ORDER BY rowid",transaction);var ids=new List<Guid>();
   using(var reader=idsCommand.ExecuteReader())while(reader.Read())ids.Add(Guid.Parse(reader.GetString(0)));
   var changed=0;
   foreach(var id in ids)
   {
    cancellationToken.ThrowIfCancellationRequested(); if(mediaId is { } selected && selected!=id)continue;
    using var command=Command("SELECT document FROM media WHERE id=$id",transaction,("$id",id.ToString()));
    var item=JsonSerializer.Deserialize<MediaItem>((string)command.ExecuteScalar()!,Json)!;
    var additions=ParseFilenameTags(item.Path,preset); var tags=item.Tags.Concat(additions).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    var categories=item.TagCategories.ToDictionary(p=>p.Key,p=>p.Value.ToList(),StringComparer.OrdinalIgnoreCase);
    if(!string.IsNullOrWhiteSpace(preset.Category))categories[preset.Category]=categories.GetValueOrDefault(preset.Category,[]).Concat(additions).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    if(tags.Count==item.Tags.Count && JsonSerializer.Serialize(categories,Json)==JsonSerializer.Serialize(item.TagCategories,Json))continue;
    UpsertCore(item with { Tags=tags,TagCategories=categories },transaction);changed++;
   }
   transaction.Commit();return changed;
  }
 }
 private static bool Same(string? left,string? right)=>string.Equals(left,right,StringComparison.OrdinalIgnoreCase);
 private static void ValidateParser(FilenameParserPreset preset)
 {
  ArgumentNullException.ThrowIfNull(preset);
  if(preset.Id==Guid.Empty||string.IsNullOrWhiteSpace(preset.Name)||preset.ParentFolderDepth is <0 or
   >32||string.IsNullOrEmpty(preset.Separators)||preset.Separators.Length>256||preset.Replacements==null||preset.Exclusions==null||preset.Replacements.Count>256||preset.Exclusions.Count>10000||preset.Exclusions.Any(e=>e==null)||preset.Replacements.Any(r=>r==null||string.IsNullOrEmpty(r.Find)||r.Replace==null))throw
   new ArgumentException("Invalid filename parser preset.");
 }
}
