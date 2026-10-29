using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using FlixyPix.Archives;
using Xunit;

namespace FlixyPix.Archives.Tests;

public sealed class Zip64Tests : IDisposable
{
 private readonly string root = Path.Combine(Path.GetTempPath(),"flixypix-zip64-" + Guid.NewGuid().ToString("N"));
 public Zip64Tests() => Directory.CreateDirectory(root);

 [Theory]
 [InlineData(true)]
 [InlineData(false)]
 public async Task ReadsZip64EndRecordsAndIndividualEntrySizeExtensions(bool fullZip64)
 {
  var source = Create(fullZip64); var before = SHA256.HashData(File.ReadAllBytes(source));
  var service = new ImageArchiveService(Path.Combine(root,"cache")); var image = Assert.Single(service.Inspect(source));
  Assert.Equal("image.avif",image.EntryName); Assert.Equal(4,image.UncompressedSize);
  Assert.Equal(new byte[] {1,2,3,4},File.ReadAllBytes(await service.ExtractAsync(image)));
  Assert.Equal(before,SHA256.HashData(File.ReadAllBytes(source)));
 }

 [Theory]
 [InlineData("locator")]
 [InlineData("size")]
 [InlineData("offset")]
 [InlineData("multivolume")]
 public void InvalidZip64MetadataIsRejectedBeforeCreatingCache(string mode)
 {
  var source = Create(true); var bytes = File.ReadAllBytes(source);
  var eocd = bytes.Length - 22; var locator = eocd - 20;
  var zip64 = (int)BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(locator + 8));
  if (mode == "locator") bytes[locator] = 0;
  if (mode == "size") BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(zip64 + 40),ulong.MaxValue);
  if (mode == "offset") BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(locator + 8),ulong.MaxValue);
  if (mode == "multivolume") BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(zip64 + 16),1);
  File.WriteAllBytes(source,bytes); var service = new ImageArchiveService(Path.Combine(root,"cache"));
  Assert.Throws<InvalidDataException>(() => service.Inspect(source));
  Assert.False(Directory.Exists(Path.Combine(root,"cache")));
 }

 private string Create(bool fullZip64)
 {
  var path = Path.Combine(root,Guid.NewGuid().ToString("N") + ".zip");
  using var file = File.Create(path); using var writer = new BinaryWriter(file);
  var name = Encoding.UTF8.GetBytes("image.avif"); byte[] data = [1,2,3,4]; var crc = Crc(data);
  void U16(int value) => writer.Write((ushort)value);
  void U32(uint value) => writer.Write(value);
  void U64(long value) => writer.Write((ulong)value);
  U32(0x04034b50); U16(45); U16(0); U16(0); U16(0); U16(0); U32(crc);
  U32(uint.MaxValue); U32(uint.MaxValue); U16(name.Length); U16(20); writer.Write(name);
  U16(1); U16(16); U64(data.Length); U64(data.Length); writer.Write(data);
  var central = file.Position;
  U32(0x02014b50); U16(45); U16(45); U16(0); U16(0); U16(0); U16(0); U32(crc);
  U32(uint.MaxValue); U32(uint.MaxValue); U16(name.Length); U16(28); U16(0); U16(0); U16(0); U32(0); U32(uint.MaxValue);
  writer.Write(name); U16(1); U16(24); U64(data.Length); U64(data.Length); U64(0);
  var size = file.Position - central;
  if (fullZip64)
  {
   var zip64 = file.Position;
   U32(0x06064b50); U64(44); U16(45); U16(45); U32(0); U32(0); U64(1); U64(1); U64(size); U64(central);
   U32(0x07064b50); U32(0); U64(zip64); U32(1);
  }
  U32(0x06054b50); U16(0); U16(0); U16(fullZip64 ? ushort.MaxValue : 1); U16(fullZip64 ? ushort.MaxValue : 1);
  U32(fullZip64 ? uint.MaxValue : (uint)size); U32(fullZip64 ? uint.MaxValue : (uint)central); U16(0);
  return path;
 }
 private static uint Crc(byte[] data)
 {
  uint crc = uint.MaxValue;
  foreach (var value in data)
  {
   crc ^= value;
   for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0);
  }
  return ~crc;
 }
 public void Dispose() => Directory.Delete(root,true);
}
