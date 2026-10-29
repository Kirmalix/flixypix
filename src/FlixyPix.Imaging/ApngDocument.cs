using System.Buffers.Binary;
using System.Text;
using ImageMagick;

namespace FlixyPix.Imaging;

/// <summary>APNG framing, independent of ImageMagick's external video delegate.</summary>
internal sealed class ApngDocument
{
 internal sealed record Frame(int Width,int Height,int X,int Y,TimeSpan Duration,byte Dispose,byte Blend,List<byte[]> Data);
 internal int Width { get; private init; }
 internal int Height { get; private init; }
 internal uint AnimationIterations { get; private set; }
 internal List<Frame> Frames { get; }=[];
 private byte[] header=[];
 private readonly List<(string Type,byte[] Data)> prefix=[];
 private static readonly byte[] Signature=[137,80,78,71,13,10,26,10];
 internal static ApngDocument? TryRead(Stream stream,CancellationToken token=default)
 {
  stream.Position=0; Span<byte> signature=stackalloc byte[8];
  if(stream.Read(signature)!=8 || !signature.SequenceEqual(Signature)) { stream.Position=0; return null; }
  ApngDocument? document=null; Frame? current=null; uint? declared=null; long compressedBytes=0; uint sequence=0;
  var ended=false; var sawData=false; var chunks=0; long prefixBytes=0;
  Span<byte> chunkHeader=stackalloc byte[8]; Span<byte> crc=stackalloc byte[4];
  while(stream.Position<stream.Length)
  {
   token.ThrowIfCancellationRequested(); if(++chunks>100000 || stream.Position>512L*1024*1024)throw new InvalidDataException("Die PNG-Datenstruktur überschreitet das Lesebudget.");
   stream.ReadExactly(chunkHeader);
   var length=BinaryPrimitives.ReadUInt32BigEndian(chunkHeader); var type=Encoding.ASCII.GetString(chunkHeader[4..]);
   if(length>64*1024*1024 || length+4L>stream.Length-stream.Position) throw new InvalidDataException("Ungültige oder übergroße PNG-Datenstruktur.");
   // A normal PNG needs no APNG parsing, and can contain very large IDAT chunks.
   if(type=="IDAT" && declared==null) { stream.Position=0; return null; }
   var data=new byte[(int)length]; stream.ReadExactly(data); stream.ReadExactly(crc);
   if(Crc(chunkHeader[4..],data)!=BinaryPrimitives.ReadUInt32BigEndian(crc)) throw new InvalidDataException("Die PNG-Prüfsumme stimmt nicht.");
   if(document==null && type!="IHDR") throw new InvalidDataException("PNG-Bildkopf fehlt.");
   switch(type)
   {
    case "IHDR":
     if(document!=null || data.Length!=13) throw new InvalidDataException("Ungültiger PNG-Bildkopf.");
     var width=ReadInt(data,0); var height=ReadInt(data,4);
     if(width<=0 || height<=0 || (long)width*height>100_000_000) throw new InvalidDataException("Das Bild überschreitet die Grenze von 100 Megapixeln.");
     document=new() { Width=width,Height=height,header=data }; break;
    case "acTL":
     if(declared!=null || sawData || data.Length!=8) throw new InvalidDataException("Ungültiger APNG-Animationskopf.");
     declared=BinaryPrimitives.ReadUInt32BigEndian(data);
     document!.AnimationIterations=BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(4));
     if(declared==0 || declared>4096 || (long)declared*document!.Width*document.Height>32_000_000) throw new InvalidDataException("Die Animation überschreitet 4096 Frames oder das Gesamtbudget von 32 Megapixeln.");
     break;
    case "fcTL":
     if(declared==null || data.Length!=26 || BinaryPrimitives.ReadUInt32BigEndian(data)!=sequence++) throw new InvalidDataException("Ungültige APNG-Framefolge.");
     if(document!.Frames.Count>=declared)throw new InvalidDataException("Die APNG-Datei enthält mehr Frames als angegeben.");
     var fw=ReadInt(data,4); var fh=ReadInt(data,8); var x=ReadInt(data,12); var y=ReadInt(data,16);
     if(fw<=0 || fh<=0 || x<0 || y<0 || (long)x+fw>document!.Width || (long)y+fh>document.Height || data[24]>2 || data[25]>1) throw new InvalidDataException("Ungültige APNG-Framegrenzen.");
     var numerator=BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(20)); var denominator=BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(22));
     current=new(fw,fh,x,y,TimeSpan.FromSeconds(Math.Max(.01,numerator/(double)(denominator==0?100:denominator))),data[24],data[25],[]);
     document.Frames.Add(current); break;
    case "IDAT":
     sawData=true;
     if(current!=null) { compressedBytes+=data.Length; current.Data.Add(data); }
     break;
    case "fdAT":
     if(current==null || data.Length<4 || BinaryPrimitives.ReadUInt32BigEndian(data)!=sequence++) throw new InvalidDataException("Ungültige APNG-Framedaten.");
     compressedBytes+=data.Length-4; current.Data.Add(data[4..]); break;
    case "IEND": ended=true; break;
    default:
     // Preserve only PNG decoding/color chunks, never filenames or external delegates.
     if(!sawData && type is "PLTE" or "tRNS" or "gAMA" or "cHRM" or "iCCP" or "sRGB" or "sBIT")
     { prefixBytes+=data.Length; if(prefixBytes>16L*1024*1024)throw new InvalidDataException("PNG-Farbprofile überschreiten 16 MiB."); document!.prefix.Add((type,data)); }
     break;
   }
   if(compressedBytes>128L*1024*1024) throw new InvalidDataException("Die komprimierte Animation überschreitet 128 MiB.");
   if(ended)break;
  }
  stream.Position=0;
  if(declared==null)return null;
  if(!ended || document!.Frames.Count!=declared || document.Frames.Any(f=>f.Data.Count==0)) throw new InvalidDataException("Die APNG-Datei ist unvollständig.");
  return document;
 }
 internal MagickImage Decode(int index,CancellationToken token)
 {
  var canvas=new MagickImage(MagickColors.Transparent,(uint)Width,(uint)Height);
  try
  {
   for(var i=0;i<=index;i++)
   {
    token.ThrowIfCancellationRequested(); var frame=Frames[i];
    using var previous=frame.Dispose==2?canvas.Clone():null;
    using var image=new MagickImage(EncodeFrame(frame));
    // Normalize each frame before compositing, so the common canvas is sRGB.
    if(image.GetColorProfile()!=null) image.TransformColorSpace(ColorProfiles.SRGB); else image.ColorSpace=ColorSpace.sRGB;
    canvas.Composite(image,frame.X,frame.Y,frame.Blend==0?CompositeOperator.Copy:CompositeOperator.Over);
    if(i==index)return canvas;
    if(frame.Dispose==1)
    { using var transparent=new MagickImage(MagickColors.Transparent,(uint)frame.Width,(uint)frame.Height); canvas.Composite(transparent,frame.X,frame.Y,CompositeOperator.Copy); }
    else if(frame.Dispose==2 && previous!=null) { canvas.Dispose(); canvas=(MagickImage)previous.Clone(); }
   }
   throw new ArgumentOutOfRangeException(nameof(index));
  }
  catch { canvas.Dispose(); throw; }
 }
 private byte[] EncodeFrame(Frame frame)
 {
  using var output=new MemoryStream(); output.Write(Signature); var ihdr=(byte[])header.Clone(); BinaryPrimitives.WriteInt32BigEndian(ihdr,frame.Width); BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4),frame.Height);
  WriteChunk(output,"IHDR",ihdr); foreach(var chunk in prefix)WriteChunk(output,chunk.Type,chunk.Data); foreach(var data in frame.Data)WriteChunk(output,"IDAT",data); WriteChunk(output,"IEND",[]); return output.ToArray();
 }
 private static int ReadInt(byte[] data,int offset)
 { var value=BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset)); return value>int.MaxValue?throw new InvalidDataException("PNG-Wert überschreitet den gültigen Bereich."):(int)value; }
 internal static void WriteChunk(Stream output,string type,byte[] data)
 {
  var name=Encoding.ASCII.GetBytes(type); Span<byte> word=stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(word,data.Length); output.Write(word); output.Write(name); output.Write(data); BinaryPrimitives.WriteUInt32BigEndian(word,Crc(name,data)); output.Write(word);
 }
 private static uint Crc(ReadOnlySpan<byte> type,ReadOnlySpan<byte> data)
 {
  uint crc=uint.MaxValue;
  foreach(var b in type)Update(b); foreach(var b in data)Update(b); return ~crc;
  void Update(byte b) { crc^=b; for(var bit=0;bit<8;bit++)crc=(crc&1)==0?crc>>1:(crc>>1)^0xedb88320; }
 }
}
