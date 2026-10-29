using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using FlixyPix.Core;
using FlixyPix.Imaging;
using ImageMagick;
using Xunit;

namespace FlixyPix.Imaging.Tests;

public sealed class ImageMetadataReaderTests : IDisposable
{
 private readonly string root = Path.Combine(Path.GetTempPath(), "flixypix-image-metadata-" + Guid.NewGuid().ToString("N"));
 public ImageMetadataReaderTests() => Directory.CreateDirectory(root);
 public void Dispose() { try { Directory.Delete(root, true); } catch (IOException) { } }

 private string Make(string name, Action<MagickImage> profiles)
 {
  var path = Path.Combine(root, name); using var image = new MagickImage(new MagickColor("teal"), 40, 30); profiles(image); image.Write(path, MagickFormat.Jpeg); return path;
 }

 [Fact]
 public void ExifAndIptcGiveTitleDescriptionRatingDateAndKeywords()
 {
  var path = Make("exif.jpg", image =>
  {
   var exif = new ExifProfile();
   exif.SetValue(ExifTag.XPTitle, Encoding.Unicode.GetBytes("Windows-Titel\0"));
   exif.SetValue(ExifTag.XPKeywords, Encoding.Unicode.GetBytes("Urlaub;Strand\0"));
   exif.SetValue(ExifTag.ImageDescription, "OLYMPUS DIGITAL CAMERA");
   exif.SetValue(ExifTag.Rating, (ushort)4);
   exif.SetValue(ExifTag.DateTimeOriginal, "2024:07:14 18:30:00");
   image.SetProfile(exif);
   var iptc = new IptcProfile();
   iptc.SetValue(IptcTag.Title, "IPTC-Titel");
   iptc.SetValue(IptcTag.Caption, "Abendlicht");
   iptc.SetValue(IptcTag.Keyword, "Meer");
   image.SetProfile(iptc);
  });
  var hash = SHA256.HashData(File.ReadAllBytes(path));
  var metadata = new ImageMetadataReader().Read(path, MediaKind.Image)!;
  // IPTC comes before EXIF, the camera's placeholder text is ignored.
  Assert.Equal("IPTC-Titel", metadata.Title);
  Assert.Equal("Abendlicht", metadata.Description);
  Assert.Equal(4, metadata.Stars);
  Assert.Equal(new DateTime(2024, 7, 14, 18, 30, 0), metadata.TakenAt!.Value.DateTime);
  Assert.Equal(new[] { "Meer", "Urlaub", "Strand" }, metadata.Keywords);
  Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(path)));
 }

 [Fact]
 public void EmbeddedXmpComesFirstAndKeepsItsHierarchy()
 {
  XNamespace rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#", dc = "http://purl.org/dc/elements/1.1/", lr = "http://ns.adobe.com/lightroom/1.0/";
  var xmp = new XDocument(new XElement(XName.Get("xmpmeta", "adobe:ns:meta/"), new XElement(rdf + "RDF", new XElement(rdf + "Description", new XAttribute(rdf + "about", ""),
   new XElement(dc + "title", new XElement(rdf + "Alt", new XElement(rdf + "li", new XAttribute(XNamespace.Xml + "lang", "x-default"), "XMP-Titel"))),
   new XElement(lr + "hierarchicalSubject", new XElement(rdf + "Bag", new XElement(rdf + "li", "Personen|Anna")))))));
  var path = Make("xmp.jpg", image =>
  {
   image.SetProfile(XmpProfile.FromXDocument(xmp));
   var iptc = new IptcProfile(); iptc.SetValue(IptcTag.Title, "IPTC-Titel"); image.SetProfile(iptc);
  });
  var metadata = new ImageMetadataReader().Read(path, MediaKind.Image)!;
  Assert.Equal("XMP-Titel", metadata.Title);
  Assert.Equal(new[] { "Anna" }, metadata.TagsByCategory()["Personen"]);
 }

 [Fact]
 public void ImagesWithoutMetadataAndOtherKindsGiveNothing()
 {
  var path = Make("leer.jpg", image => image.Strip());
  Assert.Null(new ImageMetadataReader().Read(path, MediaKind.Image));
  Assert.Null(new ImageMetadataReader().Read(path, MediaKind.Video));
 }
}
