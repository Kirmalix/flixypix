using System.Text.Json;

namespace FlixyPix.Core;

/// <summary>Automatic backups of a library, kept in its catalog so they move with it. <paramref name="IntervalDays"/> 0 is off;
/// <paramref name="Folder"/> null means the folder "backups" of the library.</summary>
public sealed record BackupSchedule(int IntervalDays = 0, int Keep = 5, string? Folder = null, bool IncludeMedia = false)
{
    public bool Enabled => IntervalDays > 0;
    public string FolderOf(string libraryDirectory) => string.IsNullOrWhiteSpace(Folder) ? Path.Combine(libraryDirectory, "backups") : Folder;

    /// <summary>Whether an automatic backup is due: never backed up, or the last backup (of any kind) is at least one interval old.</summary>
    public bool IsDue(CatalogMaintenanceLog log, DateTimeOffset now) => Enabled && (log.LastBackupAt is not { } last || now - last >= TimeSpan.FromDays(IntervalDays));
}

public enum StoredBackupKind { Automatic, SafetyCopy }

/// <summary>A backup in the backup folder: an automatic package or a copy of the catalog made before a repair.</summary>
public sealed record StoredBackup(string Path, StoredBackupKind Kind, DateTime WrittenAt, long Bytes);

/// <summary>Names, listing and rotation of the files in a backup folder. Only files FlixyPix named itself are listed or deleted.</summary>
public static class BackupRotation
{
    public const string AutomaticPrefix = "FlixyPix-auto-", PackageExtension = ".flixypixbackup", SafetyPrefix = "catalog-before-";

    public static string NewAutomaticPath(string folder, DateTime now) => Path.Combine(folder, $"{AutomaticPrefix}{now:yyyyMMdd-HHmmss}{PackageExtension}");

    public static IReadOnlyList<StoredBackup> List(string folder)
    {
        if (!Directory.Exists(folder)) return [];
        var found = new List<StoredBackup>();
        foreach (var file in new DirectoryInfo(folder).EnumerateFiles())
        {
            StoredBackupKind? kind = file.Name.StartsWith(AutomaticPrefix, StringComparison.OrdinalIgnoreCase) && file.Name.EndsWith(PackageExtension, StringComparison.OrdinalIgnoreCase) ? StoredBackupKind.Automatic
                : file.Name.StartsWith(SafetyPrefix, StringComparison.OrdinalIgnoreCase) && file.Name.EndsWith(".db", StringComparison.OrdinalIgnoreCase) ? StoredBackupKind.SafetyCopy : null;
            if (kind is { } known) found.Add(new(file.FullName, known, file.LastWriteTime, file.Length));
        }
        return found.OrderByDescending(backup => backup.WrittenAt).ThenByDescending(backup => backup.Path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>Deletes the oldest automatic packages and the oldest catalog copies beyond <paramref name="keep"/> of each kind;
    /// returns the deleted paths.</summary>
    public static IReadOnlyList<string> Prune(string folder, int keep)
    {
        keep = Math.Max(1, keep);
        var deleted = new List<string>();
        foreach (var group in List(folder).GroupBy(backup => backup.Kind))
            foreach (var old in group.Skip(keep))
            {
                try
                {
                    FileAccessPolicy.Current.EnsureWritable(old.Path);
                    File.Delete(old.Path);
                    var keys = CatalogEncryption.KeyFilePath(old.Path);
                    if (File.Exists(keys)) File.Delete(keys);
                    deleted.Add(old.Path);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        return deleted;
    }
}

public sealed partial class CatalogRepository
{
    private const string BackupScheduleKey = "backup-schedule";

    public BackupSchedule GetBackupSchedule()
    {
        lock (gate)
        {
            ThrowIfDisposed();
            using var command = Command("SELECT document FROM ui_state WHERE id=$id", null, ("$id", BackupScheduleKey));
            return command.ExecuteScalar() is string json ? JsonSerializer.Deserialize<BackupSchedule>(json, Json) ?? new() : new();
        }
    }

    public void SaveBackupSchedule(BackupSchedule schedule)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        if (schedule.IntervalDays < 0 || schedule.Keep < 1) throw new ArgumentOutOfRangeException(nameof(schedule));
        lock (gate)
        {
            ThrowIfDisposed();
            using var command = Command("INSERT INTO ui_state(id,document) VALUES($id,$json) ON CONFLICT(id) DO UPDATE SET document=excluded.document", null,
                ("$id", BackupScheduleKey), ("$json", JsonSerializer.Serialize(schedule, Json)));
            command.ExecuteNonQuery();
        }
    }

    /// <summary>Writes an automatic backup package into the schedule's folder and removes the oldest ones beyond its count.</summary>
    public async Task<CatalogBackupResult> CreateAutomaticBackupAsync(string libraryDirectory, CancellationToken token = default, IProgress<CatalogBackupProgress>? progress = null)
    {
        var schedule = GetBackupSchedule(); var folder = schedule.FolderOf(libraryDirectory);
        Directory.CreateDirectory(folder);
        var path = BackupRotation.NewAutomaticPath(folder, DateTime.Now);
        if (File.Exists(path)) path = Path.Combine(folder, Path.GetFileNameWithoutExtension(path) + "-" + Guid.NewGuid().ToString("N")[..6] + BackupRotation.PackageExtension);
        var result = await CreateBackupAsync(path, schedule.IncludeMedia, token, progress);
        BackupRotation.Prune(folder, schedule.Keep);
        return result;
    }
}
