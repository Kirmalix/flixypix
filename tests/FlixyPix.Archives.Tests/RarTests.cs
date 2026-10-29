using System.Security.Cryptography;
using FlixyPix.Archives;
using Xunit;

namespace FlixyPix.Archives.Tests;

public sealed class RarTests : IDisposable
{
 private readonly string root = Path.Combine(Path.GetTempPath(),"flixypix-rar-" + Guid.NewGuid().ToString("N"));
 public RarTests() => Directory.CreateDirectory(root);
 private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory,"Fixtures",name);

 [Theory]
 [InlineData("Rar4.rar")]
 [InlineData("Rar.solid.rar")]
 [InlineData("Rar5.rar")]
 [InlineData("Rar5.solid.rar")]
 public async Task Rar4And5IncludingSolidExtractImagesWithoutChangingArchive(string fixture)
 {
  var source = Fixture(fixture); var before = SHA256.HashData(File.ReadAllBytes(source));
  var service = new ImageArchiveService(Path.Combine(root,"cache")); var image = Assert.Single(service.Inspect(source));
  Assert.Equal("jpg/test.jpg",image.EntryName.Replace('\\','/')); Assert.Equal(40372,image.UncompressedSize);
  var result = await service.ExtractAsync(image); var bytes = File.ReadAllBytes(result);
  Assert.Equal(new byte[] {255,216,255},bytes[..3]);
  Assert.Equal(40372,bytes.Length);
  Assert.Equal("b251c7501fb0f55dd4a92feabe0a6f5733bc40a02679498155fae9b30138fc53",Convert.ToHexStringLower(SHA256.HashData(bytes)));
  var time = File.GetLastWriteTimeUtc(result); Assert.Equal(result,await service.ExtractAsync(image));
  Assert.Equal(time,File.GetLastWriteTimeUtc(result)); Assert.Equal(before,SHA256.HashData(File.ReadAllBytes(source)));
  File.WriteAllText(result,"tampered"); await Assert.ThrowsAsync<InvalidDataException>(() => service.ExtractAsync(image));
 }

 [Theory]
 [InlineData("Rar5.encrypted_filesOnly.rar")]
 [InlineData("Rar5.multi.part01.rar")]
 public void EncryptedAndMultipartRarAreRejectedBeforeCacheCreation(string fixture)
 {
  var service = new ImageArchiveService(Path.Combine(root,"cache"));
  Assert.Throws<InvalidDataException>(() => service.Inspect(Fixture(fixture)));
  Assert.False(Directory.Exists(Path.Combine(root,"cache")));
 }

 public void Dispose() => Directory.Delete(root,true);
}
