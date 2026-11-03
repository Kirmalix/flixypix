using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FlixyPix.Core;

/// <summary>A durable, rooted publication plan. Before commit it can be discarded;
/// after commit intent it is replayed forward. Published files are never mapped a second time.</summary>
public sealed class StorageJournal
{
    private sealed record Entry(string Root, string Destination, string? File, string? Container, string? Hash, string? Before, bool Replace);
    private sealed record Plan(string Format, string Id, string Context, bool Committing, List<Entry> Entries);
    private readonly string directory;
    private readonly IReadOnlyDictionary<string, string> roots;
    private Plan plan;
    internal Action<string>? Checkpoint { get; set; }
    public string Id => plan.Id;
    public string Context => plan.Context;
    public bool IsCommitting => plan.Committing;

    private StorageJournal(string directory, IReadOnlyDictionary<string, string> roots, Plan plan)
    { this.directory = Path.GetFullPath(directory); this.roots = roots; this.plan = plan; }

    public static StorageJournal Begin(string directory, IReadOnlyDictionary<string, string> roots, string context)
    {
        if (Directory.Exists(directory) || File.Exists(directory)) throw new IOException("A storage operation is pending. Restart to finish recovery first.");
        StorageFiles.RejectLinks(directory);
        Directory.CreateDirectory(directory);
        var result = new StorageJournal(directory, roots, new("flixypix-storage-journal", Guid.NewGuid().ToString("N"), context, false, []));
        result.Save(); return result;
    }

    public static string ReadContext(string directory) => Read(directory).Context;
    public static void CleanupCompleted(string parent,string journalName)
    {
        if(!Directory.Exists(parent))return;
        foreach(var path in Directory.EnumerateDirectories(parent,journalName+".completed-*"))
        {
            try
            {
                StorageFiles.RejectLinks(path);
                var manifest=Path.Combine(path,"journal.json");
                if(File.Exists(manifest))
                {
                    var saved=Read(path);
                    if(saved.Format!="flixypix-storage-journal" || !path.EndsWith(".completed-"+saved.Id,StringComparison.Ordinal))continue;
                    DeleteCompleted(path);
                }
                else if(!Directory.EnumerateFileSystemEntries(path).Any())Directory.Delete(path,false);
            }
            catch(IOException) { /* retain files whose ownership cannot be established */ }
            catch(UnauthorizedAccessException) { }
            catch(JsonException) { }
        }
    }
    public static bool RemoveUninitialized(string directory)
    {
        if(!Directory.Exists(directory) || File.Exists(Path.Combine(directory,"journal.json"))) return false;
        StorageFiles.RejectLinks(directory);
        if(new DirectoryInfo(directory).EnumerateFileSystemInfos().Any(e=>e is DirectoryInfo || !e.Name.StartsWith("journal.json.",StringComparison.Ordinal) || !e.Name.EndsWith(".tmp",StringComparison.Ordinal)))
            throw new InvalidDataException("An unrecognized file occupies the storage journal folder.");
        StorageFiles.DeleteTree(directory);return true;
    }
    public static StorageJournal Open(string directory, IReadOnlyDictionary<string, string> roots)
    {
        var plan = Read(directory);
        if (plan.Format != "flixypix-storage-journal" || !Guid.TryParseExact(plan.Id,"N",out _) || plan.Entries == null || plan.Entries.Count > 100000)
            throw new InvalidDataException("Invalid storage recovery journal.");
        var result = new StorageJournal(directory, roots, plan);
        foreach (var entry in plan.Entries)
        {
            if(entry == null || (entry.File == null) == (entry.Container == null))throw new InvalidDataException("Invalid storage journal entry.");
            _ = result.Destination(entry);
            if (entry.File != null && (Path.GetFileName(entry.File) != entry.File || !entry.File.EndsWith(".data",StringComparison.Ordinal))) throw new InvalidDataException("Invalid journal file.");
            if (entry.Container != null && (Path.GetFileName(entry.Container) != entry.Container || !entry.Container.StartsWith(".storage-copy-" + plan.Id + "-", StringComparison.Ordinal) || !int.TryParse(entry.Container[(".storage-copy-" + plan.Id + "-").Length..], out var index) || index < 0)) throw new InvalidDataException("Invalid journal staging folder.");
        }
        return result;
    }

    private static Plan Read(string directory)
    {
        StorageFiles.RejectLinks(directory);
        var path = Path.Combine(directory,"journal.json");
        if (new FileInfo(path).Length > 64 * 1024 * 1024) throw new InvalidDataException("Storage journal is too large.");
        return JsonSerializer.Deserialize<Plan>(File.ReadAllBytes(path)) ?? throw new InvalidDataException("Invalid storage journal.");
    }

    public void AddFile(string root, string relative, byte[] data, bool replace)
    {
        if (plan.Committing) throw new InvalidOperationException("Storage publication already started.");
        var name = plan.Entries.Count + ".data";
        var entry = new Entry(root,relative,name,null,Hash(data),null,replace);
        var target = Destination(entry);
        if (File.Exists(target))
        {
            if (!replace) throw new IOException("The destination already exists: " + target);
            entry = entry with { Before = Hash(File.ReadAllBytes(target)) };
        }
        StorageFiles.AtomicWrite(Path.Combine(directory,name),data);
        plan.Entries.Add(entry); Save();
    }

    /// <summary>Creates an owned container; the caller copies and verifies a library into its 'library' child.</summary>
    public string AddDirectory(string root, string relative)
    {
        if (plan.Committing) throw new InvalidOperationException("Storage publication already started.");
        var entry = new Entry(root,relative,null,".storage-copy-" + Id + "-" + plan.Entries.Count,null,null,false);
        if (Directory.Exists(Destination(entry)) || File.Exists(Destination(entry))) throw new IOException("The library destination already exists.");
        plan.Entries.Add(entry); Save();
        var container = Container(entry);
        if (Directory.Exists(container)) throw new IOException("The staging folder already exists.");
        Directory.CreateDirectory(container);
        StorageFiles.AtomicWrite(Path.Combine(container,"owner"),Encoding.UTF8.GetBytes(Id));
        return Path.Combine(container,"library");
    }

    public void MarkDirectoriesReady()
    {
        foreach (var entry in plan.Entries.Where(e => e.Container != null))
        {
            var library = Path.Combine(Container(entry),"library");
            StorageFiles.RejectLinks(library);
            StorageFiles.AtomicWrite(Path.Combine(library,".storage-transfer-owner"),Encoding.UTF8.GetBytes(Id));
        }
    }

    public void Commit()
    {
        if (!plan.Committing)
        {
            // All data and ownership markers must exist before publishing intent.
            ValidateStages();
            plan = plan with { Committing = true }; Save();
        }
        Checkpoint?.Invoke("committing");
        foreach (var entry in plan.Entries)
        {
            var target = Destination(entry);
            if (entry.Container != null)
            {
                if (Directory.Exists(target))
                {
                    if (File.ReadAllText(Path.Combine(target,".storage-transfer-owner")) != Id) throw new IOException("A different library occupies the recovery destination.");
                }
                else
                {
                    var stage = Path.Combine(Container(entry),"library");
                    if (File.ReadAllText(Path.Combine(stage,".storage-transfer-owner")) != Id) throw new IOException("Unowned staging library.");
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    Directory.Move(stage,target);
                }
                Checkpoint?.Invoke("directory-published");
            }
            else
            {
                var data = File.ReadAllBytes(Path.Combine(directory,entry.File!));
                if (Hash(data) != entry.Hash) throw new InvalidDataException("Damaged storage staging file.");
                var existing = File.Exists(target) ? Hash(File.ReadAllBytes(target)) : null;
                if (existing != entry.Hash)
                {
                    if (existing != entry.Before || (!entry.Replace && existing != null)) throw new IOException("A destination changed during storage recovery: " + target);
                    StorageFiles.AtomicWrite(target,data);
                }
                Checkpoint?.Invoke("file-published");
            }
        }
        Cleanup();
    }

    public void Discard()
    {
        if (plan.Committing) throw new InvalidOperationException("A committed storage operation must be recovered forward.");
        Cleanup();
    }

    private void ValidateStages()
    {
        foreach (var entry in plan.Entries)
        {
            if (entry.Container != null)
            {
                if (File.ReadAllText(Path.Combine(Container(entry),"library",".storage-transfer-owner")) != Id) throw new IOException("The staged library is not ready.");
            }
            else if (Hash(File.ReadAllBytes(Path.Combine(directory,entry.File!))) != entry.Hash) throw new InvalidDataException("Damaged storage staging file.");
        }
    }
    private string Destination(Entry entry)
    {
        if (!roots.TryGetValue(entry.Root,out var root)) throw new InvalidDataException("Unknown storage journal root.");
        return StorageFiles.Under(root,entry.Destination);
    }
    private string Container(Entry entry) => StorageFiles.Under(roots[entry.Root],entry.Container!);
    private void Save() => StorageFiles.AtomicWrite(Path.Combine(directory,"journal.json"),JsonSerializer.SerializeToUtf8Bytes(plan));
    private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data));
    private void Cleanup()
    {
        foreach (var entry in plan.Entries.Where(e => e.Container != null))
        {
            var container = Container(entry);
            if (!Directory.Exists(container)) continue;
            var owner=Path.Combine(container,"owner");
            if(!File.Exists(owner))
            {
                if(new DirectoryInfo(container).EnumerateFileSystemInfos().Any(e=>e is DirectoryInfo || !e.Name.StartsWith("owner.",StringComparison.Ordinal) || !e.Name.EndsWith(".tmp",StringComparison.Ordinal)))
                    throw new IOException("Unowned storage staging folder.");
            }
            else if (File.ReadAllText(owner) != Id) throw new IOException("Unowned storage staging folder.");
            StorageFiles.DeleteTree(container);
        }
        // Remove the canonical journal name atomically before deleting payload files.
        // A crash during cleanup must never leave a pending plan with missing payloads.
        var finished=directory+".completed-"+Id;
        Directory.Move(directory,finished);
        DeleteCompleted(finished);
    }
    private static void DeleteCompleted(string directory)
    {
        // Keep the identifying manifest until all payload files have gone.
        foreach(var item in new DirectoryInfo(directory).EnumerateFileSystemInfos())
        {
            if(item.Name=="journal.json")continue;
            StorageFiles.RejectLinks(item.FullName);
            if(item is DirectoryInfo)StorageFiles.DeleteTree(item.FullName);else File.Delete(item.FullName);
        }
        File.Delete(Path.Combine(directory,"journal.json"));Directory.Delete(directory,false);
    }
}

internal static class StorageFiles
{
    internal static string Under(string root, string relative)
    {
        if (Path.IsPathRooted(relative)) throw new InvalidDataException("A storage journal path must be relative.");
        var absoluteRoot = Path.GetFullPath(root);
        var prefix = Path.EndsInDirectorySeparator(absoluteRoot) ? absoluteRoot : absoluteRoot + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(root,relative));
        if (Path.TrimEndingDirectorySeparator(full).Equals(Path.TrimEndingDirectorySeparator(absoluteRoot),StringComparison.OrdinalIgnoreCase) || !full.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Storage path leaves its root.");
        RejectLinks(full); return full;
    }
    internal static void RejectLinks(string path)
    {
        for (string? part=Path.GetFullPath(path);part!=null;part=Path.GetDirectoryName(part))
            if ((File.Exists(part) || Directory.Exists(part)) && (File.GetAttributes(part)&FileAttributes.ReparsePoint)!=0) throw new IOException("Links are not followed during storage changes.");
    }
    internal static void AtomicWrite(string path, byte[] bytes)
    {
        FileAccessPolicy.Current.EnsureWritable(path); RejectLinks(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try { using(var file=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None,81920,FileOptions.WriteThrough)) { file.Write(bytes); file.Flush(true); } File.Move(temp,path,true); }
        finally { if(File.Exists(temp)) File.Delete(temp); }
    }
    internal static void DeleteTree(string root)
    {
        RejectLinks(root); FileAccessPolicy.Current.EnsureWritable(root);
        foreach(var item in new DirectoryInfo(root).EnumerateFileSystemInfos())
        { RejectLinks(item.FullName); if(item is DirectoryInfo) DeleteTree(item.FullName); else File.Delete(item.FullName); }
        Directory.Delete(root,false);
    }
}
