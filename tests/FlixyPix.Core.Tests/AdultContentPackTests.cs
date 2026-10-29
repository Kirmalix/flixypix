using System.IO.Compression;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Resources;
using System.Text;
using FlixyPix.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FlixyPix.Core.Tests;

public sealed class AdultContentPackTests : IDisposable
{
 // Synthetic stand-in for the original's preset list; FlixyPix does not ship the real names.
 private const string AdultText = "Category=[Genres (adult)]\r\n18+ Teen\r\nAmateur\r\nAnal\r\n\r\nCategory=[Performers (adult)]\r\nA. K. Honey\r\nAaliya Brown\r\nBaby Doll\r\n\r\nCategory=[Studios (adult)]\r\n18 Carat\r\n818 XXX\r\nBaby Doll\r\n";
 private const int Genres = 62, Names = 6;
 private readonly string directory = Path.Combine(Path.GetTempPath(), "flixypix-pack-" + Guid.NewGuid().ToString("N"));
 private static string Root => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
 private readonly CatalogRepository catalog;
 private readonly string program;
 public AdultContentPackTests() { Directory.CreateDirectory(directory); catalog = new(Path.Combine(directory,"catalog.db")); program = CreateProgram(directory, AdultText); }
 private ContentPackPreview Preview(string language = "de") => catalog.PreviewAdultContentPack(Path.Combine(Root,"assets"),program,language,true,true,true);
 private ContentPackResult Apply(ContentPackPreview p) => catalog.ApplyAdultContentPack(p,p.Tags.Items.Select(i=>i.Row.Line).ToArray());

 /// <summary>A minimal .NET program file whose embedded resources mirror the original's layout.</summary>
 internal static string CreateProgram(string folder, string adultText)
 {
  var zipBytes = new MemoryStream();
  using (var zip = new ZipArchive(zipBytes, ZipArchiveMode.Create, true))
  using (var writer = new StreamWriter(zip.CreateEntry("Presets/" + OriginalPresets.AdultList).Open(), new UTF8Encoding(true))) writer.Write(adultText);
  var resources = new MemoryStream();
  using (var writer = new ResourceWriter(resources)) { writer.AddResource("other", "text"); writer.AddResource(OriginalPresets.PresetsKey, zipBytes.ToArray()); writer.Generate(); }
  var metadata = new MetadataBuilder();
  metadata.AddModule(0, metadata.GetOrAddString("fixture.exe"), metadata.GetOrAddGuid(Guid.NewGuid()), default, default);
  metadata.AddAssembly(metadata.GetOrAddString("fixture"), new Version(1, 0), default, default, 0, AssemblyHashAlgorithm.None);
  metadata.AddTypeDefinition(default, default, metadata.GetOrAddString("<Module>"), default, MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
  var managed = new BlobBuilder();
  foreach (var (name, bytes) in new[] { ("Other.resources", new byte[] { 1, 2, 3 }), (OriginalPresets.ResourceFile, resources.ToArray()) })
  {
   metadata.AddManifestResource(ManifestResourceAttributes.Public, metadata.GetOrAddString(name), default, (uint)managed.Count);
   managed.WriteInt32(bytes.Length); managed.WriteBytes(bytes); managed.Align(8);
  }
  var image = new BlobBuilder();
  new ManagedPEBuilder(PEHeaderBuilder.CreateExecutableHeader(), new MetadataRootBuilder(metadata), new BlobBuilder(), managedResources: managed).Serialize(image);
  var path = Path.Combine(folder, LegacyInstallations.ProgramFile);
  File.WriteAllBytes(path, image.ToArray());
  return path;
 }

 [Fact] public void ReadsNameListsFromTheProgramFileAndFindsIt()
 {
  var lists = OriginalPresets.ReadAdult(program);
  Assert.Equal(new[] { "18+ Teen", "Amateur", "Anal" }, lists[OriginalPresets.Genres]);
  Assert.Equal("Baby Doll", lists[OriginalPresets.Studios][^1]);
  Assert.Equal(program, OriginalPresets.FindProgram(directory));
  Assert.Null(OriginalPresets.FindProgram(Path.Combine(directory, "missing"), new() { ProgramFolders = [] }));
  var other = Path.Combine(directory, "other"); Directory.CreateDirectory(other);
  File.WriteAllBytes(Path.Combine(other, LegacyInstallations.ProgramFile), [0x4D, 0x5A, 0, 0]);
  Assert.Throws<InvalidDataException>(() => OriginalPresets.ReadAdult(Path.Combine(other, LegacyInstallations.ProgramFile)));
 }

 [Fact] public void TranslatedGenresNeedNoInstallationButNamesDo()
 {
  var genres = catalog.PreviewAdultContentPack(Path.Combine(Root,"assets"),null,"de",true,false,false);
  Assert.Equal(Genres, genres.Tags.Items.Count);
  Assert.Throws<FileNotFoundException>(() => catalog.PreviewAdultContentPack(Path.Combine(Root,"assets"),null,"de",false,true,false));
  Assert.Throws<FileNotFoundException>(() => catalog.PreviewAdultContentPack(Path.Combine(Root,"assets"),null,"original",true,false,false));
 }

 [Fact] public void PartialSelectionPreservesUserMetadataAndUndoRestoresIt()
 {
  catalog.UpsertVocabularyTag(new("Amateur","Genres (adult)",true,Description:"user description"));
  var bytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aF6sAAAAASUVORK5CYII=");
  var picture = catalog.UpsertTagAsset(new() { Tag = new("Genres (adult)", "Amateur"), Data = bytes });
  var p=catalog.PreviewAdultContentPack(Path.Combine(Root,"assets"),program,"de",true,false,false);
  var selected=p.Tags.Items.Where(i=>i.Row.Name=="Amateur"||i.Row.Line==1).Select(i=>i.Row.Line).ToArray();
  var result=catalog.ApplyAdultContentPack(p,selected);
  Assert.Equal(2,catalog.GetPreferences().TagVocabulary.Count);
  Assert.True(catalog.GetPreferences().TagVocabulary.Single(e=>e.Tag=="Amateur").IsFavorite);
  Assert.Equal(bytes, catalog.GetTagAsset(picture.Id)!.Data);
  Assert.True(catalog.UndoContentPack(result.Undo!));
  Assert.Equal("user description",Assert.Single(catalog.GetPreferences().TagVocabulary).Description);
  Assert.Equal(picture.Id, Assert.Single(catalog.GetTagAssetSummaries()).Id);
  Assert.Equal(bytes, catalog.GetTagAsset(picture.Id)!.Data);
 }

 [Fact] public void ChangedFilesAndOtherWritersInvalidateThePreview()
 {
  var assets=Path.Combine(directory,"assets");Directory.CreateDirectory(Path.Combine(assets,"tag-vocabularies"));
  var path=Path.Combine(assets,"tag-vocabularies/en.flixytags.csv");
  File.Copy(Path.Combine(Root,"assets/tag-vocabularies/en.flixytags.csv"),path);
  var p=catalog.PreviewAdultContentPack(assets,program,"en",true,false,false);
  File.AppendAllText(path,"\n# changed\n");Assert.Throws<InvalidDataException>(()=>Apply(p));
  var names=Preview(); File.AppendAllText(program,"changed");Assert.Throws<InvalidDataException>(()=>Apply(names));
  CreateProgram(directory, AdultText);
  var current=Preview();
  using(var other=new CatalogRepository(Path.Combine(directory,"catalog.db"))) other.Upsert(new(){Path=Path.Combine(directory,"other.mp4"),Kind=MediaKind.Video});
  Assert.Throws<InvalidOperationException>(()=>Apply(current));Assert.Empty(catalog.GetPreferences().TagVocabulary);
 }

 [Fact] public void ExistingStudioRoleChangesOnlyAfterExplicitChoice()
 {
  catalog.UpsertVocabularyTag(new("18 Carat", "Studios (adult)"));
  var blocked=catalog.PreviewAdultContentPack(Path.Combine(Root,"assets"),program,"en",false,false,true);
  Assert.All(blocked.Tags.Items,i=>Assert.Equal(TagPackageStatus.Conflict,i.Status));
  var approved=catalog.PreviewAdultContentPack(Path.Combine(Root,"assets"),program,"en",false,false,true,changeRoles:true);
  Assert.All(approved.Tags.Items,i=>Assert.True(i.CanApply,i.Note));
  var result=Apply(approved); Assert.Equal(TagRole.Studio,catalog.GetTagRole("Studios (adult)"));
  Assert.True(catalog.UndoContentPack(result.Undo!)); Assert.Equal(TagRole.Tag,catalog.GetTagRole("Studios (adult)"));
 }

 [Fact] public void OriginalNamesAndSeparateRoleImportsRemainDistinct()
 {
  var first=catalog.PreviewAdultContentPack(Path.Combine(Root,"assets"),program,"original",true,true,false);
  Assert.Contains(first.Tags.Items,i=>i.Row.Name=="18+ Teen");
  Apply(first);
  var studios=catalog.PreviewAdultContentPack(Path.Combine(Root,"assets"),program,"original",false,false,true);
  Assert.All(studios.Tags.Items,i=>Assert.True(i.CanApply,i.Note)); Apply(studios);
  Assert.Equal(2,catalog.GetDirectory().Count(e=>e.Tag=="Baby Doll"));
 }

 [Fact] public void CompletePackRepeatsWithoutDuplicatesAndUndoRestoresOriginal()
 {
  var media = catalog.Upsert(new() { Path = Path.Combine(directory,"fixture.mp4"), Kind=MediaKind.Video, Tags=["user"] });
  var p=Preview(); Assert.Equal(Genres+Names,p.Tags.Items.Count); Assert.All(p.Tags.Items,i=>Assert.True(i.CanApply,i.Row.Name+": "+i.Note));
  var imported=Apply(p); Assert.NotNull(imported.Undo); Assert.Equal(Genres+Names,catalog.GetPreferences().TagVocabulary.Count);
  Assert.Empty(catalog.GetTagAssetSummaries());
  Assert.Equal(2,catalog.GetDirectory().Count(e=>e.Tag=="Baby Doll"));
  Assert.Equal(TagRole.Studio,catalog.GetTagRole("Studios (adult)"));
  Assert.Equal(new[]{"user"},catalog.Search().Single(i=>i.Id==media.Id).Tags);
  Assert.True(catalog.UndoContentPack(imported.Undo!)); Assert.Empty(catalog.GetPreferences().TagVocabulary);
  Apply(Preview()); var again=Apply(Preview()); Assert.Null(again.Undo); Assert.Equal(Genres+Names,catalog.GetPreferences().TagVocabulary.Count);
 }
 [Fact] public void TranslationAliasesAndSearchDoNotDuplicateConcepts()
 {
  Apply(Preview()); var other=Preview("fr"); Assert.All(other.Tags.Items,i=>Assert.True(i.CanApply,i.Row.Name+": "+i.Note));
  Apply(other); Assert.Equal(Genres+Names,catalog.GetPreferences().TagVocabulary.Count);
  var entry=catalog.GetPreferences().TagVocabulary.Single(e=>(e.Aliases??[]).Contains("Blowjob"));
  var item=catalog.Upsert(new(){Path=Path.Combine(directory,"clip.mp4"),Kind=MediaKind.Video,Tags=[entry.Tag]});
  Assert.Equal(item.Id,Assert.Single(catalog.QueryPage(new(){Text="Blowjob"}).Items).Id);
 }
 [Fact] public void CancellationDoesNotCommitAndInterveningChangesProtectUndo()
 {
  var p=Preview(); using var cancellation=new CancellationTokenSource(); cancellation.Cancel();
  Assert.Throws<OperationCanceledException>(()=>catalog.ApplyAdultContentPack(p,p.Tags.Items.Select(i=>i.Row.Line).ToArray(),cancellation.Token));
  Assert.Empty(catalog.GetPreferences().TagVocabulary);
  var applied=Apply(Preview()); catalog.Upsert(new(){Path=Path.Combine(directory,"new.mp4"),Kind=MediaKind.Video});
  Assert.False(catalog.UndoContentPack(applied.Undo!)); Assert.Equal(Genres+Names,catalog.GetPreferences().TagVocabulary.Count);
 }
 public void Dispose() { catalog.Dispose(); SqliteConnection.ClearAllPools(); Directory.Delete(directory,true); }
}
