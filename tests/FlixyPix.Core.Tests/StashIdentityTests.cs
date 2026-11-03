using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FlixyPix.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FlixyPix.Core.Tests;

public sealed class StashIdentityTests:IDisposable
{
 private readonly string root=StashTestWorkspace.Create("flixypix-stash-identity-");
 private string Database=>Path.Combine(root,"catalog.db");
 private const string Secret="synthetic-stash-identity-password";
 public StashIdentityTests()=>Directory.CreateDirectory(root);
 public void Dispose(){SqliteConnection.ClearAllPools();if(Environment.GetEnvironmentVariable("FLIXYPIX_TEST_CAPTURE")!="1")Directory.Delete(root,true);}
 private CatalogKey? Prepare(bool encrypted)
 {using(var db=new CatalogRepository(Database)){}if(!encrypted)return null;CatalogEncryption.EnableEncryption(Database,Secret);return CatalogEncryption.Unlock(Database,Secret);}
 private StashSourceProfile Source(CatalogRepository db,string name="Source")=>db.SaveStashSource(new(){Name=name,SourceDirectory=Path.Combine(root,name)});
 private static StashSourceDocument Document(string json,string path="scenes/a.json",StashEntityKind kind=StashEntityKind.Scene)
 {
  using var parsed=JsonDocument.Parse(json);var validated=StashExport.ValidateDocument(kind,parsed.RootElement);var bytes=Encoding.UTF8.GetBytes(json);
  return new(1,validated.Kind,path,Convert.ToHexStringLower(SHA256.HashData(bytes)),validated.NaturalKey,bytes,parsed.RootElement.Clone(),validated.Issues);
 }
 private static StashIdentity Store(CatalogRepository db,StashSourceProfile source,StashSourceDocument document,Guid? id=null,long? expected=null)
 =>Assert.Single(db.StoreStashIdentities(source.Id,source.Revision,[new(new(){Id=id??Guid.NewGuid(),SourceId=source.Id,Kind=document.Kind,Keys=StashExport.IdentityKeys(document).ToList()},expected)]));

 [Theory][InlineData(false)]
 public void StableIdentitySurvivesExportFilenameTitleAndMetadataChanges(bool encrypted)
 {
  using var key=Prepare(encrypted);Guid identityId,sourceId;var original=Document("""{"title":"Before","files":["/media/clip.mp4"]}""");var changed=Document("""{"title":"After","files":["/media/clip.mp4"],"rating":90}""","scenes/renamed.json");
  using(var db=new CatalogRepository(Database,key))
  {
   var source=Source(db);sourceId=source.Id;Assert.Equal(StashIdentityState.New,db.ResolveStashIdentity(source.Id,original).State);Assert.Empty(db.GetStashIdentities(source.Id));
   var identity=Store(db,source,original);identityId=identity.Id;var match=db.ResolveStashIdentity(source.Id,changed);Assert.Equal(StashIdentityState.Matched,match.State);Assert.Equal(identity.Id,Assert.Single(match.Candidates).Id);Assert.Equal(1,match.SourceRevision);
   var updated=Store(db,source,changed,identity.Id,identity.Revision);Assert.Equal(2,updated.Revision);Assert.Contains(updated.Keys,key=>key.Kind==StashIdentityKeyKind.ExportPath&&key.Value=="scenes/a.json");Assert.Contains(updated.Keys,key=>key.Kind==StashIdentityKeyKind.ExportPath&&key.Value=="scenes/renamed.json");
   Assert.Equal(2,Store(db,source,changed,identity.Id,updated.Revision).Revision);Assert.Single(db.GetStashIdentities(source.Id));
  }
  using var reopened=new CatalogRepository(Database,key);Assert.Equal(identityId,Assert.Single(reopened.ResolveStashIdentity(sourceId,changed).Candidates).Id);
 }
 [Theory][InlineData("hash")][InlineData("filename")]
 public void HashOrFilenameOnlyEvidenceAlwaysNeedsConfirmation(string evidence)
 {
  using var db=new CatalogRepository(Database);var source=Source(db);var original=Document("""{"title":"Before"}""");var identity=Store(db,source,original);
  Assert.Equal(StashIdentityState.Matched,db.ResolveStashIdentity(source.Id,original).State);
  var changed=evidence=="hash"?original with{RelativePath="scenes/new.json"}:Document("""{"title":"Entirely different"}""");var proposal=db.ResolveStashIdentity(source.Id,changed);
  Assert.Equal(StashIdentityState.ConfirmationRequired,proposal.State);Assert.Equal(identity.Id,Assert.Single(proposal.Candidates).Id);
 }
 [Fact]
 public void SourceAndEntityTypeAreIndependentNamespacesAndPerformerDisambiguationIsPreserved()
 {
  using var db=new CatalogRepository(Database);var source=Source(db);var other=Source(db,"Other");var performer=Document("""{"name":"Same","disambiguation":"One"}""","performers/a.json",StashEntityKind.Performer);Store(db,source,performer);
  Assert.Equal(StashIdentityState.New,db.ResolveStashIdentity(other.Id,performer).State);Assert.Equal(StashIdentityState.New,db.ResolveStashIdentity(source.Id,Document("""{"name":"Same","disambiguation":"Two"}""","performers/b.json",StashEntityKind.Performer)).State);Assert.Equal(StashIdentityState.New,db.ResolveStashIdentity(source.Id,Document("""{"name":"Same"}""","studios/a.json",StashEntityKind.Studio)).State);
 }
 [Fact]
 public void ConflictingStrongAndHistoricalFilenameEvidenceNeverChoosesTheFirstIdentity()
 {
  using var db=new CatalogRepository(Database);var source=Source(db);var first=Store(db,source,Document("""{"files":["/media/a.mp4"]}""","scenes/a.json"));var second=Store(db,source,Document("""{"files":["/media/b.mp4"]}""","scenes/b.json"));
  var proposal=db.ResolveStashIdentity(source.Id,Document("""{"files":["/media/b.mp4"]}""","scenes/a.json"));Assert.Equal(StashIdentityState.Ambiguous,proposal.State);Assert.Equal(new[]{first.Id,second.Id}.Order(),proposal.Candidates.Select(candidate=>candidate.Id));
 }
 [Fact]
 public void WindowsMediaPathsCanonicalizeAndUnixPathsRemainCaseSensitive()
 {
  using var db=new CatalogRepository(Database);var source=Source(db);Store(db,source,Document("""{"files":["Z:\\MEDIA\\Clip.mp4"]}"""));
  Assert.Equal(StashIdentityState.Matched,db.ResolveStashIdentity(source.Id,Document("""{"files":["z:/media/clip.MP4"]}""","scenes/new.json")).State);
  Store(db,source,Document("""{"files":["/media/Clip.mp4"]}""","scenes/unix.json"));Assert.Equal(StashIdentityState.New,db.ResolveStashIdentity(source.Id,Document("""{"files":["/media/clip.mp4"]}""","scenes/newunix.json")).State);
 }
 [Theory][InlineData(false)]
 public void StaleIdentityOrSourceAndLateBatchFailureLeaveNoPartialRegistration(bool encrypted)
 {
  using var key=Prepare(encrypted);using var db=new CatalogRepository(Database,key);var source=Source(db);var document=Document("""{"files":["/media/a.mp4"]}""");var identity=Store(db,source,document);
  using var second=new CatalogRepository(Database,key);var updated=Store(second,source,document with{RelativePath="scenes/renamed.json"},identity.Id,identity.Revision);
  Assert.Throws<InvalidOperationException>(()=>Store(db,source,document,identity.Id,identity.Revision));Assert.Equal(2,db.GetStashIdentity(identity.Id)!.Revision);
  var newIdentity=new StashIdentity{SourceId=source.Id,Kind=document.Kind,Keys=StashExport.IdentityKeys(document with{RelativePath="scenes/new.json"}).ToList()};
  Assert.Throws<InvalidOperationException>(()=>db.StoreStashIdentities(source.Id,source.Revision,[new(newIdentity,null),new(updated,1)]));Assert.Null(db.GetStashIdentity(newIdentity.Id));
  var newProfile=second.SaveStashSource(source with{Name="Changed"},source.Revision);Assert.Throws<InvalidOperationException>(()=>db.StoreStashIdentities(source.Id,source.Revision,[new(newIdentity,null)]));Assert.Null(db.GetStashIdentity(newIdentity.Id));Assert.Equal(newProfile.Revision,db.GetStashSource(source.Id)!.Revision);
 }
 [Theory][InlineData("conflict")][InlineData("duplicate")][InlineData("missing-source")]
 public void InvalidPortableEvidenceRollsBackEarlierMediaWrites(string problem)
 {
  using var db=new CatalogRepository(Database);var source=Source(db);var identity=Store(db,source,Document("""{"files":["/media/a.mp4"]}"""));var media=db.Upsert(new(){Path=Path.Combine(root,"media.jpg"),Title="Keep"});
  var imported=problem=="conflict"?identity with{Keys=[new(StashIdentityKeyKind.Natural,"Conflict")]}:problem=="missing-source"?identity with{Id=Guid.NewGuid(),SourceId=Guid.NewGuid()}:identity;
  var path=Path.Combine(root,"bad.json");File.WriteAllText(path,JsonSerializer.Serialize(new{format="flixypix-catalog",version=5,items=new[]{media with{Title="Rollback"}},stashIdentities=problem=="duplicate"?new[]{imported,imported}:new[]{imported}},CatalogRepository.Json));
  Assert.NotEmpty(db.ImportJson(path).Errors);Assert.Equal("Keep",db.GetMedia(media.Id)!.Title);Assert.Equal(identity.Keys,db.GetStashIdentity(identity.Id)!.Keys);
 }
 [Fact]
 public void CancellationInvalidEvidenceAndInvalidSourceDocumentsDoNotCreateIdentities()
 {
  using var db=new CatalogRepository(Database);var source=Source(db);var document=Document("""{"files":["/media/a.mp4"]}""");var identity=new StashIdentity{SourceId=source.Id,Kind=document.Kind,Keys=StashExport.IdentityKeys(document).ToList()};using var cancellation=new CancellationTokenSource();cancellation.Cancel();
  Assert.Throws<OperationCanceledException>(()=>db.StoreStashIdentities(source.Id,source.Revision,[new(identity,null)],cancellation.Token));Assert.Throws<OperationCanceledException>(()=>db.ResolveStashIdentity(source.Id,document,cancellation.Token));Assert.Empty(db.GetStashIdentities(source.Id));
  Assert.Throws<ArgumentException>(()=>db.StoreStashIdentities(source.Id,source.Revision,[new(identity with{Keys=[new(StashIdentityKeyKind.ContentHash,"bad")]},null)]));Assert.Equal(StashIdentityState.Invalid,db.ResolveStashIdentity(source.Id,document with{Issues=[new("bad",StashIssueSeverity.Error,"bad")]}).State);
 }
 [Fact]
 public void ManySharedFilePathsKeepEvidenceBoundedWithoutLosingWeakFallbackInformation()
 {
  using var db=new CatalogRepository(Database);var source=Source(db);var document=Document(JsonSerializer.Serialize(new{files=Enumerable.Range(0,100).Select(i=>"/media/"+i+".mp4")}));Store(db,source,document);var proposal=db.ResolveStashIdentity(source.Id,document);Assert.Equal(StashIdentityState.Matched,proposal.State);var candidate=Assert.Single(proposal.Candidates);Assert.True(candidate.EvidenceTruncated);Assert.Equal(66,candidate.Evidence.Count);Assert.Contains(candidate.Evidence,key=>key.Kind==StashIdentityKeyKind.ContentHash);Assert.Contains(candidate.Evidence,key=>key.Kind==StashIdentityKeyKind.ExportPath);
 }
 [Fact]
 public void CandidateEnumerationIsBoundedAndTruncationCannotProduceAMatch()
 {
  using var db=new CatalogRepository(Database);var source=Source(db);var document=Document("""{"files":["/media/a.mp4"]}""");var changes=Enumerable.Range(0,260).Select(_=>new StashIdentityChange(new(){SourceId=source.Id,Kind=document.Kind,Keys=StashExport.IdentityKeys(document).ToList()},null)).ToArray();db.StoreStashIdentities(source.Id,source.Revision,changes);
  var proposal=db.ResolveStashIdentity(source.Id,document);Assert.Equal(StashIdentityState.Ambiguous,proposal.State);Assert.True(proposal.Truncated);Assert.Equal(256,proposal.Candidates.Count);Assert.Equal(100,db.GetStashIdentities(source.Id).Count);Assert.Equal(4,db.GetStashIdentities(source.Id,256).Count);Assert.Throws<ArgumentOutOfRangeException>(()=>db.GetStashIdentities(source.Id,0,257));
 }
}
