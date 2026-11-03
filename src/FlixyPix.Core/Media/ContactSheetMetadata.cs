using System.Security.Cryptography;

namespace FlixyPix.Core;

public sealed record SheetRectangle(int X,int Y,int Width,int Height)
{
 public bool Contains(double x,double y)=>x>=X&&y>=Y&&x<X+(long)Width&&y<Y+(long)Height;
}
public sealed record ContactSheetCell(int Index,SheetRectangle Bounds,double Seconds);
/// <summary>Derived layout bound to the rendered image bytes and the source version.</summary>
public sealed record ContactSheetMetadata(int Width,int Height,string ImageSha256,string SourcePath,long SourceSize,DateTimeOffset SourceModifiedAtUtc,IReadOnlyList<ContactSheetCell> Cells)
{
 public ContactSheetMetadata Validate()
 {
  if(Width<=0||Height<=0||ImageSha256==null||ImageSha256.Length!=64||ImageSha256.Any(c=>!Uri.IsHexDigit(c))||string.IsNullOrWhiteSpace(SourcePath)||SourceSize<0||Cells==null||Cells.Count is <1 or >36)throw new ArgumentException("Invalid contact-sheet metadata.");
  _=Path.GetFullPath(SourcePath);
  for(var i=0;i<Cells.Count;i++)
  {
   var cell=Cells[i];
   if(cell==null||cell.Index!=i||cell.Bounds is not {} bounds||bounds.Width<=0||bounds.Height<=0||bounds.X<0||bounds.Y<0||(long)bounds.X+bounds.Width>Width||(long)bounds.Y+bounds.Height>Height||!double.IsFinite(cell.Seconds)||cell.Seconds<0)throw new ArgumentException("Invalid contact-sheet cell.");
   if(Cells.Take(i).Any(prior=>prior.Bounds.X<(long)bounds.X+bounds.Width&&(long)prior.Bounds.X+prior.Bounds.Width>bounds.X&&prior.Bounds.Y<(long)bounds.Y+bounds.Height&&(long)prior.Bounds.Y+prior.Bounds.Height>bounds.Y))throw new ArgumentException("Overlapping contact-sheet cells.");
  }
  return this;
 }
 public ContactSheetCell? HitTest(double x,double y)=>double.IsFinite(x)&&double.IsFinite(y)?Cells.FirstOrDefault(cell=>cell.Bounds.Contains(x,y)):null;
 public bool MatchesImage(string path)
 {
  try{using var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);return Convert.ToHexStringLower(SHA256.HashData(input)).Equals(ImageSha256,StringComparison.OrdinalIgnoreCase);}
  catch(Exception error)when(error is IOException or UnauthorizedAccessException or ArgumentException){return false;}
 }
 public bool MatchesSource(MediaItem item)
 {
  if(item.Kind!=MediaKind.Video||!MediaVersions.All(item).Any(file=>CatalogRepository.PathKey(file.Path)==CatalogRepository.PathKey(SourcePath)))return false;
  try{var source=new FileInfo(SourcePath);return source.Exists&&source.Length==SourceSize&&source.LastWriteTimeUtc.Ticks==SourceModifiedAtUtc.UtcTicks;}
  catch(Exception error)when(error is IOException or UnauthorizedAccessException or ArgumentException){return false;}
 }
 /// <summary>Uniform image rendering, including letterboxing. View coordinates are device-independent units.</summary>
 public ContactSheetCell? HitTestView(double x,double y,double viewWidth,double viewHeight)
 {
  if(!double.IsFinite(viewWidth)||!double.IsFinite(viewHeight)||viewWidth<=0||viewHeight<=0)return null;
  var scale=Math.Min(viewWidth/Width,viewHeight/Height);return HitTest((x-(viewWidth-Width*scale)/2)/scale,(y-(viewHeight-Height*scale)/2)/scale);
 }
}
