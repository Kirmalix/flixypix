namespace FlixyPix.Core;

public sealed partial class CatalogRepository
{
 public const string GeneratedThumbnailStyleKey="thumbnail.style";

 /// <summary>Publishes a rendered thumbnail only if its original source and thumbnail are still current.</summary>
 public MediaItem SetGeneratedThumbnail(MediaItem expected,PlaybackSource source,string path,ContactSheetMetadata? sheet,string style)
 {
  ArgumentException.ThrowIfNullOrWhiteSpace(path);ArgumentException.ThrowIfNullOrWhiteSpace(style);
  if(expected.Id!=source.MediaId)throw new ArgumentException("The thumbnail source belongs to another medium.");
  sheet?.Validate();
  if(sheet is not null&&(PathKey(sheet.SourcePath)!=PathKey(source.Path)||sheet.SourceSize!=source.Size||sheet.SourceModifiedAtUtc!=source.ModifiedAtUtc||!sheet.MatchesImage(path)))
   throw new ArgumentException("The contact sheet does not match its rendered image or source.");
  if(!File.Exists(path))throw new IOException("The generated thumbnail is unavailable.");
  lock(gate)
  {
   ThrowIfDisposed();using var transaction=connection.BeginTransaction();
   var current=ReadMediaDocument(expected.Id,transaction)??throw new ArgumentException("Unknown media.");
   if(current.Kind!=MediaKind.Video||PathKey(current.Path)!=PathKey(source.Path)||current.ThumbnailPath!=expected.ThumbnailPath||
    current.Extra.GetValueOrDefault("thumbnail.assetId")!=expected.Extra.GetValueOrDefault("thumbnail.assetId")||
    current.Extra.GetValueOrDefault(GeneratedThumbnailStyleKey)!=expected.Extra.GetValueOrDefault(GeneratedThumbnailStyleKey))
    throw new InvalidOperationException("The thumbnail or main version changed while rendering.");
   var file=new FileInfo(source.Path);
   if(!file.Exists||file.Length!=source.Size||file.LastWriteTimeUtc.Ticks!=source.ModifiedAtUtc.UtcTicks)throw new InvalidOperationException("The thumbnail source changed while rendering.");
   var extra=new Dictionary<string,string?>(current.Extra){[GeneratedThumbnailStyleKey]=style};
   extra.Remove("thumbnail.assetId");
   var saved=UpsertCore(current with {ThumbnailPath=path,ContactSheet=sheet,Extra=extra,
    DurationSeconds=current.DurationSeconds??expected.DurationSeconds,Width=current.Width??expected.Width,Height=current.Height??expected.Height},transaction);
   transaction.Commit();return saved;
  }
 }

 /// <summary>Applies background analysis to a fresh document without replacing edits made during decoding.</summary>
 internal MediaItem? SetPreparedMedia(MediaItem expected,MediaItem prepared,long sourceSize,DateTimeOffset sourceModifiedAtUtc)
 {
  if(expected.Id!=prepared.Id||PathKey(expected.Path)!=PathKey(prepared.Path))throw new ArgumentException("The prepared medium changed identity.");
  prepared.ContactSheet?.Validate();
  lock(gate)
  {
   ThrowIfDisposed();using var transaction=connection.BeginTransaction();
   var current=ReadMediaDocument(expected.Id,transaction);
   if(current==null||current.Kind!=expected.Kind||PathKey(current.Path)!=PathKey(expected.Path)||current.FileModifiedAtUtc!=expected.FileModifiedAtUtc)return null;
   var file=new FileInfo(expected.Path);
   if(!file.Exists||file.Length!=sourceSize||file.LastWriteTimeUtc.Ticks!=sourceModifiedAtUtc.UtcTicks)return null;
   var extra=new Dictionary<string,string?>(current.Extra);
   var changeThumbnail=current.ThumbnailPath==expected.ThumbnailPath&&
    current.Extra.GetValueOrDefault("thumbnail.assetId")==expected.Extra.GetValueOrDefault("thumbnail.assetId")&&
    current.Extra.GetValueOrDefault(GeneratedThumbnailStyleKey)==expected.Extra.GetValueOrDefault(GeneratedThumbnailStyleKey);
   if(changeThumbnail&&prepared.ContactSheet is {} sheet&&prepared.ThumbnailPath!=expected.ThumbnailPath&&
    (prepared.ThumbnailPath==null||!sheet.MatchesImage(prepared.ThumbnailPath)||!sheet.MatchesSource(prepared)))throw new ArgumentException("The prepared contact sheet does not match its image or source.");
   if(changeThumbnail&&prepared.Extra.TryGetValue(GeneratedThumbnailStyleKey,out var style))extra[GeneratedThumbnailStyleKey]=style;
   if(changeThumbnail&&prepared.ThumbnailPath!=expected.ThumbnailPath&&prepared.Extra.ContainsKey(GeneratedThumbnailStyleKey))extra.Remove("thumbnail.assetId");
   if(!prepared.Extra.ContainsKey("folders.previewStale")&&current.Extra.GetValueOrDefault("folders.previewStale")==expected.Extra.GetValueOrDefault("folders.previewStale"))extra.Remove("folders.previewStale");
   var saved=UpsertCore(current with {Width=current.Width==expected.Width?prepared.Width:current.Width,Height=current.Height==expected.Height?prepared.Height:current.Height,
    DurationSeconds=current.DurationSeconds==expected.DurationSeconds?prepared.DurationSeconds:current.DurationSeconds,Technical=current.Technical==expected.Technical?prepared.Technical:current.Technical,
    Kind=prepared.Kind,ThumbnailPath=changeThumbnail?prepared.ThumbnailPath:current.ThumbnailPath,ContactSheet=changeThumbnail?prepared.ContactSheet:current.ContactSheet,Extra=extra},transaction);
   transaction.Commit();return saved;
  }
 }
}
