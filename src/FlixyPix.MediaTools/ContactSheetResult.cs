using FlixyPix.Core;
using System.Security.Cryptography;
namespace FlixyPix.MediaTools;
/// <summary>Positions refer to original image pixels; times refer to the frames actually rendered.</summary>
public sealed record ContactSheetResult(string ImagePath,int Width,int Height,string SourcePath,long SourceSize,DateTimeOffset SourceModifiedAtUtc,IReadOnlyList<ContactSheetCell> Cells)
{
 public ContactSheetMetadata Metadata()
 {
  using var image=File.OpenRead(ImagePath);
  return new ContactSheetMetadata(Width,Height,Convert.ToHexString(SHA256.HashData(image)),SourcePath,SourceSize,SourceModifiedAtUtc,Cells).Validate();
 }
 public ContactSheetCell? HitTest(double x,double y)
 =>double.IsFinite(x)&&double.IsFinite(y)?Cells.FirstOrDefault(cell=>cell.Bounds.Contains(x,y)):null;
 public bool SourceUnchanged()
 {
  var file=new FileInfo(SourcePath);return file.Exists&&file.Length==SourceSize&&file.LastWriteTimeUtc.Ticks==SourceModifiedAtUtc.UtcTicks;
 }
}
