using System.Diagnostics;

namespace FlixyPix.Core;

/// <summary>Shared admission for running instances, exclusive admission for a storage transfer.
/// The OS releases the file handles after a crash; no timestamp-based lock stealing is needed.</summary>
public sealed class StorageRootSession : IDisposable
{
    private FileStream? shared;
    public string Root { get; }

    public StorageRootSession(string root)
    {
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        using var gate = Gates([Root]);
        shared = Open(Root, FileShare.ReadWrite);
    }

    public IDisposable Reserve(params string[] roots)
    {
        var all = roots.Append(Root).Select(p=>Path.TrimEndingDirectorySeparator(Path.GetFullPath(p))).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        using var gate = Gates(all);
        shared?.Dispose(); shared = null;
        var handles = new List<FileStream>();
        try
        {
            foreach (var root in all) handles.Add(Open(root, FileShare.None));
            return new ActionLease(() =>
            {
                using var releaseGate = Gates(all);
                foreach (var handle in handles) handle.Dispose();
                shared = Open(Root, FileShare.ReadWrite);
            });
        }
        catch
        {
            foreach (var handle in handles) handle.Dispose();
            shared = Open(Root, FileShare.ReadWrite);
            throw new IOException("Close the other FlixyPix windows first. Storage is currently in use or is not writable.");
        }
    }

    private static FileStream Open(string root, FileShare share)
    {
        FileAccessPolicy.Current.EnsureWritable(root);
        Directory.CreateDirectory(root);
        return new FileStream(Path.Combine(root, ".storage-access.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, share);
    }

    private static IDisposable Gates(IEnumerable<string> roots)
    {
        // A file-range lock is shared across Windows sessions and path aliases. A
        // Local named mutex alone cannot protect a shared USB folder across sessions.
        if(OperatingSystem.IsMacOS())throw new PlatformNotSupportedException("Storage admission requires file-range locks.");
        var held=new List<FileStream>();
        try
        {
            foreach(var root in roots.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
            {
                FileAccessPolicy.Current.EnsureWritable(root);Directory.CreateDirectory(root);
                var file=new FileStream(Path.Combine(root,".storage-admission.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.ReadWrite);
                try
                {
                    var elapsed=Stopwatch.StartNew();
                    while(true)
                    {
                        try {file.Lock(0,1);break;}
                        catch(IOException) when(elapsed.Elapsed<TimeSpan.FromSeconds(5)) {Thread.Sleep(10);}
                    }
                    held.Add(file);
                }
                catch {file.Dispose();throw;}
            }
            return new ActionLease(()=>
            {
                foreach(var file in held.AsEnumerable().Reverse()) {ReleaseGate(file);}
            });
        }
        catch
        {
            foreach(var file in held.AsEnumerable().Reverse()) {ReleaseGate(file);}
            throw;
        }
    }

    private static void ReleaseGate(FileStream file)
    {
        try {if(!OperatingSystem.IsMacOS())file.Unlock(0,1);}
        catch(IOException) { /* disposing still releases an unplugged drive's handle */ }
        finally {file.Dispose();}
    }
    public void Dispose() { shared?.Dispose(); shared = null; }

    private sealed class ActionLease(Action release) : IDisposable
    {
        private Action? action = release;
        public void Dispose() => Interlocked.Exchange(ref action, null)?.Invoke();
    }
}
