using System.Security.Cryptography;
using System.Text.Json;

namespace FlixyPix.Core;

public sealed record VerifiedMigrationResult(bool Passed, bool SourceUnchanged, bool RepeatIsNoOp,
    bool PortableRoundTripVerified, bool BackupRestoreVerified, long MediaCount, MigrationReport Migration,
    IReadOnlyList<string> ProjectionErrors);

/// <summary>Provider-free end-to-end migration audit. No media probes or external-file backup reads.</summary>
public static class VerifiedMigration
{
    public static async Task<VerifiedMigrationResult> RunAsync(string exchangePath, string newOutputDirectory, string sourceId,
        CancellationToken cancellationToken = default)
    {
        var input = Path.GetFullPath(exchangePath); var target = Path.GetFullPath(newOutputDirectory);
        FileAccessPolicy.Current.EnsureWritable(target);
        if (Directory.Exists(target) || File.Exists(target)) throw new IOException("Migration audit requires a new destination.");
        var parent = Path.GetDirectoryName(target) ?? throw new ArgumentException("Missing parent directory.");
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException(parent);
        var work = Path.Combine(parent, ".flixypix-audit-" + Guid.NewGuid().ToString("N"));
        FileAccessPolicy.Current.EnsureWritable(work); Directory.CreateDirectory(work);
        var initialHash = Hash(input); var library = Path.Combine(work, "Library"); Directory.CreateDirectory(library);
        // Failed work is retained in its distinctly named private staging folder for diagnosis.
        using (var catalog = new CatalogRepository(Path.Combine(library, "catalog.db")))
        {
            var options = new LegacyImportOptions { SourceId = sourceId };
            var imported = await Task.Run(() => catalog.ImportLegacyJsonl(input, options, cancellationToken), cancellationToken);
            if (imported.Migration is not { } migration) throw new InvalidDataException("Import failed without a migration report.");
            catalog.ExportMigrationReport(migration.Batch, Path.Combine(work, "migration-report.json"));
            if (!catalog.CheckIntegrity(cancellationToken).IsValid) throw new InvalidDataException("Migrated catalog failed integrity checks.");
            var repeat = await Task.Run(() => catalog.ImportLegacyJsonl(input, options, cancellationToken), cancellationToken);
            var noOp = repeat.Migration?.AlreadyImported == true;
            var portable = Path.Combine(work, "catalog.json"); catalog.Export(portable);
            var roundtripDirectory = Path.Combine(work, "RoundTrip"); Directory.CreateDirectory(roundtripDirectory);
            bool roundtrip;
            using (var restored = new CatalogRepository(Path.Combine(roundtripDirectory, "catalog.db")))
            {
                var result = restored.ImportJson(portable, cancellationToken: cancellationToken);
                var exported = Path.Combine(roundtripDirectory, "catalog.json"); restored.Export(exported);
                roundtrip = result.Errors.Count == 0 && restored.CheckIntegrity(cancellationToken).IsValid && Hash(portable) == Hash(exported);
            }
            var package = Path.Combine(work, "catalog.flixypixbackup");
            await catalog.CreateBackupAsync(package, false, cancellationToken, includeCachedThumbnails: false);
            var backup = await CatalogRepository.RestoreBackupAsync(package, Path.Combine(work, "BackupRestore"), cancellationToken);
            bool backupVerified;
            using (var restored = new CatalogRepository(backup.DatabasePath))
            {
                var exported = Path.Combine(work, "backup-restored.json"); restored.Export(exported);
                backupVerified = restored.CheckIntegrity(cancellationToken).IsValid && Hash(portable) == Hash(exported);
            }
            var unchanged = initialHash == Hash(input);
            var report = new VerifiedMigrationResult(unchanged && noOp && roundtrip && backupVerified && imported.Errors.Count == 0,
                unchanged, noOp, roundtrip, backupVerified, catalog.QueryPage(new() { Limit = 1 }).TotalCount, migration, imported.Errors);
            var document = new { format = "flixypix-migration-audit", version = 1, result = report, mediaFilesRead = false };
            File.WriteAllText(Path.Combine(work, "audit-result.json"), JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true }));
            if (!unchanged || !noOp || !roundtrip || !backupVerified) throw new InvalidDataException("Migration audit failed; inspect its private staging directory.");
            // Close SQLite before publishing the complete directory on Windows.
            catalog.Dispose(); cancellationToken.ThrowIfCancellationRequested();
            FileAccessPolicy.Current.EnsureWritable(work); FileAccessPolicy.Current.EnsureWritable(target); Directory.Move(work, target);
            return report;
        }
    }
    private static string Hash(string path) { using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); return Convert.ToHexStringLower(SHA256.HashData(file)); }
}
