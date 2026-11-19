using FlixyPix.Core;
using Xunit;

namespace FlixyPix.Core.Tests;

public sealed class VideoStackTests:IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"flixypix-stack-"+Guid.NewGuid().ToString("N"));
    private CatalogRepository Open()=>new(Path.Combine(root,"catalog.db"));
    private MediaItem Add(CatalogRepository c,string title)=>c.Upsert(new MediaItem{Path=Path.Combine(root,title+".mp4"),Title=title,Kind=MediaKind.Video});
    [Fact]
    public void PagingFilteringAndExpansionUseOneOrdering()
    {
        using var c=Open();var a=Add(c,"A");var b=Add(c,"B");var z=Add(c,"Z");var stack=c.SaveVideoStack(new(Guid.NewGuid(),"Group",z.Id,[a.Id,z.Id]));c.Upsert(a with{Tags=["needle"]});
        var query=new MediaQuery{GroupVideoStacks=true,Limit=1};
        Assert.Equal(b.Id,Assert.Single(c.QueryPage(query).Items).Id);
        Assert.Equal(2,c.QueryPage(query).TotalCount);
        Assert.Equal(z.Id,Assert.Single(c.QueryPage(query with{Offset=1}).Items).Id);
        Assert.Equal(1,c.GetPosition(query,z.Id));Assert.Null(c.GetPosition(query,a.Id));
        Assert.Equal(new[]{b.Id,z.Id}.Order(),c.VisibleVideoStackMedia(query,[a.Id,b.Id,z.Id]).Order());
        var expanded=query with{ExpandedVideoStacks=[stack.Id],Limit=10};
        Assert.Equal(new[]{b.Id,z.Id,a.Id},c.QueryPage(expanded).Items.Select(i=>i.Id));
        Assert.Equal(a.Id,Assert.Single(c.QueryPage(query with{Tag="needle"}).Items).Id);
        Assert.Equal(z.Id,Assert.Single(c.GetVideoStacks()).MasterId);
    }
    [Fact]
    public void MembershipAndStaleWritesAreAtomic()
    {
        using var c=Open();var a=Add(c,"A");var b=Add(c,"B");var d=Add(c,"D");
        var stack=c.SaveVideoStack(new(Guid.NewGuid(),null,a.Id,[a.Id,b.Id]));
        Assert.Throws<InvalidOperationException>(()=>c.SaveVideoStack(new(Guid.NewGuid(),null,a.Id,[a.Id,d.Id])));
        var renamed=c.SaveVideoStack(stack with{Name="New"});
        Assert.Throws<InvalidOperationException>(()=>c.SaveVideoStack(stack with{Name="Stale"}));
        Assert.Equal(renamed,Assert.Single(c.GetVideoStacks()) with{MediaIds=renamed.MediaIds});
    }
    [Fact]
    public void MasterDeletionAndDissolutionPreserveOtherVideos()
    {
        using var c=Open();var a=Add(c,"A");var b=Add(c,"B");var d=Add(c,"D");
        c.SaveVideoStack(new(Guid.NewGuid(),null,d.Id,[a.Id,b.Id,d.Id]));
        c.Delete(d.Id);Assert.Equal(a.Id,Assert.Single(c.GetVideoStacks()).MasterId);
        c.Delete(a.Id);Assert.Empty(c.GetVideoStacks());Assert.NotNull(c.GetMedia(b.Id));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PortableRoundTripAndPresentationSurviveReopen(bool encrypted)
    {
        var export=Path.Combine(root,"export.json");Guid stackId;
        using(var empty=Open()){}
        if(encrypted)CatalogEncryption.EnableEncryption(Path.Combine(root,"catalog.db"),"synthetic-stack-password");
        using var key=encrypted?CatalogEncryption.Unlock(Path.Combine(root,"catalog.db"),"synthetic-stack-password"):null;
        using(var c=new CatalogRepository(Path.Combine(root,"catalog.db"),key))
        {
            var a=Add(c,"A");var b=Add(c,"B");var stack=c.SaveVideoStack(new(Guid.NewGuid(),"Named",b.Id,[a.Id,b.Id]));stackId=stack.Id;
            c.SaveVideoStackPresentation(true,[stackId]);c.Export(export);
        }
        using(var c=new CatalogRepository(Path.Combine(root,"catalog.db"),key))Assert.Contains(stackId,c.GetVideoStackPresentation().Expanded);
        using var other=new CatalogRepository(Path.Combine(root,"other.db"));var imported=other.ImportJson(export,CatalogRepository.MaximumPortableEntityBytes,default,encrypted?"synthetic-stack-password":null);
        Assert.Empty(imported.Errors);Assert.Equal(stackId,Assert.Single(other.GetVideoStacks()).Id);
    }
    [Fact]
    public void ExplicitMovesNormalizeSourcesAndStaleTargetRollsBack()
    {
        using var c=Open();var a=Add(c,"A");var b=Add(c,"B");var d=Add(c,"D");var e=Add(c,"E");
        var source=c.SaveVideoStack(new(Guid.NewGuid(),null,a.Id,[a.Id,b.Id]));
        var target=c.SaveVideoStack(new(Guid.NewGuid(),null,d.Id,[d.Id,e.Id]));
        c.SaveVideoStack(target with{Name="Changed"});
        Assert.Throws<InvalidOperationException>(()=>c.MoveVideoStackMembers(target with{MediaIds=[d.Id,e.Id,a.Id]}));
        Assert.Contains(c.GetVideoStacks(),s=>s.Id==source.Id && s.MediaIds.Contains(a.Id));
        target=c.GetVideoStacks().Single(s=>s.Id==target.Id);
        c.MoveVideoStackMembers(target with{MediaIds=[d.Id,e.Id,a.Id]});
        Assert.Single(c.GetVideoStacks());Assert.NotNull(c.GetMedia(b.Id));
    }
    [Fact]
    public void FileMergeKeepsMembershipAndRejectsAmbiguousStacks()
    {
        using var c=Open();var a=Add(c,"A");var b=Add(c,"B");var d=Add(c,"D");var e=Add(c,"E");
        var source=c.SaveVideoStack(new(Guid.NewGuid(),null,a.Id,[a.Id,b.Id,d.Id]));
        c.MergeVersions(a.Id,[b.Id]);Assert.Equal(new[]{a.Id,d.Id}.Order(),c.GetVideoStacks().Single().MediaIds.Order());
        var other=c.SaveVideoStack(new(Guid.NewGuid(),null,e.Id,[e.Id,Add(c,"F").Id]));
        Assert.Throws<InvalidOperationException>(()=>c.MergeVersions(a.Id,[e.Id]));Assert.NotNull(c.GetMedia(e.Id));Assert.Equal(2,c.GetVideoStacks().Count);
    }
    [Fact]
    public void StackAndMediaWithSameIdentityDoNotShareABucket()
    {
        using var c=Open();var a=Add(c,"A");var b=Add(c,"B");var d=Add(c,"D");
        c.SaveVideoStack(new(d.Id,null,a.Id,[a.Id,b.Id]));Assert.Equal(2,c.QueryPage(new(){GroupVideoStacks=true}).TotalCount);
        Assert.Equal(new[]{a.Id,d.Id},c.QueryPage(new(){GroupVideoStacks=true}).Items.Select(i=>i.Id));
    }
    [Fact]
    public void CachedOrderFollowsOwnAndForeignWritesAndFailedImports()
    {
        using var c=Open();var a=Add(c,"A");var b=Add(c,"B");var d=Add(c,"D");
        c.SaveVideoStack(new(Guid.NewGuid(),null,a.Id,[a.Id,b.Id]));
        var query=new MediaQuery{GroupVideoStacks=true};Assert.Equal(a.Id,c.QueryPage(query).Items[0].Id);
        c.Upsert(a with{Title="Z"});Assert.Equal(d.Id,c.QueryPage(query).Items[0].Id);
        using(var other=Open())other.Upsert(a with{Title="AA"});Assert.Equal(a.Id,c.QueryPage(query).Items[0].Id);
        var invalid=Path.Combine(root,"invalid.json");File.WriteAllText(invalid,"invalid");Assert.NotEmpty(c.ImportJson(invalid).Errors);
        Assert.Equal(a.Id,c.QueryPage(query).Items[0].Id);Assert.Equal(0,c.GetPosition(query,a.Id));
    }
    [Fact]
    public void MigrationBacksUpVersion27BeforeAddingStacks()
    {
        Guid media;
        using(var c=Open())media=Add(c,"Kept").Id;
        using(var sql=new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(root,"catalog.db")};Pooling=False"))
        {
            sql.Open();CatalogRepository.AddOrdinalCollation(sql);using var command=sql.CreateCommand();
            command.CommandText="DROP TRIGGER video_stack_removed; DROP TABLE video_stack_members; DROP TABLE video_stacks; PRAGMA user_version=27;";command.ExecuteNonQuery();
        }
        using var upgraded=Open();Assert.True(upgraded.UpgradedOnOpen);Assert.True(File.Exists(upgraded.MigrationBackupPath));Assert.NotNull(upgraded.GetMedia(media));Assert.Empty(upgraded.GetVideoStacks());
    }
    [Fact]
    public void FailedMembershipWriteRestoresThePreviousStack()
    {
        using var c=Open();var a=Add(c,"A");var b=Add(c,"B");var d=Add(c,"D");var stack=c.SaveVideoStack(new(Guid.NewGuid(),"Keep",a.Id,[a.Id,b.Id]));
        using(var sql=new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={c.DatabasePath};Pooling=False"))
        {
            sql.Open();using var command=sql.CreateCommand();command.CommandText="CREATE TRIGGER fail_stack_write BEFORE INSERT ON video_stack_members BEGIN SELECT raise(ABORT,'synthetic fault'); END;";command.ExecuteNonQuery();
        }
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(()=>c.SaveVideoStack(stack with{Name="Lost",MediaIds=[a.Id,b.Id,d.Id]}));
        var retained=Assert.Single(c.GetVideoStacks());Assert.Equal("Keep",retained.Name);Assert.Equal(stack.Revision,retained.Revision);Assert.Equal(stack.MediaIds.Order(),retained.MediaIds.Order());
    }
    [Fact]
    public void CatalogRepairReplacesAnInvalidMasterWithoutRemovingMedia()
    {
        using var c=Open();var a=Add(c,"A");var b=Add(c,"B");c.SaveVideoStack(new(Guid.NewGuid(),null,a.Id,[a.Id,b.Id]));
        using(var sql=new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={c.DatabasePath};Pooling=False"))
        {
            sql.Open();using var command=sql.CreateCommand();command.CommandText="UPDATE video_stacks SET master_id=$missing";command.Parameters.AddWithValue("$missing",Guid.NewGuid().ToString());command.ExecuteNonQuery();
        }
        var report=c.CheckCatalog(root);Assert.Equal(1,report[CatalogFindingKind.VideoStacks].Count);
        var repaired=c.RepairCatalog(report,new HashSet<CatalogFindingKind>{CatalogFindingKind.VideoStacks},Path.Combine(root,"safety"));
        Assert.True(File.Exists(repaired.BackupPath));Assert.Contains(c.GetVideoStacks().Single().MasterId,new[]{a.Id,b.Id});Assert.Equal(2,c.CountMedia(new()));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BackupRestoresVideoStacks(bool encrypted)
    {
        using(var empty=Open()){}
        var database=Path.Combine(root,"catalog.db");
        if(encrypted)CatalogEncryption.EnableEncryption(database,"synthetic-backup-password");
        using var key=encrypted?CatalogEncryption.Unlock(database,"synthetic-backup-password"):null;
        using var c=new CatalogRepository(database,key);var a=Add(c,"A");var b=Add(c,"B");var stack=c.SaveVideoStack(new(Guid.NewGuid(),"Backup stack",b.Id,[a.Id,b.Id]));
        var package=Path.Combine(root,"backup.zip");await c.CreateBackupAsync(package,true);
        var restored=await CatalogRepository.RestoreBackupAsync(package,Path.Combine(root,"restored"),secret:encrypted?"synthetic-backup-password":null);
        using var restoredKey=encrypted?CatalogEncryption.Unlock(restored.DatabasePath,"synthetic-backup-password"):null;
        using var target=new CatalogRepository(restored.DatabasePath,restoredKey);var retained=Assert.Single(target.GetVideoStacks());Assert.Equal(stack.Id,retained.Id);Assert.Equal(stack.MasterId,retained.MasterId);Assert.Equal(stack.MediaIds.Order(),retained.MediaIds.Order());
    }
    public void Dispose(){if(Directory.Exists(root))Directory.Delete(root,true);}
}
