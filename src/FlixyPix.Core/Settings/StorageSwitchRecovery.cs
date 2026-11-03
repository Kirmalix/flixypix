namespace FlixyPix.Core;

public static class StorageSwitchRecovery
{
    public const string JournalName = ".storage-switch";

    /// <summary>Runs before mode resolution or loading preferences. A prepared copy is discarded;
    /// once publication starts, it is completed before any destination library can open.</summary>
    public static void Recover(string applicationDirectory,string profileDirectory,bool useCurrentSession=false)
    {
        var directory=Path.Combine(applicationDirectory,JournalName);
        var pending=Directory.Exists(directory);
        if(!pending && (!Directory.Exists(applicationDirectory) || !Directory.EnumerateDirectories(applicationDirectory,JournalName+".completed-*").Any()))return;
        // First take shared admission before inspecting anything. In particular, an
        // empty journal may be another instance's in-progress initialization.
        using var owner=new StorageRootSession(applicationDirectory);
        var context=File.Exists(Path.Combine(directory,"journal.json"))?StorageJournal.ReadContext(directory):null;
        var target=context switch
        {
            "portable"=>AppStoragePaths.Resolve(applicationDirectory,profileDirectory,StorageMode.Portable),
            "profile"=>AppStoragePaths.Resolve(applicationDirectory,profileDirectory,StorageMode.Profile),
            null=>null,
            _=>throw new InvalidDataException("Unknown pending storage mode.")
        };
        if(useCurrentSession)owner.Dispose();
        IDisposable reservation;
        try {reservation=useCurrentSession?AppStorage.ReserveTransfer(applicationDirectory,target?.Root ?? applicationDirectory):owner.Reserve(target?.Root ?? applicationDirectory);}
        catch(IOException) when(!pending) {return;} // completed cleanup can wait; no publication is pending
        using(reservation)
        {
            StorageJournal.CleanupCompleted(applicationDirectory,JournalName);
            if(!Directory.Exists(directory))return;
            if(StorageJournal.RemoveUninitialized(directory))return;
            if(context!=StorageJournal.ReadContext(directory) || target==null)throw new IOException("The pending storage operation changed. Restart to retry recovery.");
            var journal=StorageJournal.Open(directory,new Dictionary<string,string> {["app"]=applicationDirectory,["target"]=target.Root});
            if(journal.IsCommitting)journal.Commit();else journal.Discard();
        }
    }
}
