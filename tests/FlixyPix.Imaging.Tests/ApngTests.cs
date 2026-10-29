using System.Buffers.Binary;
using FlixyPix.Imaging;
using ImageMagick;
using Xunit;

namespace FlixyPix.Imaging.Tests;

public sealed class ApngTests : IDisposable
{
 private readonly string root=Path.Combine(Path.GetTempPath(),"flixypix-apng-"+Guid.NewGuid().ToString("N"));
 public ApngTests()=>Directory.CreateDirectory(root);
 public void Dispose()=>Directory.Delete(root,true);
 private string Make(byte dispose=0,byte blend=0,MagickColor? overlay=null)
 {
  var path=Path.Combine(root,Guid.NewGuid()+".apng"); using var output=File.Create(path);
  var background=Png(MagickColors.Red,4,4); output.Write(background.AsSpan(0,8));
  ApngDocument.WriteChunk(output,"IHDR",Chunk(background,"IHDR"));
  var actl=new byte[8]; BinaryPrimitives.WriteInt32BigEndian(actl,3); BinaryPrimitives.WriteInt32BigEndian(actl.AsSpan(4),2); ApngDocument.WriteChunk(output,"acTL",actl);
  Frame(0,4,4,0,0,15,0,0,Chunk(background,"IDAT"),false);
  Frame(1,2,2,1,1,25,dispose,blend,Chunk(Png(overlay??MagickColors.Blue,2,2),"IDAT"),true);
  Frame(3,1,1,0,0,10,0,0,Chunk(Png(MagickColors.Green,1,1),"IDAT"),true);
  ApngDocument.WriteChunk(output,"IEND",[]); return path;
  void Frame(int sequence,int w,int h,int x,int y,ushort delay,byte disposal,byte composition,byte[] compressed,bool fdat)
  {
   var control=new byte[26]; var values=new[]{sequence,w,h,x,y}; for(var i=0;i<5;i++)BinaryPrimitives.WriteInt32BigEndian(control.AsSpan(i*4),values[i]);
   BinaryPrimitives.WriteUInt16BigEndian(control.AsSpan(20),delay); BinaryPrimitives.WriteUInt16BigEndian(control.AsSpan(22),100); control[24]=disposal; control[25]=composition;
   ApngDocument.WriteChunk(output,"fcTL",control);
   if(!fdat)ApngDocument.WriteChunk(output,"IDAT",compressed);
   else { var data=new byte[compressed.Length+4]; BinaryPrimitives.WriteInt32BigEndian(data,sequence+1); compressed.CopyTo(data,4); ApngDocument.WriteChunk(output,"fdAT",data); }
  }
 }
 private static byte[] Png(MagickColor color,uint width,uint height)
 { using var image=new MagickImage(color,width,height); image.Settings.SetDefine(MagickFormat.Png,"color-type","6"); return image.ToByteArray(MagickFormat.Png); }
 private static byte[] Chunk(byte[] png,string name)
 {
  for(var p=8;p<png.Length;)
  { var length=BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(p)); var type=System.Text.Encoding.ASCII.GetString(png,p+4,4); if(type==name)return png.AsSpan(p+8,length).ToArray(); p+=length+12; }
  throw new InvalidOperationException("Missing chunk "+name);
 }
 [Theory]
 [InlineData((byte)0)] [InlineData((byte)1)] [InlineData((byte)2)]
 public async Task AnimatedPngDecodesTimingOffsetsAndDisposalWithoutExternalTools(byte disposal)
 {
  var path=Make(disposal); var source=File.ReadAllBytes(path); var service=new ImageService(Path.Combine(root,"cache"));
  var info=await service.GetInfoAsync(path); Assert.True(info.IsAnimated); Assert.Equal("APNG",info.Format); Assert.Equal(3,info.FrameCount); Assert.Equal(250,info.FrameDurations[1].TotalMilliseconds); Assert.Equal(2u,info.AnimationIterations);
  using var second=new MagickImage((await service.DecodeAsync(path,1)).PngBytes); using var third=new MagickImage((await service.DecodeAsync(path,2)).PngBytes);
  Assert.Equal(MagickColors.Blue,second.GetPixels().GetPixel(1,1).ToColor()); Assert.Equal(MagickColors.Red,second.GetPixels().GetPixel(0,0).ToColor());
  var expected=disposal==1?MagickColors.Transparent:disposal==2?MagickColors.Red:MagickColors.Blue;
  Assert.Equal(expected,third.GetPixels().GetPixel(1,1).ToColor()); Assert.Equal(source,File.ReadAllBytes(path));
 }
 [Fact]
 public async Task CorruptApngChunksAreRejected()
 {
  var path=Make(); var bytes=File.ReadAllBytes(path); bytes[^1]^=1; File.WriteAllBytes(path,bytes);
  await Assert.ThrowsAsync<InvalidDataException>(()=>new ImageService(Path.Combine(root,"cache")).DecodeAsync(path));
 }
 [Theory]
 [InlineData((byte)0)] [InlineData((byte)1)]
 public async Task SourceAndOverBlendingTreatAlphaDifferently(byte blend)
 {
  var path=Make(blend:blend,overlay:new MagickColor("#0000ff80"));
  using var decoded=new MagickImage((await new ImageService(Path.Combine(root,"cache")).DecodeAsync(path,1)).PngBytes);
  var color=decoded.GetPixels().GetPixel(1,1).ToColor()!;
  if(blend==0) { Assert.Equal((ushort)0,color.R); Assert.InRange(color.A,(ushort)32000,(ushort)34000); }
  else { Assert.InRange(color.R,(ushort)32000,(ushort)34000); Assert.Equal(ushort.MaxValue,color.A); }
 }
}
