using System.Runtime.CompilerServices;
using System.Text;
using ImageMagick;

namespace FlixyPix.Imaging;

using FlixyPix.Core;

/// <summary>Metadata embedded in images, as the original read it with MetadataExtractor: XMP first, then IPTC
/// (object name, caption, keywords), then EXIF (Windows title, subject and keywords, description, rating, capture date).
/// Only the header is read (ping); the image is neither decoded nor changed.</summary>
public sealed class ImageMetadataReader : IFileMetadataReader
{
 // The resource limits of the image service apply here too.
 static ImageMetadataReader() => RuntimeHelpers.RunClassConstructor(typeof(ImageService).TypeHandle);

 public FileMetadata? Read(string path, MediaKind kind)
 {
  if (kind != MediaKind.Image) return null;
  using var stream = new FileStream(Path.GetFullPath(path), FileMode.Open, FileAccess.Read, FileShare.Read);
  ImageService.ValidateSignature(stream);
  using var image = new MagickImage();
  try { image.Ping(stream, ImageService.Settings(stream)); }
  catch (MagickException error) { throw new InvalidDataException("Bild konnte nicht gelesen werden: " + error.Message, error); }
  FileMetadata? xmp = null;
  try { if (image.GetXmpProfile() is { } profile && profile.ToXDocument() is { } document) xmp = XmpSidecar.Parse(document.ToString()); }
  catch (System.Xml.XmlException) { }
  var metadata = (xmp ?? new FileMetadata()).Or(Iptc(image.GetIptcProfile())).Or(Exif(image.GetExifProfile()));
  return metadata.IsEmpty ? null : metadata;
 }

 private static FileMetadata? Iptc(IIptcProfile? iptc)
 {
  if (iptc == null) return null;
  string? Value(IptcTag tag) => FileMetadata.Clean(iptc.GetValue(tag)?.Value);
  var date = Value(IptcTag.CreatedDate) is { Length: 8 } day ? day[..4] + "-" + day[4..6] + "-" + day[6..] + (Value(IptcTag.CreatedTime) is { Length: >= 6 } time ? "T" + time[..2] + ":" + time[2..4] + ":" + time[4..6] + time[6..] : "") : null;
  return new()
  {
   Title = Value(IptcTag.Title), Description = Value(IptcTag.Caption), TakenAt = FileMetadata.ParseDate(date),
   Keywords = iptc.GetAllValues(IptcTag.Keyword).Select(v => FileMetadata.Clean(v.Value)).OfType<string>().ToArray(),
  };
 }

 private static FileMetadata? Exif(IExifProfile? exif)
 {
  if (exif == null) return null;
  // The Windows fields (XP…) are UTF-16 text with a closing zero.
  string? Windows(ExifTag<byte[]> tag) => exif.GetValue(tag)?.Value is { } bytes ? FileMetadata.Clean(Encoding.Unicode.GetString(bytes)) : null;
  var rating = exif.GetValue(ExifTag.Rating)?.Value;
  return new()
  {
   Title = Windows(ExifTag.XPTitle), Description = Windows(ExifTag.XPSubject) ?? FileMetadata.Clean(exif.GetValue(ExifTag.ImageDescription)?.Value),
   Stars = rating is >= 1 and <= 5 ? rating : null, TakenAt = FileMetadata.ParseDate(exif.GetValue(ExifTag.DateTimeOriginal)?.Value),
   Keywords = FileMetadata.SplitKeywords(Windows(ExifTag.XPKeywords)).ToArray(),
  };
 }
}
