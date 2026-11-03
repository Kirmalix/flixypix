using System.Diagnostics;
using System.IO;
using System.Text.Json;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

/// <summary>Only called by the isolated --storage-crash-test harness. Kills its own child process;
/// the parent then checks the durable state, without ever touching a user's profile.</summary>
internal static class StorageCrashProbe
{
    internal static readonly Guid MediaId=new("686b9ac2-46b9-45f3-8758-a82f97ad9e25");
    internal static void Run(string root)
    {
        if(File.ReadAllText(Path.Combine(root,"test-owner.txt"))!="flixypix-storage-crash-fixture") throw new InvalidDataException("Not a crash-test fixture.");
        var phase=File.ReadAllText(Path.Combine(root,"phase.txt"));
        if(phase is not ("prepared" or "database-committed" or "file-published")) throw new InvalidDataException("Invalid crash-test phase.");
        var library=Path.Combine(root,"catalog");var old=Path.Combine(root,"old");
        using var catalog=new CatalogRepository(Path.Combine(library,"catalog.db"));
        catalog.Upsert(new MediaItem {Id=MediaId,Path=Path.Combine(old,"media.mp4")});
        catalog.PreparePortableLocation(old,true);
        File.WriteAllText(Path.Combine(library,"queue.json"),JsonSerializer.Serialize(new {format="flixypix-media-queue",input=Path.Combine(old,"media.mp4")}));
        catalog.RelocationCheckpoint=step=>
        {
            if(step!=phase)return;
            File.WriteAllText(Path.Combine(root,"kill-marker.txt"),phase);
            Process.GetCurrentProcess().Kill();
        };
        catalog.PreparePortableLocation(Path.Combine(old,"child"),true);
        throw new InvalidOperationException("Crash checkpoint was not reached.");
    }
}
