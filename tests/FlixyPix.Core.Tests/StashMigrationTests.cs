using System.Security.Cryptography;
using FlixyPix.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FlixyPix.Core.Tests;

public sealed class StashMigrationTests
{
 [Theory]
 [InlineData(22,false)][InlineData(22,true)][InlineData(23,false)][InlineData(24,false)][InlineData(25,true)]
 public void OlderSchemasPreserveTheirExistingDataAndCreateSafetyBackup(int schema,bool encrypted)
 {
  var root=StashTestWorkspace.Create("flixypix-stash-migration-");Directory.CreateDirectory(root);
  var path=Path.Combine(root,"catalog.db");CatalogKey? key=null;
  try
  {
   using(var empty=new CatalogRepository(path)){}
   if(encrypted){CatalogEncryption.EnableEncryption(path,"synthetic-migration-password");key=CatalogEncryption.Unlock(path,"synthetic-migration-password");}
   Guid mediaId,setId;StashSourceProfile? source=null;StashIdentity? identity=null;StashProvenance? provenance=null;
   using(var db=new CatalogRepository(path,key))
   {
    mediaId=db.Upsert(new(){Path=Path.Combine(root,"video.mp4"),Kind=MediaKind.Video,Title="Preserve",Playback=new(){Speed=1.5},Tags=["Parent","Child"]}).Id;
    var image=db.Upsert(new(){Path=Path.Combine(root,"photo.jpg"),Kind=MediaKind.Image});setId=db.UpsertImageSet(new(){Name="Set",MediaIds=[image.Id],CoverId=image.Id}).Id;db.AddTagParent(new("","Child"),new("","Parent"));
    if(schema>=23)source=db.SaveStashSource(new(){Name="Source",SourceDirectory=Path.Combine(root,"export"),PathMappings=[new("/media",root)]});
    if(schema>=24)identity=Assert.Single(db.StoreStashIdentities(source!.Id,source.Revision,[new(new(){SourceId=source.Id,Kind=StashEntityKind.Scene,Keys=[new(StashIdentityKeyKind.ExportPath,"scenes/a.json")]},null)]));
    if(schema>=25)
    {
     byte[] bytes=[123,125];var raw=new StashSourceDocument(1,StashEntityKind.Scene,"scenes/a.json",Convert.ToHexStringLower(SHA256.HashData(bytes)),null,bytes,null,[]);
     provenance=Assert.Single(db.CaptureStashProvenance(source!.Id,source.Revision,[new(identity!.Id,identity.Revision,raw)]));
    }
   }
   using(var raw=new SqliteConnection(CatalogEncryption.ConnectionString(path,key).ToString()))
   {
    raw.Open();using var command=raw.CreateCommand();
    command.CommandText="DROP TRIGGER stash_detach_media; DROP TRIGGER stash_detach_image_sets; DROP TRIGGER stash_detach_publication_groups; DROP TABLE stash_bindings;";
    if(schema<25)command.CommandText+="DROP TABLE stash_provenance_chunks; DROP TABLE stash_provenance;";
    if(schema<24)command.CommandText+="DROP TABLE stash_identity_keys; DROP TABLE stash_identities;";
    if(schema<23)command.CommandText+="DROP TABLE stash_sources;";
    command.CommandText+=$"PRAGMA user_version={schema}";command.ExecuteNonQuery();
   }
   using var migrated=new CatalogRepository(path,key);Assert.True(migrated.UpgradedOnOpen);Assert.True(File.Exists(migrated.MigrationBackupPath));Assert.Equal(1.5,migrated.GetMedia(mediaId)!.Playback.Speed);Assert.Equal("Preserve",migrated.GetMedia(mediaId)!.Title);Assert.NotNull(migrated.GetImageSet(setId)!.CoverId);Assert.Single(migrated.GetTagHierarchy());
   if(source!=null)Assert.Equal(source.PathMappings,migrated.GetStashSource(source.Id)!.PathMappings);else Assert.Empty(migrated.GetStashSources());
   if(identity!=null)Assert.Equal(identity.Keys,migrated.GetStashIdentity(identity.Id)!.Keys);
   if(provenance!=null)Assert.Equal(new byte[]{123,125},migrated.ReadStashProvenanceBytes(provenance.Id));
  }
  finally{key?.Dispose();SqliteConnection.ClearAllPools();if(Environment.GetEnvironmentVariable("FLIXYPIX_TEST_CAPTURE")!="1")Directory.Delete(root,true);}
 }
}
