using System.IO.Compression;
using System.Text.Json;
using FlixyPix.Core;
using Xunit;

namespace FlixyPix.Core.Tests;
public sealed class BrandCompatibilityTests:IDisposable
{
 private readonly string root=Path.Combine(Path.GetTempPath(),"flixypix-brand-"+Guid.NewGuid());
 public BrandCompatibilityTests()=>Directory.CreateDirectory(root);public void Dispose()=>Directory.Delete(root,true);
 [Theory][InlineData(1)][InlineData(2)][InlineData(3)]
 public void PreviousCatalogVersionsRemainReadableAndNewExportsUseFlixyPix(int version)
 {
  var id=Guid.NewGuid();var path=Path.Combine(root,"previous.json");File.WriteAllText(path,JsonSerializer.Serialize(new{format=ProductIdentity.PreviousFormatPrefix+"-catalog",version,items=new[]{new{id,path=@"U:\offline\image.jpg",title="Keep",rating=9,tags=new[]{"Preserved"}}}}));
  using var db=new CatalogRepository(Path.Combine(root,"catalog.db"));Assert.Empty(db.ImportJson(path).Errors);Assert.Equal(id,Assert.Single(db.QueryPage(new()).Items).Id);var
   export=Path.Combine(root,"new.json");db.Export(export);using var
   json=JsonDocument.Parse(File.ReadAllText(export));Assert.Equal("flixypix-catalog",json.RootElement.GetProperty("format").GetString());Assert.Equal(4,json.RootElement.GetProperty("version").GetInt32());
 }
 [Theory][InlineData(1)][InlineData(2)]public void PreviousLegacyExchangeIsReadableWithoutMediaAccess(int version)
 {
  object[] records=[new{type="header",format=ProductIdentity.PreviousFormatPrefix+"-legacy",version,sourceSha256=new
   string('a',64)},new{type="schema",table="movies",columns=new[]{new{name="ID"},new{name="fullpath"},new{name="filename"}}},new{type="row",table="movies",values=new{ID=1,fullpath=@"U:\offline",filename="movie.mp4"}},new{type="footer",tables=1,rows=1,tableRows=new
   Dictionary<string,int>{{"movies",1}},blobCount=0,blobBytes=0}];
  var path=Path.Combine(root,"legacy.jsonl");File.WriteAllLines(path,records.Select(r=>JsonSerializer.Serialize(r)));using var db=new CatalogRepository(Path.Combine(root,"catalog.db"));Assert.Empty(db.ImportLegacyJsonl(path).Errors);Assert.Single(db.QueryPage(new()).Items);
 }
 [Fact]public void PreviousCsvIsAcceptedAndNewCsvUsesCurrentName()
 {
  using var source=new CatalogRepository(Path.Combine(root,"source.db"));source.Upsert(new(){Path=@"U:\offline\movie.mp4",Rating=8});var
   path=Path.Combine(root,"new.csv");CatalogInterchange.ExportCsv(source.QueryPage(new()).Items,path);var
   text=File.ReadAllText(path);Assert.Contains("flixypix-csv",text);File.WriteAllText(path,text.Replace("flixypix-csv",ProductIdentity.PreviousFormatPrefix+"-csv"));using
   var target=new CatalogRepository(Path.Combine(root,"target.db"));Assert.Empty(CatalogInterchange.ImportCsv(target,path).Errors);Assert.Equal(8,Assert.Single(target.QueryPage(new()).Items).Rating);
 }
 [Fact]public async Task PreviousBackupMarkerAndExtensionRestoreWithoutLoss()
 {
  using var source=new CatalogRepository(Path.Combine(root,"source.db"));var item=source.Upsert(new(){Path=@"U:\offline\movie.mp4",Rating=7});var
   current=Path.Combine(root,"current.flixypixbackup");await source.CreateBackupAsync(current,false);var
   previous=Path.Combine(root,"previous"+ProductIdentity.PreviousBackupExtension);
  using(var input=ZipFile.OpenRead(current))using(var output=ZipFile.Open(previous,ZipArchiveMode.Create))foreach(var entry in input.Entries)
  {var copied=output.CreateEntry(entry.FullName);using var from=entry.Open();using var to=copied.Open();if(entry.FullName=="manifest.ndjson"){using
   var reader=new StreamReader(from);var text=reader.ReadToEnd();Assert.Contains("flixypix-backup",text);using var writer=new
   StreamWriter(to);writer.Write(text.Replace("flixypix-backup",ProductIdentity.PreviousFormatPrefix+"-backup"));}else from.CopyTo(to);}
  var restored=Path.Combine(root,"restored");await CatalogRepository.RestoreBackupAsync(previous,restored);using var target=new
   CatalogRepository(Path.Combine(restored,"catalog.db"));Assert.Equal(item.Id,Assert.Single(target.QueryPage(new()).Items).Id);Assert.Equal(7,target.GetMedia(item.Id)!.Rating);
 }
 [Fact]public void FormatAliasesAreExactAndDoNotAcceptUnrelatedMarkers()
 {Assert.True(ProductIdentity.IsFormat(ProductIdentity.PreviousFormatPrefix+"-catalog","flixypix-catalog"));Assert.False(ProductIdentity.IsFormat("other-catalog","flixypix-catalog"));Assert.False(ProductIdentity.IsFormat("FLIXYPIX-CATALOG","flixypix-catalog"));Assert.False(ProductIdentity.IsFormat(null!,"flixypix-catalog"));Assert.Equal("EFP_ORDINAL",ProductIdentity.OrdinalCollation);}
 [Theory][InlineData(null,"previous","previous")][InlineData("current","previous","current")][InlineData("","previous","")][InlineData(null,null,null)]
 public void CurrentPasswordVariableWinsAndPreviousVariableRemainsFallback(string? current,string? previous,string? expected)
 {Assert.Equal(expected,ProductIdentity.ReadCatalogPassword(name=>name==ProductIdentity.PasswordVariable?current:previous));}
}
