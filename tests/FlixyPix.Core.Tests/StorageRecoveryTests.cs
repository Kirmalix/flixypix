using System.Text;
using System.Text.Json;
using FlixyPix.Core;
using Xunit;

namespace FlixyPix.Core.Tests;

public sealed class StorageRecoveryTests : IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"flixypix-recovery-"+Guid.NewGuid().ToString("N"));
    public StorageRecoveryTests()=>Directory.CreateDirectory(root);
    public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root,true); }
    private sealed class Interrupted : Exception { }

    [Theory]
    [InlineData("prepared")]
    [InlineData("database-committed")]
    [InlineData("file-published")]
    public void NestedRelocationRecoversWithoutMappingPathsTwice(string phase)
    {
        var old=Path.Combine(root,"app"); var moved=Path.Combine(old,"child"); var library=Path.Combine(old,"Data","Library");
        var id=Guid.NewGuid();
        using(var db=new CatalogRepository(Path.Combine(library,"catalog.db")))
        {
            db.Upsert(new MediaItem {Id=id,Path=Path.Combine(old,"media","one.mp4"),ThumbnailPath=Path.Combine(library,"thumb.jpg"),Extra=new(){{"migration.originalPath",Path.Combine(old,"untouched.mp4")}}});
            db.PreparePortableLocation(old,true);
        }
        File.WriteAllText(Path.Combine(library,"queue.json"),JsonSerializer.Serialize(new {format="flixypix-media-queue",jobs=new[]{new {input=Path.Combine(old,"media","one.mp4"),output=Path.Combine(old,"output.mp4")}}}));
        var newLibrary=Path.Combine(moved,"Data","Library"); Directory.CreateDirectory(Path.GetDirectoryName(newLibrary)!); Directory.Move(library,newLibrary);
        using(var db=new CatalogRepository(Path.Combine(newLibrary,"catalog.db")))
        {
            db.RelocationCheckpoint=step=> {if(step==phase)throw new Interrupted();};
            Assert.Throws<Interrupted>(()=>db.PreparePortableLocation(moved,true));
        }
        using(var db=new CatalogRepository(Path.Combine(newLibrary,"catalog.db")))
        {
            db.PreparePortableLocation(moved,true);
            Assert.Equal(Path.Combine(moved,"media","one.mp4"),db.GetMedia(id)!.Path);
            Assert.Equal(Path.Combine(newLibrary,"thumb.jpg"),db.GetMedia(id)!.ThumbnailPath);
            Assert.Equal(Path.Combine(old,"untouched.mp4"),db.GetMedia(id)!.Extra["migration.originalPath"]);
            db.PreparePortableLocation(moved,true);
            Assert.Equal(Path.Combine(moved,"media","one.mp4"),db.GetMedia(id)!.Path);
        }
        using var queue=JsonDocument.Parse(File.ReadAllText(Path.Combine(newLibrary,"queue.json")));
        Assert.Equal(Path.Combine(moved,"media","one.mp4"),queue.RootElement.GetProperty("jobs")[0].GetProperty("input").GetString());
        Assert.False(Directory.Exists(Path.Combine(newLibrary,".relocation-pending")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StorageSwitchRecoversPreparedOrPartlyPublishedData(bool publish)
    {
        var app=Path.Combine(root,"app"); var target=Path.Combine(app,"Data"); Directory.CreateDirectory(app); Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target,"appsettings.json"),"old");
        var journal=StorageJournal.Begin(Path.Combine(app,StorageSwitchRecovery.JournalName),new Dictionary<string,string>{{"app",app},{"target",target}},"portable");
        var library=journal.AddDirectory("target","Libraries/copied"); Directory.CreateDirectory(library); File.WriteAllText(Path.Combine(library,"catalog.db"),"verified snapshot");
        journal.AddFile("target","appsettings.json",Encoding.UTF8.GetBytes("new"),true);
        journal.AddFile("app","storage-mode.json",Encoding.UTF8.GetBytes("{\"mode\":\"portable\"}"),true);
        journal.MarkDirectoriesReady();
        if(publish) { journal.Checkpoint=step=> {if(step=="file-published")throw new Interrupted();}; Assert.Throws<Interrupted>(()=>journal.Commit()); }
        StorageSwitchRecovery.Recover(app,Path.Combine(root,"profile"));
        Assert.Equal(publish?"new":"old",File.ReadAllText(Path.Combine(target,"appsettings.json")));
        Assert.Equal(publish,Directory.Exists(Path.Combine(target,"Libraries/copied")));
        Assert.Equal(publish,File.Exists(Path.Combine(app,"storage-mode.json")));
        Assert.False(Directory.Exists(Path.Combine(app,StorageSwitchRecovery.JournalName)));
    }

    [Fact] public void TransferReservationExcludesNewInstancesAndOtherRunningRoots()
    {
        var first=Path.Combine(root,"first"); var second=Path.Combine(root,"second");
        using var session=new StorageRootSession(first);
        using(var other=new StorageRootSession(second)) Assert.Throws<IOException>(()=>session.Reserve(second));
        using(session.Reserve(second))
        {
            Assert.Throws<IOException>(()=>new StorageRootSession(first));
            Assert.Throws<IOException>(()=>new StorageRootSession(second));
        }
        using var allowed=new StorageRootSession(first);
    }

    [Fact] public void StartupCannotDeleteAnInitializingJournalOfAnActiveTransfer()
    {
        var app=Path.Combine(root,"app");var target=Path.Combine(app,"Data");
        using var owner=new StorageRootSession(app);
        using(owner.Reserve(target))
        {
            var pending=Path.Combine(app,StorageSwitchRecovery.JournalName);Directory.CreateDirectory(pending);
            Assert.Throws<IOException>(()=>StorageSwitchRecovery.Recover(app,Path.Combine(root,"profile")));
            Assert.True(Directory.Exists(pending));
        }
        owner.Dispose();
        StorageSwitchRecovery.Recover(app,Path.Combine(root,"profile"));
        Assert.False(Directory.Exists(Path.Combine(app,StorageSwitchRecovery.JournalName)));
    }

    [Fact] public void JournalRefusesEscapingPathsAndKeepsInterveningUserEdits()
    {
        var target=Path.Combine(root,"target");Directory.CreateDirectory(target);
        var path=Path.Combine(target,"settings.json");File.WriteAllText(path,"original");
        var directory=Path.Combine(root,"journal");
        var journal=StorageJournal.Begin(directory,new Dictionary<string,string>{{"target",target}},"portable");
        Assert.Throws<InvalidDataException>(()=>journal.AddFile("target","../escape.json",Encoding.UTF8.GetBytes("data"),true));
        journal.AddFile("target","settings.json",Encoding.UTF8.GetBytes("prepared"),true);
        journal.Checkpoint=step=> {if(step=="committing")throw new Interrupted();};
        Assert.Throws<Interrupted>(()=>journal.Commit());
        File.WriteAllText(path,"edited by user");
        var recovered=StorageJournal.Open(directory,new Dictionary<string,string>{{"target",target}});
        Assert.Throws<IOException>(()=>recovered.Commit());
        Assert.Equal("edited by user",File.ReadAllText(path));
    }

    [Fact] public void IncompleteInitializationAndCompletedCleanupAreRecoverable()
    {
        var app=Path.Combine(root,"app");var directory=Path.Combine(app,StorageSwitchRecovery.JournalName);
        Directory.CreateDirectory(directory);File.WriteAllText(Path.Combine(directory,"journal.json.partial.tmp"),"partial write");
        StorageSwitchRecovery.Recover(app,Path.Combine(root,"unused-profile"));
        Assert.False(Directory.Exists(directory));Assert.False(Directory.Exists(Path.Combine(root,"unused-profile")));
        var journal=StorageJournal.Begin(directory,new Dictionary<string,string>{{"app",app}},"portable");
        journal.AddFile("app","new.json",Encoding.UTF8.GetBytes("staged"),false);
        var completed=directory+".completed-"+journal.Id;Directory.Move(directory,completed);
        StorageSwitchRecovery.Recover(app,Path.Combine(root,"unused-profile"));
        Assert.False(Directory.Exists(completed));Assert.False(File.Exists(Path.Combine(app,"new.json")));
    }

    [Fact] public void JournalPathsAlsoWorkWhenTheRootIsAVolumeRoot()
    {
        var volume=Path.GetPathRoot(root)!;
        Assert.Equal(Path.Combine(volume,"flixypix-path-check-only"),StorageFiles.Under(volume,"flixypix-path-check-only"));
        Assert.Throws<InvalidDataException>(()=>StorageFiles.Under(volume,"."));
    }
}
