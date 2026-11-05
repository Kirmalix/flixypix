using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using FlixyPix.Core;
using Xunit;

namespace FlixyPix.Desktop.Tests;

public sealed class StorageTransferServiceTests : IDisposable
{
 private readonly string root=Path.Combine(Path.GetTempPath(),"flixypix-transfer-service-"+Guid.NewGuid().ToString("N"));
 [Theory]
 [InlineData(false)]
 [InlineData(true)]
 public async Task CopyPreservesDestinationAndSourceAndCancellationDoesNotPublish(bool encrypted)
 {
  var sourceRoot=Path.Combine(root,"source");var library=Path.Combine(sourceRoot,"Library");
  Directory.CreateDirectory(library);
  var database=Path.Combine(library,"catalog.db");
  using(var catalog=new CatalogRepository(database))catalog.Upsert(new() {Path=Path.Combine(library,"video.mp4"),Title="Synthetic"});
  const string password="synthetic-service-test-password";
  if(encrypted)CatalogEncryption.EnableEncryption(database,password);
  var beforeSource=SHA256.HashData(File.ReadAllBytes(database));
  var app=Path.Combine(root,"app");Directory.CreateDirectory(app);
  var target=new AppStoragePaths(StorageMode.Profile,app,Path.Combine(root,"target"));
  var existing=new DesktopSettings {DataDirectory=Path.Combine(target.Root,"existing"),Language="fr",Libraries=[new(Path.Combine(target.Root,"existing"))]};
  existing.SaveTo(target.Settings);
  var source=new DesktopSettings {DataDirectory=library,Libraries=[new(library)]};
  var request=new StorageTransferRequest(source,sourceRoot,target,true,false,[library]);
  CatalogRepository Open(string path)
  {
   if(!encrypted)return new(path);
   using var key=CatalogEncryption.Unlock(path,password);return new(path,key);
  }
  using var session=new StorageRootSession(sourceRoot);
  var service=new StorageTransferService(Open,session.Reserve);
  await service.ApplyAsync(request,CancellationToken.None);
  var settings=JsonSerializer.Deserialize<DesktopSettings>(File.ReadAllBytes(target.Settings))!;
  Assert.Equal("fr",settings.Language);Assert.Equal(2,settings.Libraries.Count);
  using(var copied=Open(Path.Combine(settings.DataDirectory,"catalog.db")))Assert.Single(copied.QueryPage(new()).Items);
  Assert.Equal(beforeSource,SHA256.HashData(File.ReadAllBytes(database)));
  var beforeTarget=File.ReadAllBytes(target.Settings);
  await service.ApplyAsync(request with {Copy=false},CancellationToken.None);
  Assert.Equal(beforeTarget,File.ReadAllBytes(target.Settings));
  using var cancellation=new CancellationTokenSource();
  var cancelling=new StorageTransferService(Open,session.Reserve,_=>cancellation.Cancel());
  await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>cancelling.ApplyAsync(request,cancellation.Token));
  Assert.Equal(beforeTarget,File.ReadAllBytes(target.Settings));
  Assert.False(Directory.Exists(Path.Combine(app,StorageSwitchRecovery.JournalName)));
 }
 public void Dispose() {if(Directory.Exists(root))Directory.Delete(root,true);}
}
