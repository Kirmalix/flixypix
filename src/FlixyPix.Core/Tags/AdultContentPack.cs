using System.Security.Cryptography;
using System.Text.Json;

namespace FlixyPix.Core;

public sealed record ContentPackPreview(TagPackagePlan Tags, bool ChangeRoles,
    IReadOnlyDictionary<string, string> SourceHashes, long CatalogChanges, long CatalogVersion);
public sealed record ContentPackUndo(TagChangeUndo Changes, long ChangesStamp, long VersionStamp);
public sealed record ContentPackResult(int Entries, ContentPackUndo? Undo);

public sealed partial class CatalogRepository
{
    /// <summary>Translated genres come from <paramref name="root"/><c>/tag-vocabularies</c>. Performers, studios and the
    /// untranslated genres ("original") are read from <paramref name="programFile"/> of an installed original, because
    /// FlixyPix does not distribute these lists.</summary>
    public ContentPackPreview PreviewAdultContentPack(string root, string? programFile, string language, bool genres, bool performers, bool studios,
        bool changeRoles = false)
    {
        if (!new[] { "de", "en", "fr", "es", "it", "pt", "nl", "pl", "ja", "ru", "original" }.Contains(language))
            throw new ArgumentException("Unknown package language.");
        var rows = new List<TagPackageRow>();
        var sources = new Dictionary<string, string>();
        string Hash(string path) => sources[path] = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
        void Read(string path)
        {
            Hash(path);
            var parsed = TagPackages.Parse(TagPackages.Decode(File.ReadAllBytes(path)));
            if (parsed.Warnings.Count > 0) throw new InvalidDataException(string.Join("\n", parsed.Warnings));
            foreach (var row in parsed.Rows) rows.Add(row with { Line = rows.Count + 1 });
        }
        IReadOnlyDictionary<string, IReadOnlyList<string>>? original = null;
        void ReadOriginal(string category, TagRole role)
        {
            if (programFile == null) throw new FileNotFoundException(T("No installation of EazyFlixPix 4 was found."));
            if (original == null) { Hash(programFile); original = OriginalPresets.ReadAdult(programFile); }
            foreach (var name in original[category]) rows.Add(new(rows.Count + 1, name, category, role, [], null, null));
        }
        if (genres)
        {
            if (language == "original") ReadOriginal(OriginalPresets.Genres, TagRole.Tag);
            else Read(Path.Combine(root, "tag-vocabularies", language + TagPackages.Extension));
        }
        if (performers) ReadOriginal(OriginalPresets.Performers, TagRole.Performer);
        if (studios) ReadOriginal(OriginalPresets.Studios, TagRole.Studio);
        var package = new TagPackage(rows, []);
        (long Changes, long DataVersion) stamp;
        lock (gate) stamp = ChangeStamp();
        var plan = PlanTagPackage(package, allowRoleChanges: changeRoles);
        lock (gate)
            if (ChangeStamp() != stamp) throw new InvalidOperationException("Catalog changed; preview again.");
        return new(plan, changeRoles, sources, stamp.Changes, stamp.DataVersion);
    }

    public ContentPackResult ApplyAdultContentPack(ContentPackPreview preview, IReadOnlyCollection<int> lines, CancellationToken token = default)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            var directory = GetDirectory();
            foreach (var source in preview.SourceHashes)
            {
                using var stream = File.OpenRead(source.Key);
                if (Convert.ToHexStringLower(SHA256.HashData(stream)) != source.Value) throw new InvalidDataException("Package changed; preview again.");
            }
            token.ThrowIfCancellationRequested();
            using var tx = connection.BeginTransaction();
            using var changesCommand = Command("SELECT total_changes()", tx);
            using var versionCommand = Command("PRAGMA data_version", tx);
            var currentStamp = (Changes: Convert.ToInt64(changesCommand.ExecuteScalar()), DataVersion: Convert.ToInt64(versionCommand.ExecuteScalar()));
            if (currentStamp.Changes != preview.CatalogChanges || currentStamp.DataVersion != preview.CatalogVersion)
                throw new InvalidOperationException("Catalog changed; preview again.");
            var before = SnapshotTagChange([], tx);
            var originalEntries = ReadPreferences(tx).TagVocabulary.ToDictionary(e => e.Category + "\0" + e.Tag, e => JsonSerializer.Serialize(e, Json), StringComparer.OrdinalIgnoreCase);
            ApplyTagPackageCore(preview.Tags, lines, tx, preview.ChangeRoles, directory);
            var entries = ReadPreferences(tx).TagVocabulary;
            var changes = UndoBetween(before, SnapshotTagChange([], tx));
            token.ThrowIfCancellationRequested(); tx.Commit();
            var stamp = ChangeStamp();
            return new(entries.Count(e => originalEntries.GetValueOrDefault(e.Category + "\0" + e.Tag) != JsonSerializer.Serialize(e, Json)),
                changes.IsEmpty ? null : new(changes, stamp.Changes, stamp.DataVersion));
        }
    }

    public bool UndoContentPack(ContentPackUndo undo)
    {
        lock (gate)
        {
            // Refuse any intervening catalog write, including new media references.
            var stamp = ChangeStamp();
            if (stamp.Changes != undo.ChangesStamp || stamp.DataVersion != undo.VersionStamp) return false;
            var result = UndoTagChange(undo.Changes);
            if (result) FlushReleasedAssets();
            return result;
        }
    }
}
